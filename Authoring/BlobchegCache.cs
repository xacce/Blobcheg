using System;
using System.Collections.Generic;
using UnityEditor;

namespace Blobcheg.Authoring
{
    /// <summary>
    /// What the rebuild remembers from last time: the list of nodes, their records in bytes and their
    /// carriers. It lives in memory and dies together with the domain — it has no reason to outlive a
    /// reload, and a file on disk would be a second source of truth about what lies in the assets
    /// anyway.
    ///
    /// There is one point to it: a rebuild is obliged to cost as much as changed, not as much as there
    /// are nodes in the project. Without the cache every import of any node walks the project again,
    /// calls Write on all of them and reads the carriers of all of them — on 10,000 nodes that is
    /// seconds on every save.
    ///
    /// A node becomes dirty in three ways: it was reimported (the same hook that starts the rebuild sees
    /// that), it is edited in the inspector and is dirty in memory, or it was handed a different id than
    /// the one it had at write time — a node may have put its own id straight into the record.
    ///
    /// What the cache CANNOT do: notice an edit of a foreign asset the node depends on. The rebuild did
    /// not fire on that before either — the hook only runs on an import of the node itself — so the
    /// cache makes nothing worse here and does not pretend to do more.
    /// </summary>
    static class BlobchegCache
    {
        /// <summary>One record of a node: the same thing the node handed to the collector.</summary>
        public struct Written
        {
            public Type Domain;
            public string RecordType;
            public uint TypeHash;
            public byte[] Bytes;
        }

        public sealed class Entry
        {
            public string Path;

            /// <summary>The identity of a node: its path changes, its GUID does not.</summary>
            public string Guid;

            public BlobchegNodeSo Node;

            /// <summary>Write has to be called again.</summary>
            public bool Dirty = true;

            /// <summary>What the node wrote last time. <c>null</c> means it never wrote.</summary>
            public List<Written> Records;

            /// <summary>The ids it wrote that with: by the index of <see cref="BlobchegRouters.All"/>.</summary>
            public uint[] IdsAtWrite;

            /// <summary>The carriers of the node, read from the asset. <c>null</c> means unread.</summary>
            public List<BlobchegRefSo> Refs;

            public List<BlobchegIdSo> Ids;
        }

        static readonly List<Entry> Entries = new List<Entry>();
        static readonly Dictionary<string, Entry> ByPath = new Dictionary<string, Entry>(StringComparer.Ordinal);

        static bool _filled;

        /// <summary>The nodes in path order. The first run walks the project, after that the list is edited spot by spot.</summary>
        public static IReadOnlyList<Entry> Fill()
        {
            if (_filled)
            {
                // The asset may have been destroyed past the hook (by rolling back a version on disk, for
                // instance). An empty wrapper in the list is a silently skipped node, so the list is
                // gathered again.
                foreach (var entry in Entries)
                {
                    if (entry.Node == null)
                    {
                        Drop();
                        break;
                    }
                }
            }

            if (_filled)
                return Entries;

            // The list is sorted in one go and filled into the tail: inserting by searching for a place
            // would turn filling on 10,000 nodes into a quadratic. The GUID is taken from the walk rather
            // than asked for again: it is already computed there.
            var found = BlobchegBuild.FindNodesByGuid();
            var byPath = new List<KeyValuePair<string, Entry>>(found.Count);

            foreach (var pair in found)
            {
                var path = AssetDatabase.GetAssetPath(pair.Value);
                byPath.Add(new KeyValuePair<string, Entry>(path,
                    new Entry { Path = path, Guid = pair.Key, Node = pair.Value }));
            }

            byPath.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key));

            foreach (var pair in byPath)
            {
                Entries.Add(pair.Value);
                ByPath[pair.Key] = pair.Value;
            }

            _filled = true;
            return Entries;
        }

        /// <summary>
        /// The carriers of a node: from memory if the rebuild has already read them, otherwise from the
        /// asset. The pickers live on this — they need the whole project at once, and asking the asset
        /// database once per node on 10,000 nodes costs seconds on every opening of a field.
        /// </summary>
        public static IEnumerable<BlobchegRefSo> RefsOf(Entry entry)
            => entry.Refs ?? BlobchegBuild.RefsOf(entry.Node);

        public static IEnumerable<BlobchegIdSo> IdsOf(Entry entry)
            => entry.Ids ?? BlobchegBuild.IdsOf(entry.Node);

        /// <summary>Forget everything. Called by the pre-build gate and by edits after which the cache cannot be trusted.</summary>
        public static void Drop()
        {
            Entries.Clear();
            ByPath.Clear();
            _filled = false;
        }

        /// <summary>What the import brought. The paths come from <c>OnPostprocessAllAssets</c>.</summary>
        public static void Touch(string[] imported, string[] deleted, string[] moved, string[] movedFrom)
        {
            if (!_filled)
                return;

            foreach (var path in deleted)
                Remove(path);

            // A move is taken as a PAIR and not as two independent lists: the asset itself went nowhere,
            // and re-reading it at the new path is not allowed — the asset database does not know about
            // the rename in this run yet, and the node would quietly drop out of the rebuild together
            // with its record.
            for (var i = 0; i < moved.Length; i++)
            {
                if (i < movedFrom.Length && ByPath.TryGetValue(movedFrom[i], out var entry))
                {
                    Rekey(entry, moved[i]);
                    continue;
                }

                if (i < movedFrom.Length)
                    Remove(movedFrom[i]);

                Mark(moved[i]);
            }

            foreach (var path in imported)
                Mark(path);
        }

        /// <summary>The same asset at a new path: the entry stays, its place in the list is recomputed.</summary>
        static void Rekey(Entry entry, string path)
        {
            ByPath.Remove(entry.Path);
            Entries.Remove(entry);

            entry.Path = path;
            entry.Dirty = true;

            var at = Entries.Count;
            for (var i = 0; i < Entries.Count; i++)
            {
                if (string.CompareOrdinal(Entries[i].Path, path) > 0)
                {
                    at = i;
                    break;
                }
            }

            Entries.Insert(at, entry);
            ByPath[path] = entry;
        }

        static void Mark(string path)
        {
            if (ByPath.TryGetValue(path, out var entry))
            {
                entry.Dirty = true;
                return;
            }

            if (!path.EndsWith(".asset", StringComparison.OrdinalIgnoreCase))
                return;

            var type = AssetDatabase.GetMainAssetTypeAtPath(path);
            if (type == null || !typeof(BlobchegNodeSo).IsAssignableFrom(type))
                return;

            var node = AssetDatabase.LoadAssetAtPath<BlobchegNodeSo>(path);
            if (node != null)
                Put(path, node);
        }

        static void Remove(string path)
        {
            if (!ByPath.TryGetValue(path, out var entry))
                return;

            ByPath.Remove(path);
            Entries.Remove(entry);
        }

        /// <summary>The order of the nodes is by path: the same order a full walk hands them out in.</summary>
        static void Put(string path, BlobchegNodeSo node)
        {
            var guid = AssetDatabase.AssetPathToGUID(path);

            // A node created between full walks is known only to the cache. The walk has to be told about
            // it: otherwise it disappears from it unnoticed — there will be nothing to compare against.
            BlobchegBuild.Remember(guid);

            var entry = new Entry { Path = path, Guid = guid, Node = node };

            var at = Entries.Count;
            for (var i = 0; i < Entries.Count; i++)
            {
                if (string.CompareOrdinal(Entries[i].Path, path) > 0)
                {
                    at = i;
                    break;
                }
            }

            Entries.Insert(at, entry);
            ByPath.Add(path, entry);
        }
    }
}
