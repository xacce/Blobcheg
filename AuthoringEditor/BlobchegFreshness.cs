using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using UnityEditor;
using UnityEngine;

namespace Blobcheg.Authoring
{
    [Serializable]
    sealed class BlobchegNodeState
    {
        public string guid;
        public string path;
        public string hash;
        public string reach;
        public string[] refs;
        public string[] deps;
        public string numbers;
    }

    [Serializable]
    sealed class BlobchegIndex
    {
        public int version;
        public bool debug;
        public string assemblies;
        public string[] search;
        public string[] files;
        public List<BlobchegNodeState> nodes = new List<BlobchegNodeState>();
    }

    // What the bases were built from, in hashes. Nothing here reacts to an import; readers ask.
    public static class BlobchegFreshness
    {
        const int Version = 3;

        public static string FilePath => Path.Combine(BlobchegEditorOutput.MetaDirectory, "index.json");

        static BlobchegIndex _stored;
        static bool _read;
        static string _assemblies;
        static int _epoch;
        static int _checked = -1;
        static int _thread;

        [InitializeOnLoadMethod]
        static void Install()
        {
            _thread = Thread.CurrentThread.ManagedThreadId;
            BlobchegFileVersions.Watch = () => Ensure("a live world watches its file");
        }

        internal static bool Checked => _checked == _epoch;

        public static void Invalidate() => _epoch++;

        // An .asset the index does not know yet: the search index lags behind an import by whole seconds.
        internal static void Suspect(string path)
        {
            if (path.EndsWith(".asset", StringComparison.OrdinalIgnoreCase))
                Suspects.Add(path);
        }

        static readonly HashSet<string> Suspects = new HashSet<string>(StringComparer.Ordinal);

        static HashSet<string> _nodes;
        static DateTime _nodesAt;

        // Asked by an import, in a worker process too: the index file on disk is the whole answer.
        internal static bool IsNode(string guid)
        {
            if (string.IsNullOrEmpty(guid))
                return false;

            var at = File.Exists(FilePath) ? File.GetLastWriteTimeUtc(FilePath) : DateTime.MinValue;
            if (_nodes == null || at != _nodesAt)
            {
                _nodesAt = at;
                _nodes = new HashSet<string>(StringComparer.Ordinal);

                var index = at == DateTime.MinValue ? null : Read();
                if (index != null)
                {
                    foreach (var node in index.nodes)
                        _nodes.Add(node.guid);
                }
            }

            return _nodes.Contains(guid);
        }

        // Where the nodes are without a sweep over the project: the index and what was imported since.
        internal static bool Candidates(List<string> guids, List<string> paths)
        {
            var index = Load();
            if (index == null)
                return false;

            foreach (var node in index.nodes)
                guids.Add(node.guid);

            paths.AddRange(Suspects);
            return true;
        }

        public static void Ensure(string trigger)
        {
            if (_checked == _epoch || !Answerable())
                return;

            _checked = _epoch;

            try
            {
                var dirty = Diverged(out var why);
                if (why == null)
                    return;

                BlobchegBuild.Rebuild(dirty, why + (trigger == null ? string.Empty : ", " + trigger));
            }
            catch (BlobchegTransientException e)
            {
                // The layout is keyed by guid, so the bases on disk are right until it settles.
                _checked = -1;
                BlobchegProfile.Say("the check waits for the asset database: " + e.Message);
            }
        }

        public static IReadOnlyCollection<string> DirtyNow() => Diverged(out _);

        // Why a rebuild would run right now, in the words of the log: for the window and for a probe.
        public static string Explain()
        {
            HashSet<string> dirty;
            string why;

            try
            {
                dirty = Diverged(out why);
            }
            catch (BlobchegTransientException e)
            {
                return e.Message;
            }

            if (why == null)
                return "the bases stand for the assets";

            var text = new StringBuilder(why);

            if (dirty != null)
            {
                foreach (var guid in dirty)
                    text.Append("\n  ").Append(AssetDatabase.GUIDToAssetPath(guid));
            }

            return text.ToString();
        }

        // Everything the layout is a function of, in one hash: for RegisterCustomDependency.
        public static Hash128 Key
        {
            get
            {
                var index = Load();
                if (index == null)
                    return default;

                var text = new StringBuilder();
                text.Append(index.version).Append(' ').Append(index.debug).Append(' ').Append(index.assemblies);

                foreach (var node in index.nodes)
                    text.Append(' ').Append(node.guid).Append(' ').Append(node.hash);

                return HashOfText(text.ToString());
            }
        }

        // Inside an import the bases on disk answer; the next reader outside it rebuilds.
        static bool Answerable()
            => !BlobchegBuild.Building
               && _thread != 0 && Thread.CurrentThread.ManagedThreadId == _thread
               && !AssetDatabase.IsAssetImportWorkerProcess() && !EditorApplication.isUpdating;

        // A null reason means the bases are current; a null set with a reason means all nodes.
        static HashSet<string> Diverged(out string why)
        {
            using var _ = BlobchegProfile.Section("freshness");

            var index = Load();
            why = null;

            if (index == null)
            {
                why = "the bases have not been assembled in this checkout";
                return null;
            }

            if (index.version != Version)
            {
                why = "the index is of another version";
                return null;
            }

            if (!string.Equals(index.assemblies, Assemblies(), StringComparison.Ordinal))
            {
                why = "the code changed";
                return null;
            }

            // Contour, lost file, node set: the files are laid out anew, the records keep their bytes.
            string set = null;

            if (index.debug != BlobchegBuild.WithDebug)
                set = "the debug contour is wanted the other way round";

            var missing = Missing(index);
            if (missing != null)
                set = "the file '" + missing + "' is not on disk";

            if (set == null && !Same(index.search, Search()))
                set = "the set of nodes changed";

            var known = new HashSet<string>(StringComparer.Ordinal);
            foreach (var node in index.nodes)
                known.Add(node.guid);

            var born = Born(known);
            if (born != null && set == null)
                set = "the node '" + born + "' is new to the index";

            var dirty = new HashSet<string>(StringComparer.Ordinal);

            foreach (var node in index.nodes)
            {
                if (!string.Equals(node.hash, HashOf(node.path, node.deps), StringComparison.Ordinal))
                    dirty.Add(node.guid);
            }

            if (set != null)
                why = set;
            else if (dirty.Count > 0)
                why = dirty.Count + " node(s) changed";

            return dirty;
        }

        // A node whose asset was imported while the index does not know it, or null.
        static string Born(HashSet<string> known)
        {
            foreach (var path in Suspects)
            {
                var type = AssetDatabase.GetMainAssetTypeAtPath(path);
                if (type == null || !typeof(BlobchegNodeSo).IsAssignableFrom(type))
                    continue;

                var guid = AssetDatabase.AssetPathToGUID(path);
                if (!string.IsNullOrEmpty(guid) && !known.Contains(guid))
                    return path;
            }

            return null;
        }

        internal static void Publish(IReadOnlyList<BlobchegNodeEntry> entries)
        {
            using var _ = BlobchegProfile.Section("freshness: the index");

            var index = new BlobchegIndex
            {
                version = Version,
                debug = BlobchegBuild.WithDebug,
                assemblies = Assemblies(),
                search = Search(),
                files = Files(),
            };

            var was = Load();

            foreach (var entry in entries)
            {
                var prior = was == null ? null : StateOf(was, entry.Guid);
                string[] refs;
                string[] deps;

                if (!entry.Dirty && prior?.deps != null)
                {
                    refs = prior.refs;
                    deps = prior.deps;
                }
                else
                {
                    // Nothing the references came from was reimported: a code edit does not move them.
                    refs = prior?.refs != null && prior.path == entry.Path && !EditorUtility.IsDirty(entry.Node)
                           && string.Equals(prior.reach, HashOf(entry.Path, prior.refs), StringComparison.Ordinal)
                        ? prior.refs
                        : References(entry.Path);
                    deps = WithExtra(entry, refs);
                }

                index.nodes.Add(new BlobchegNodeState
                {
                    guid = entry.Guid,
                    path = entry.Path,
                    refs = refs,
                    deps = deps,
                    numbers = prior?.numbers,
                });
            }

            index.nodes.Sort((a, b) => string.CompareOrdinal(a.guid, b.guid));
            _stored = index;
            _read = true;
            Suspects.Clear();

            // On disk before the reimport: a node asset learns that it is a node from this very file.
            Write(index);
            Declare(index);
            Seal(index);
        }

        internal static void Redeclare()
        {
            var index = Load();
            if (index == null)
                return;

            Declare(index);
            Seal(index);
        }

        static void Seal(BlobchegIndex index)
        {
            foreach (var node in index.nodes)
            {
                node.reach = HashOf(node.path, node.refs);
                node.hash = HashOf(node.path, node.deps);
            }

            Write(index);
        }

        static void Write(BlobchegIndex index)
        {
            Directory.CreateDirectory(BlobchegEditorOutput.MetaDirectory);
            File.WriteAllText(FilePath, JsonUtility.ToJson(index, true));
        }

        // Hashes are taken only after the reimport: the numbers are a dependency of the node asset too.
        static void Declare(BlobchegIndex index)
        {
            var were = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var node in index.nodes)
            {
                if (node.numbers != null)
                    were[node.guid] = node.numbers;
            }

            // An unsaved node is left undeclared until its own Ctrl+S: a reimport would write it to disk.
            var are = new Dictionary<string, string>(StringComparer.Ordinal);
            var fresh = BlobchegDependencies.Publish(were, are, Unsaved);

            foreach (var node in index.nodes)
                node.numbers = are.TryGetValue(node.guid, out var value) ? value : null;

            if (fresh.Count == 0)
                return;

            using var _ = BlobchegProfile.Section("dependencies: the reimport");

            try
            {
                AssetDatabase.StartAssetEditing();
                try
                {
                    foreach (var guid in fresh)
                    {
                        var path = AssetDatabase.GUIDToAssetPath(guid);
                        if (!string.IsNullOrEmpty(path))
                            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
                    }
                }
                finally
                {
                    AssetDatabase.StopAssetEditing();
                }
            }
            catch (Exception e)
            {
                // Not reimported means not declared: the next attempt is obliged to take these again.
                var retry = new HashSet<string>(fresh, StringComparer.Ordinal);
                foreach (var node in index.nodes)
                {
                    if (retry.Contains(node.guid))
                        node.numbers = null;
                }

                BlobchegDependencies.Retry(e);
            }
        }

        static bool Unsaved(string guid)
        {
            var path = AssetDatabase.GUIDToAssetPath(guid);
            return !string.IsNullOrEmpty(path) && AssetDatabase.IsMainAssetAtPathLoaded(path)
                   && EditorUtility.IsDirty(AssetDatabase.LoadMainAssetAtPath(path));
        }

        static BlobchegNodeState StateOf(BlobchegIndex index, string guid)
        {
            foreach (var node in index.nodes)
            {
                if (string.Equals(node.guid, guid, StringComparison.Ordinal))
                    return node;
            }

            return null;
        }

        // Scripts are left out: a code edit reloads the domain and the hash of the assemblies has it.
        static string[] References(string path)
        {
            var found = AssetDatabase.GetDependencies(path, true);
            var refs = new List<string>(found.Length);

            foreach (var dep in found)
            {
                if (!dep.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                    refs.Add(dep);
            }

            return refs.ToArray();
        }

        static string[] WithExtra(BlobchegNodeEntry entry, string[] refs)
        {
            var deps = new List<string>(refs);
            var extra = new List<string>();
            entry.Node.CollectExtraDependencies(extra);

            foreach (var path in extra)
            {
                if (!string.IsNullOrEmpty(path) && !deps.Contains(path))
                    deps.Add(path);
            }

            deps.Sort(StringComparer.Ordinal);
            return deps.ToArray();
        }

        static string HashOf(string path, string[] deps)
        {
            var text = new StringBuilder(path);
            text.Append(' ').Append(AssetDatabase.GetAssetDependencyHash(path));

            if (deps != null)
            {
                foreach (var dep in deps)
                    text.Append(' ').Append(dep).Append(' ').Append(AssetDatabase.GetAssetDependencyHash(dep));
            }

            return HashOfText(text.ToString()).ToString();
        }

        static Hash128 HashOfText(string text)
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            var hash = BlobchegHash.Of(bytes, 0, bytes.Length);
            return new Hash128((uint)(hash >> 32), (uint)hash, 0, 0);
        }

        // Stands for Write, the rift ENV, the codegen and the record structs at once. Once per domain.
        static string Assemblies()
        {
            if (_assemblies != null)
                return _assemblies;

            var directory = Path.Combine(
                Path.GetDirectoryName(Application.dataPath) ?? string.Empty, "Library", "ScriptAssemblies");

            if (!Directory.Exists(directory))
                return _assemblies = "none";

            var files = Directory.GetFiles(directory, "*.dll");
            Array.Sort(files, StringComparer.Ordinal);

            var text = new StringBuilder();

            foreach (var file in files)
            {
                var bytes = File.ReadAllBytes(file);
                text.Append(Path.GetFileName(file)).Append(' ')
                    .Append(BlobchegHash.Of(bytes, 0, bytes.Length).ToString("X16")).Append(' ');
            }

            return _assemblies = HashOfText(text.ToString()).ToString();
        }

        // The search index lags behind an import, so it is only ever compared against itself.
        static string[] Search()
        {
            var guids = AssetDatabase.FindAssets("t:" + nameof(BlobchegNodeSo));
            Array.Sort(guids, StringComparer.Ordinal);
            return guids;
        }

        static string[] Files()
        {
            var directory = BlobchegBuild.OutputDirectory;
            if (!Directory.Exists(directory))
                return Array.Empty<string>();

            var files = Directory.GetFiles(directory, "*" + BlobchegNaming.Extension);

            for (var i = 0; i < files.Length; i++)
                files[i] = Path.GetFileName(files[i]);

            Array.Sort(files, StringComparer.Ordinal);
            return files;
        }

        static string Missing(BlobchegIndex index)
        {
            if (!File.Exists(BlobchegStampStore.FilePath))
                return "stamps.json";

            if (index.files == null)
                return null;

            foreach (var file in index.files)
            {
                if (!File.Exists(Path.Combine(BlobchegBuild.OutputDirectory, file)))
                    return file;
            }

            return null;
        }

        static bool Same(string[] were, string[] are)
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

        static BlobchegIndex Load()
        {
            if (_read)
                return _stored;

            _read = true;
            _stored = File.Exists(FilePath) ? Read() : null;
            return _stored;
        }

        static BlobchegIndex Read()
        {
            try
            {
                return JsonUtility.FromJson<BlobchegIndex>(File.ReadAllText(FilePath));
            }
            catch (Exception e)
            {
                // An unreadable derived file breaks nothing: the bases are assembled from scratch.
                Debug.LogWarning($"Blobcheg: the index at '{FilePath}' could not be read ({e.Message})");
                return null;
            }
        }

        internal static void Forget()
        {
            _stored = null;
            _read = false;
            _assemblies = null;
            _checked = -1;
        }
    }

    // An import does no work here: it voids the memo and writes down which .asset paths it touched.
    sealed class BlobchegImports : AssetPostprocessor
    {
        static void OnPostprocessAllAssets(string[] imported, string[] deleted, string[] moved,
            string[] movedFrom)
        {
            BlobchegFreshness.Invalidate();

            foreach (var path in imported)
                BlobchegFreshness.Suspect(path);

            foreach (var path in moved)
                BlobchegFreshness.Suspect(path);
        }
    }
}
