using System;
using System.Collections.Generic;
using Unity.Burst;
using UnityEditor;

namespace Blobcheg.Authoring
{
    // Name and type come from the editor catalog: a file carries only its name hash, unknown is fine.
    public sealed class BlobchegLiveFile
    {
        public ulong Key;
        public string Name;
        public ulong Address;
        public int Length;
        public uint DebugOffset;
        public BlobchegHeader Header;
        public BlobchegFileKind Kind;

        public bool Readable;

        public string Trouble;

        public Type Declared;

        public Type Router; // null for anything but a base

        public int Bit = -1;

        public readonly List<BlobchegLiveGeneration> Retired = new List<BlobchegLiveGeneration>();

        public bool HasDebug => DebugOffset != 0;
    }

    public struct BlobchegLiveGeneration // never dereferenced: only explains a stale pointer
    {
        public ulong Address;
        public int Length;
    }

    public sealed class BlobchegLiveRecord
    {
        public uint Offset;
        public uint Size;
        public uint TypeHash;
        public string TypeName;
        public string NodeName;

        public Type Type;

        public bool TypeAgrees; // false: built against another struct version, fields read as nonsense
    }

    public sealed class BlobchegLiveRow
    {
        public BlobchegId Id;
        public ulong Mask;
        public string NodeName;
        public readonly List<BlobchegLiveRowEntry> Entries = new List<BlobchegLiveRowEntry>();
    }

    public struct BlobchegLiveRowEntry
    {
        public int Bit;
        public string Domain;
        public uint Offset;
    }

    // Only the catalogs are cached: a rebuild under a live PlayMode moves every buffer.
    public static unsafe class BlobchegLive
    {
        public static List<BlobchegLiveFile> Files() // kind comes from each buffer header, not the name
        {
            var files = new List<BlobchegLiveFile>();

            for (var i = 0; i < BlobchegBases.SlotCount; i++)
            {
                if (!BlobchegBases.TryGetSlot(i, out var key, out var ptr, out var length, out var debugOffset))
                    continue;

                var file = new BlobchegLiveFile
                {
                    Key = key,
                    Address = (ulong)ptr,
                    Length = length,
                    DebugOffset = debugOffset,
                    Name = NameOf(key),
                };

                ReadHeader(file, ptr, length);
                Describe(file);

                for (var g = 0; g < BlobchegBases.RetiredGenerations; g++)
                {
                    if (BlobchegBases.TryGetRetired(i, g, out var address, out var retiredLength))
                        file.Retired.Add(new BlobchegLiveGeneration { Address = address, Length = retiredLength });
                }

                files.Add(file);
            }

            files.Sort(Order);
            return files;
        }

        // A release file carries no debug contour and holds no record table, so there is nothing to list.
        public static List<BlobchegLiveRecord> RecordsOf(BlobchegLiveFile file)
        {
            var records = new List<BlobchegLiveRecord>();
            if (file == null || !file.Readable || file.Kind != BlobchegFileKind.Database || !file.HasDebug)
                return records;

            var bytes = (byte*)file.Address;
            var count = *(uint*)(bytes + file.DebugOffset + 4);
            var entries = (BlobchegDebugEntry*)(bytes + file.DebugOffset + BlobchegDebugSection.PrologSize);

            for (var i = 0u; i < count; i++)
            {
                var entry = entries[i];
                BlobchegDebugSection.ReadNames(bytes, entry, out var typeName, out var nodeName);

                var record = new BlobchegLiveRecord
                {
                    Offset = entry.Offset,
                    Size = entry.Size,
                    TypeHash = entry.TypeHash,
                    TypeName = typeName,
                    NodeName = nodeName,
                    Type = TypeOf(entry.TypeHash, typeName),
                };

                record.TypeAgrees = record.Type != null && HashOf(record.Type) == entry.TypeHash;
                records.Add(record);
            }

            return records;
        }

        // Bit numbering lives in code, not the file: names come from the registry the rebuild numbers by.
        public static List<BlobchegLiveRow> RowsOf(BlobchegLiveFile file)
        {
            var rows = new List<BlobchegLiveRow>();
            if (file == null || !file.Readable || file.Kind != BlobchegFileKind.Router)
                return rows;

            var domains = file.Declared != null ? BlobchegRouters.DomainsOf(file.Declared) : Array.Empty<Type>();
            var router = BlobchegRouterBlob.FromRegistry(
                (byte*)file.Address, file.Length, BlobchegNaming.TagOf(file.Name), file.DebugOffset);

            for (var i = 0u; i < router.Count; i++)
            {
                var id = router.IdAt(i);
                if (!router.TryGet(id, out var source))
                    continue;

                var row = new BlobchegLiveRow
                {
                    Id = id,
                    Mask = source.Mask,
                    NodeName = router.HasDebug ? router.Describe(id) : null,
                };

                for (var bit = 0; bit < BlobchegRouterFormat.MaxDomains; bit++)
                {
                    if (!source.TryOffset(bit, out var offset))
                        continue;

                    row.Entries.Add(new BlobchegLiveRowEntry
                    {
                        Bit = bit,
                        Domain = bit < domains.Length ? BlobchegDomains.NameOf(domains[bit]) : "bit " + bit,
                        Offset = offset,
                    });
                }

                rows.Add(row);
            }

            return rows;
        }

        public static BlobchegLiveFile Find(List<BlobchegLiveFile> files, string name)
        {
            for (var i = 0; i < files.Count; i++)
            {
                if (string.Equals(files[i].Name, name, StringComparison.Ordinal))
                    return files[i];
            }

            return null;
        }

        // Called on a domain reload: the types of the previous domain are dead.
        public static void Forget()
        {
            s_Names = null;
            s_Declared = null;
            s_ByHash = null;
            s_ByName = null;
        }

        static void ReadHeader(BlobchegLiveFile file, byte* ptr, int length)
        {
            if (length < BlobchegFormat.HeaderSize)
            {
                file.Trouble = $"буфер {length} Б короче {BlobchegFormat.HeaderSize}-байтной шапки";
                return;
            }

            file.Header = *(BlobchegHeader*)ptr;

            if (file.Header.Magic != BlobchegFormat.Magic)
            {
                file.Trouble = $"это не blobcheg-файл: magic {file.Header.Magic:X8}";
                return;
            }

            if (file.Header.Version != BlobchegFormat.Version)
            {
                file.Trouble = $"версия формата {file.Header.Version}, читатель понимает {BlobchegFormat.Version}";
                return;
            }

            try
            {
                file.Kind = file.Header.Kind;
            }
            catch (InvalidOperationException e)
            {
                file.Trouble = e.Message;
                return;
            }

            file.Readable = true;
        }

        static void Describe(BlobchegLiveFile file)
        {
            if (!Declared.TryGetValue(file.Key, out var declared))
                return;

            file.Declared = declared;

            if (file.Kind != BlobchegFileKind.Database)
                return;

            file.Router = BlobchegRouters.RouterOf(declared);
            if (file.Router != null)
                file.Bit = BlobchegRouters.BitOf(declared);
        }

        static string NameOf(ulong key)
            => Names.TryGetValue(key, out var name) ? name : BlobchegDomainNames.Of(key);

        // The tree draws one header per router and relies on its bases lying adjacent in bit order.
        static int Order(BlobchegLiveFile a, BlobchegLiveFile b)
        {
            var byKind = KindRank(a.Kind).CompareTo(KindRank(b.Kind));
            if (byKind != 0)
                return byKind;

            var byRouter = string.CompareOrdinal(RouterName(a), RouterName(b));
            if (byRouter != 0)
                return byRouter;

            var byBit = a.Bit.CompareTo(b.Bit);
            return byBit != 0 ? byBit : string.CompareOrdinal(a.Name, b.Name);
        }

        static string RouterName(BlobchegLiveFile file)
            => file.Router != null ? BlobchegRouters.NameOf(file.Router) : string.Empty;

        static int KindRank(BlobchegFileKind kind)
        {
            switch (kind)
            {
                case BlobchegFileKind.Router: return 0;
                case BlobchegFileKind.Database: return 1;
                default: return 2;
            }
        }

        static Dictionary<ulong, string> Names
        {
            get
            {
                BuildCatalog();
                return s_Names;
            }
        }

        static Dictionary<ulong, Type> Declared
        {
            get
            {
                BuildCatalog();
                return s_Declared;
            }
        }

        // Keys follow the same three rules the codegen emits its constants by.
        static void BuildCatalog()
        {
            if (s_Names != null)
                return;

            s_Names = new Dictionary<ulong, string>();
            s_Declared = new Dictionary<ulong, Type>();

            foreach (var domain in BlobchegDomains.All)
                Remember(BlobchegDomains.NameOf(domain), domain);

            foreach (var router in BlobchegRouters.All)
                Remember(BlobchegRouters.NameOf(router), router);

            foreach (var table in TypeCache.GetTypesWithAttribute<BlobchegHashesAttribute>())
            {
                var attribute = (BlobchegHashesAttribute)Attribute.GetCustomAttribute(
                    table, typeof(BlobchegHashesAttribute));

                if (attribute?.Router != null)
                    Remember(BlobchegHashesFormat.IdentityOf(BlobchegRouters.NameOf(attribute.Router)), table);
            }
        }

        static void Remember(string name, Type declared)
        {
            var key = BlobchegNaming.NameHash(name);
            s_Names[key] = name;
            s_Declared[key] = declared;
        }

        // Hash decides; the name is a fallback for a changed struct, which then shows as disagreeing.
        static Type TypeOf(uint typeHash, string typeName)
        {
            BuildRecordTypes();

            if (typeHash != 0 && s_ByHash.TryGetValue(typeHash, out var byHash))
                return byHash;

            return !string.IsNullOrEmpty(typeName) && s_ByName.TryGetValue(typeName, out var byName) ? byName : null;
        }

        static void BuildRecordTypes() // a record belongs to exactly one domain, none live outside them
        {
            if (s_ByHash != null)
                return;

            s_ByHash = new Dictionary<uint, Type>();
            s_ByName = new Dictionary<string, Type>(StringComparer.Ordinal);

            foreach (var domain in BlobchegDomains.All)
            {
                foreach (var record in TypeCache.GetTypesDerivedFrom(domain))
                {
                    if (!record.IsValueType || record.IsGenericTypeDefinition || record.FullName == null)
                        continue;

                    s_ByName[record.FullName] = record;

                    var hash = HashOf(record);
                    if (hash != 0)
                        s_ByHash[hash] = record;
                }
            }
        }

        // Both GetHashCode32 overloads hash the assembly-qualified name, so this matches the writer.
        static uint HashOf(Type record) => unchecked((uint)BurstRuntime.GetHashCode32(record));

        static Dictionary<ulong, string> s_Names;
        static Dictionary<ulong, Type> s_Declared;
        static Dictionary<uint, Type> s_ByHash;
        static Dictionary<string, Type> s_ByName;
    }
}
