using System;
using System.Collections.Generic;
using UnityEditor;

namespace Blobcheg.Authoring
{
    struct BlobchegWritten
    {
        public Type Domain;
        public string RecordType;
        public uint TypeHash;
        public byte[] Bytes;
    }

    sealed class BlobchegNodeEntry
    {
        public string Guid;
        public string Path;
        public BlobchegNodeSo Node;

        public bool Dirty;

        public List<BlobchegWritten> Records;

        public uint[] IdsAtWrite;
    }

    // The nodes of one rebuild and what each wrote last time: a rebuild costs as much as changed.
    static class BlobchegNodes
    {
        sealed class Memory
        {
            public List<BlobchegWritten> Records;
            public uint[] Ids;
        }

        static readonly Dictionary<string, Memory> Remembered =
            new Dictionary<string, Memory>(StringComparer.Ordinal);

        public static List<BlobchegNodeEntry> Gather(IReadOnlyCollection<string> dirty)
        {
            var found = BlobchegBuild.FindNodesByGuid();
            var entries = new List<BlobchegNodeEntry>(found.Count);

            var set = dirty as HashSet<string>;
            if (set == null && dirty != null)
                set = new HashSet<string>(dirty, StringComparer.Ordinal);

            foreach (var pair in found)
            {
                var entry = new BlobchegNodeEntry
                {
                    Guid = pair.Key,
                    Path = AssetDatabase.GetAssetPath(pair.Value),
                    Node = pair.Value,
                };

                if (Remembered.TryGetValue(pair.Key, out var memory))
                {
                    entry.Records = memory.Records;
                    entry.IdsAtWrite = memory.Ids;
                }

                // Nothing remembered means nothing to hand over: such a node writes anew whatever asked.
                entry.Dirty = set == null || set.Contains(pair.Key) || entry.Records == null;

                entries.Add(entry);
            }

            entries.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));
            return entries;
        }

        public static void Keep(IReadOnlyList<BlobchegNodeEntry> entries)
        {
            Remembered.Clear();

            foreach (var entry in entries)
            {
                if (entry.Records == null)
                    continue;

                Remembered[entry.Guid] = new Memory
                {
                    Records = entry.Records,
                    Ids = entry.IdsAtWrite,
                };
            }
        }

        public static void Forget() => Remembered.Clear();
    }
}
