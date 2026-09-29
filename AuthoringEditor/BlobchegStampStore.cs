using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Blobcheg.Authoring
{
    [Serializable]
    sealed class BlobchegRecordStamp
    {
        public string guid;
        public string domainName;
        public string recordType;
        public uint offset;

        [SerializeField] long revision;

        public ulong Revision
        {
            get => unchecked((ulong)revision);
            set => revision = unchecked((long)value);
        }
    }

    [Serializable]
    sealed class BlobchegIdStamp
    {
        public string guid;
        public string routerName;
        public uint id;
    }

    // The numbers of every carrier of the project in one derived file next to the bases.
    public static class BlobchegStampStore
    {
        const int Version = 1;

        [Serializable]
        sealed class Table
        {
            public int version;
            public List<BlobchegRecordStamp> records = new List<BlobchegRecordStamp>();
            public List<BlobchegIdStamp> ids = new List<BlobchegIdStamp>();
        }

        static SortedDictionary<string, BlobchegRecordStamp> _records;
        static SortedDictionary<string, BlobchegIdStamp> _ids;

        static readonly HashSet<string> SyncedRecords = new HashSet<string>(StringComparer.Ordinal);
        static readonly HashSet<string> SyncedIds = new HashSet<string>(StringComparer.Ordinal);

        static bool _dirty;

        public static string FilePath => Path.Combine(BlobchegEditorOutput.MetaDirectory, "stamps.json");

        [InitializeOnLoadMethod]
        static void Install() => BlobchegStamps.Source = new AssetSource();

        internal static void BeginRebuild()
        {
            Load();
            SyncedRecords.Clear();
            SyncedIds.Clear();
        }

        internal static bool SyncRecord(string guid, string domainName, string recordType, uint offset,
            ulong revision)
        {
            var key = Key(guid, domainName);
            SyncedRecords.Add(key);

            _records.TryGetValue(key, out var stamp);

            if (stamp != null
                && stamp.offset == offset
                && stamp.Revision == revision
                && string.Equals(stamp.recordType, recordType, StringComparison.Ordinal))
                return false;

            if (stamp == null)
                _records.Add(key, stamp = new BlobchegRecordStamp { guid = guid, domainName = domainName });

            stamp.recordType = recordType;
            stamp.offset = offset;
            stamp.Revision = revision;
            _dirty = true;
            return true;
        }

        internal static bool SyncId(string guid, string routerName, uint id)
        {
            var key = Key(guid, routerName);
            SyncedIds.Add(key);

            _ids.TryGetValue(key, out var stamp);

            if (stamp != null && stamp.id == id)
                return false;

            if (stamp == null)
                _ids.Add(key, stamp = new BlobchegIdStamp { guid = guid, routerName = routerName });

            stamp.id = id;
            _dirty = true;
            return true;
        }

        // What the previous rebuild handed out. It decides nothing, it only names who moved.
        internal static bool TryPriorId(string guid, string routerName, out uint id)
        {
            id = BlobchegId.NoneValue;
            if (!Load().TryGetValue(Key(guid, routerName), out var stamp))
                return false;

            id = stamp.id;
            return true;
        }

        internal static int Flush()
        {
            Load();

            var stale = Prune(_records, SyncedRecords) + Prune(_ids, SyncedIds);

            if (_dirty || stale > 0 || !File.Exists(FilePath))
                Write();

            _dirty = false;
            return stale;
        }

        static int Prune<T>(SortedDictionary<string, T> table, HashSet<string> synced)
        {
            var stale = new List<string>();
            foreach (var pair in table)
            {
                if (!synced.Contains(pair.Key))
                    stale.Add(pair.Key);
            }

            foreach (var key in stale)
                table.Remove(key);

            return stale.Count;
        }

        static void Write()
        {
            var carried = new Table { version = Version };
            foreach (var pair in _records)
                carried.records.Add(pair.Value);

            foreach (var pair in _ids)
                carried.ids.Add(pair.Value);

            Directory.CreateDirectory(BlobchegEditorOutput.MetaDirectory);
            File.WriteAllText(FilePath, JsonUtility.ToJson(carried, true));
        }

        static DateTime _loadedAt;

        static SortedDictionary<string, BlobchegIdStamp> Load()
        {
            // A worker process outlives the rebuilds of the main one: it goes by the file, not by memory.
            if (_ids != null && AssetDatabase.IsAssetImportWorkerProcess())
            {
                var at = File.Exists(FilePath) ? File.GetLastWriteTimeUtc(FilePath) : DateTime.MinValue;
                if (at != _loadedAt)
                    _ids = null;
            }

            if (_ids != null)
                return _ids;

            _loadedAt = File.Exists(FilePath) ? File.GetLastWriteTimeUtc(FilePath) : DateTime.MinValue;

            _records = new SortedDictionary<string, BlobchegRecordStamp>(StringComparer.Ordinal);
            _ids = new SortedDictionary<string, BlobchegIdStamp>(StringComparer.Ordinal);

            var read = Read();
            if (read == null)
                return _ids;

            foreach (var stamp in read.records)
            {
                if (stamp != null && !string.IsNullOrEmpty(stamp.guid))
                    _records[Key(stamp.guid, stamp.domainName)] = stamp;
            }

            foreach (var stamp in read.ids)
            {
                if (stamp != null && !string.IsNullOrEmpty(stamp.guid))
                    _ids[Key(stamp.guid, stamp.routerName)] = stamp;
            }

            return _ids;
        }

        static Table Read()
        {
            var path = FilePath;
            if (!File.Exists(path))
                return null;

            Table read;
            try
            {
                read = JsonUtility.FromJson<Table>(File.ReadAllText(path));
            }
            catch (Exception e)
            {
                // An unreadable derived file is no reason to break the editor: the next rebuild writes it.
                Debug.LogWarning($"Blobcheg: the stamps at '{path}' could not be read ({e.Message})");
                return null;
            }

            return read != null && read.version == Version && read.records != null && read.ids != null
                ? read
                : null;
        }

        // One dependency per record and per id: a scene rebakes when the number it baked has moved.
        // What a bake reads of a node: the numbers and the record types, never the record bytes.
        internal static Dictionary<string, Hash128> NodeKeys()
        {
            Load();

            var parts = new Dictionary<string, List<string>>(StringComparer.Ordinal);

            foreach (var pair in _records)
                PartsOf(parts, pair.Value.guid).Add(pair.Key + " " + pair.Value.offset + " " + pair.Value.recordType);

            foreach (var pair in _ids)
                PartsOf(parts, pair.Value.guid).Add(pair.Key + " " + pair.Value.id);

            var keys = new Dictionary<string, Hash128>(parts.Count, StringComparer.Ordinal);
            foreach (var pair in parts)
                keys[pair.Key] = BlobchegDependencies.KeyOf(pair.Value);

            return keys;
        }

        static List<string> PartsOf(Dictionary<string, List<string>> parts, string guid)
        {
            if (!parts.TryGetValue(guid, out var list))
                parts[guid] = list = new List<string>();

            return list;
        }

        static string Key(string guid, string name) => guid + " " + (name ?? string.Empty);

        static string OwnerGuid(UnityEngine.Object carrier)
            => AssetDatabase.TryGetGUIDAndLocalFileIdentifier(carrier, out var guid, out long _)
                ? guid
                : null;

        sealed class AssetSource : IBlobchegStamps
        {
            public bool TryOffset(BlobchegRefSo reference, out uint offset)
            {
                offset = 0;
                var stamp = StampOf(reference);
                if (stamp == null)
                    return false;

                offset = stamp.offset;
                return true;
            }

            public bool TryRecordType(BlobchegRefSo reference, out string recordType)
            {
                recordType = null;
                var stamp = StampOf(reference);
                if (stamp == null)
                    return false;

                recordType = stamp.recordType ?? string.Empty;
                return true;
            }

            public bool TryId(BlobchegIdSo carrier, out uint id)
            {
                id = BlobchegId.NoneValue;

                BlobchegFreshness.Ensure("an id is being asked for");

                var guid = OwnerGuid(carrier);
                if (guid == null)
                    return false;

                Load();
                if (!_ids.TryGetValue(Key(guid, carrier.RouterName), out var stamp))
                    return false;

                id = stamp.id;
                return true;
            }

            static BlobchegRecordStamp StampOf(BlobchegRefSo reference)
            {
                BlobchegFreshness.Ensure("an address is being asked for");

                var guid = OwnerGuid(reference);
                if (guid == null)
                    return null;

                Load();
                if (!_records.TryGetValue(Key(guid, reference.DomainName), out var stamp))
                    return null;

                return stamp;
            }
        }
    }
}
