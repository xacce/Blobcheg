using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Blobcheg.Authoring
{
    [Serializable]
    public sealed class BlobchegManifest
    {
        public BlobchegFileKind kind;
        public string domainName;
        public string fileName;
        public string builtAt;
        public int recordCount;

        // Members in file order; an empty guid is a hole left by a deleted node.
        public string[] nodeGuids = Array.Empty<string>();

        [SerializeField] long contentHash;

        public bool IsRouter => kind == BlobchegFileKind.Router;

        public ulong ContentHash
        {
            get => unchecked((ulong)contentHash);
            set => contentHash = unchecked((long)value);
        }

        public int NodeCount => nodeGuids?.Length ?? 0;

        public BlobchegNodeSo NodeAt(int index)
        {
            var guid = nodeGuids[index];
            if (string.IsNullOrEmpty(guid))
                return null;

            return AssetDatabase.LoadAssetAtPath<BlobchegNodeSo>(AssetDatabase.GUIDToAssetPath(guid));
        }
    }

    // Every manifest of the project in one derived file next to the bases.
    public static class BlobchegManifests
    {
        const int Version = 1;

        [Serializable]
        sealed class Table
        {
            public int version;
            public List<BlobchegManifest> manifests = new List<BlobchegManifest>();
        }

        static SortedDictionary<string, BlobchegManifest> _byName;
        static readonly HashSet<string> Synced = new HashSet<string>(StringComparer.Ordinal);
        static bool _dirty;

        public static string FilePath => Path.Combine(BlobchegEditorOutput.MetaDirectory, "manifests.json");

        public static IEnumerable<BlobchegManifest> All => Loaded().Values;

        public static BlobchegManifest Of(string name)
            => Loaded().TryGetValue(name ?? string.Empty, out var manifest) ? manifest : null;

        internal static void BeginRebuild()
        {
            Loaded();
            Synced.Clear();
        }

        // Rewritten when anything at all diverged from what was assembled, not only the hash.
        internal static bool Sync(string name, BlobchegFileKind kind, string[] nodeGuids, int recordCount,
            ulong contentHash, bool fileChanged)
        {
            Synced.Add(name);

            var table = Loaded();
            var known = table.TryGetValue(name, out var manifest);
            var fileName = BlobchegNaming.FileName(name);

            if (known
                && !fileChanged
                && manifest.kind == kind
                && manifest.fileName == fileName
                && manifest.recordCount == recordCount
                && manifest.ContentHash == contentHash
                && SameNodes(manifest.nodeGuids, nodeGuids))
                return false;

            if (!known)
            {
                manifest = new BlobchegManifest { domainName = name };
                table.Add(name, manifest);
            }

            manifest.kind = kind;
            manifest.fileName = fileName;
            manifest.recordCount = recordCount;
            manifest.nodeGuids = nodeGuids;
            manifest.ContentHash = contentHash;
            manifest.builtAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            _dirty = true;
            return true;
        }

        // What the rebuild did not touch is a manifest of something the project no longer has.
        internal static int Flush()
        {
            var table = Loaded();
            var stale = new List<string>();

            foreach (var pair in table)
            {
                if (!Synced.Contains(pair.Key))
                    stale.Add(pair.Key);
            }

            foreach (var name in stale)
                table.Remove(name);

            if (_dirty || stale.Count > 0 || !File.Exists(FilePath))
                Write(table);

            _dirty = false;
            return stale.Count;
        }

        static void Write(SortedDictionary<string, BlobchegManifest> table)
        {
            var carried = new Table { version = Version };
            foreach (var pair in table)
                carried.manifests.Add(pair.Value);

            Directory.CreateDirectory(BlobchegEditorOutput.MetaDirectory);
            File.WriteAllText(FilePath, JsonUtility.ToJson(carried, true));
        }

        static SortedDictionary<string, BlobchegManifest> Loaded()
        {
            if (_byName != null)
                return _byName;

            _byName = new SortedDictionary<string, BlobchegManifest>(StringComparer.Ordinal);

            var path = FilePath;
            if (!File.Exists(path))
                return _byName;

            Table read;
            try
            {
                read = JsonUtility.FromJson<Table>(File.ReadAllText(path));
            }
            catch (Exception e)
            {
                // An unreadable derived file is no reason to break the editor: the next rebuild writes it.
                Debug.LogWarning($"Blobcheg: the manifests at '{path}' could not be read ({e.Message})");
                return _byName;
            }

            if (read == null || read.version != Version || read.manifests == null)
                return _byName;

            foreach (var manifest in read.manifests)
            {
                if (manifest != null && !string.IsNullOrEmpty(manifest.domainName))
                    _byName[manifest.domainName] = manifest;
            }

            return _byName;
        }

        static bool SameNodes(string[] were, string[] are)
        {
            if (were == null || are == null || were.Length != are.Length)
                return false;

            for (var i = 0; i < are.Length; i++)
            {
                if (!string.Equals(were[i], are[i], StringComparison.Ordinal))
                    return false;
            }

            return true;
        }
    }
}
