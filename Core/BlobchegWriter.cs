using System;
using System.Collections.Generic;
using System.IO;

namespace Blobcheg
{
    /// <summary>One record at the writer's input. The type is needed by the layout, the node name only by the debug section.</summary>
    public readonly struct BlobchegRecord
    {
        /// <summary>The full name of the record type. <c>null</c> means a raw block, those go into the tail of the file.</summary>
        public readonly string TypeName;

        /// <summary>A stable ordering key within the type. The pipeline passes the GUID of the node asset.</summary>
        public readonly string SortKey;

        /// <summary>BurstRuntime.GetHashCode32 of the type, 0 for raw ones. Travels into the debug section only.</summary>
        public readonly uint TypeHash;

        /// <summary>The node name for the debug section.</summary>
        public readonly string NodeName;

        public readonly byte[] Bytes;

        public BlobchegRecord(string typeName, string sortKey, uint typeHash, string nodeName, byte[] bytes)
        {
            TypeName = typeName;
            SortKey = sortKey ?? throw new ArgumentNullException(nameof(sortKey));
            TypeHash = typeHash;
            NodeName = nodeName ?? string.Empty;
            Bytes = bytes ?? throw new ArgumentNullException(nameof(bytes));
        }

        public bool IsRaw => string.IsNullOrEmpty(TypeName);
    }

    /// <summary>
    /// The base writer: ordinary C# on <see cref="System.IO"/>, it wants nothing from Unity.
    /// The offset is not handed out at the moment of <see cref="Append"/> — the layout depends on the
    /// full set of records, so Append returns a ticket and <see cref="Flush"/> exchanges tickets for
    /// offsets.
    /// </summary>
    public sealed class BlobchegWriter
    {
        readonly List<BlobchegRecord> _records = new List<BlobchegRecord>();
        readonly HashSet<string> _keys = new HashSet<string>(StringComparer.Ordinal);

        uint[] _offsets;
        ulong[] _revisions;
        bool _flushed;

        BlobchegWriter(string directory, string domainName)
        {
            Directory = directory ?? throw new ArgumentNullException(nameof(directory));
            DomainName = domainName;
            FilePath = Path.Combine(directory, BlobchegNaming.FileName(domainName));
        }

        public string Directory { get; }
        public string DomainName { get; }
        public string FilePath { get; }

        /// <summary>The content hash of the last layout. Before <see cref="Flush"/> — an error.</summary>
        public ulong ContentHash { get; private set; }

        /// <summary>The file on disk differed from the assembled one and was rewritten.</summary>
        public bool FileChanged { get; private set; }

        public int RecordCount => _records.Count;

        public static BlobchegWriter Open(string directory, string domainName)
            => new BlobchegWriter(directory, domainName);

        /// <summary>Puts a record into the queue and returns a ticket. The bytes are copied by the caller beforehand.</summary>
        public int Append(in BlobchegRecord record)
        {
            if (_flushed)
                throw new InvalidOperationException(
                    $"Blobcheg: Append into domain '{DomainName}' after Flush — the layout is already computed");

            var key = (record.TypeName ?? string.Empty) + " " + record.SortKey;
            if (!_keys.Add(key))
                throw new InvalidOperationException(
                    $"Blobcheg: domain '{DomainName}' holds two records of type '{record.TypeName}' with the same key " +
                    $"'{record.SortKey}' — one node writes exactly one record into a base");

            _records.Add(record);
            return _records.Count - 1;
        }

        /// <summary>
        /// A batch of records in one call. The tickets run consecutively from the current end, so the
        /// position of a record in the batch is exactly its ticket.
        ///
        /// It exists for the price of the call: in the editor runtime one crossing of an assembly
        /// boundary costs noticeably more than the work inside it, and a rebuild carries one record per
        /// node in the domain.
        /// </summary>
        public int AppendAll(List<BlobchegRecord> records)
        {
            var first = _records.Count;

            for (var i = 0; i < records.Count; i++)
                Append(records[i]);

            return first;
        }

        /// <summary>
        /// Lays the records out in groups by final type, computes the offsets and the integrity, writes
        /// the file atomically. If the content matches what already lies on disk, the file is not
        /// touched.
        /// </summary>
        public void Flush(bool withDebug = false)
        {
            if (_flushed)
                throw new InvalidOperationException($"Blobcheg: a repeated Flush of domain '{DomainName}'");

            // An empty base has nothing to describe, and a section of zero entries would make it longer
            // than the header and drag away the meaning of "not a single node is left in the base".
            withDebug &= _records.Count > 0;

            var order = BuildOrder();
            var file = Layout(order, withDebug, out var offsets);

            _offsets = offsets;
            _revisions = new ulong[_records.Count];
            for (var i = 0; i < _records.Count; i++)
                _revisions[i] = BlobchegHash.Of(_records[i].Bytes);

            var flags = withDebug ? BlobchegFormat.FlagHasDebug : (ushort)0;
            ContentHash = BlobchegBytes.Seal(file, flags, withDebug ? DebugOffset : 0u,
                BlobchegNaming.NameHash(DomainName));

            _flushed = true;
            FileChanged = BlobchegBytes.WriteIfChanged(Directory, FilePath, file, ContentHash);
        }

        /// <summary>The address of a record. The only thing that exists at all; before Flush — an error.</summary>
        public uint OffsetOf(int ticket)
        {
            RequireFlushed(nameof(OffsetOf));
            return _offsets[ticket];
        }

        /// <summary>The revision of a record — the hash of its bytes. The key to incrementality; before Flush — an error.</summary>
        public ulong RevisionOf(int ticket)
        {
            RequireFlushed(nameof(RevisionOf));
            return _revisions[ticket];
        }

        void RequireFlushed(string what)
        {
            if (!_flushed)
                throw new InvalidOperationException(
                    $"Blobcheg: {what} before the Flush of domain '{DomainName}' — the layout is not computed yet");
        }

        /// <summary>
        /// The order does not depend on the order of traversal: types by FullName, inside a type by the
        /// node key, raw blocks of variable length go to the tail so that they do not drag the typed
        /// ones along with them.
        /// </summary>
        int[] BuildOrder()
        {
            var order = new int[_records.Count];
            for (var i = 0; i < order.Length; i++)
                order[i] = i;

            Array.Sort(order, (a, b) =>
            {
                var ra = _records[a];
                var rb = _records[b];

                var rawA = ra.IsRaw ? 1 : 0;
                var rawB = rb.IsRaw ? 1 : 0;
                if (rawA != rawB)
                    return rawA - rawB;

                if (rawA == 0)
                {
                    var byType = string.CompareOrdinal(ra.TypeName, rb.TypeName);
                    if (byType != 0)
                        return byType;
                }

                return string.CompareOrdinal(ra.SortKey, rb.SortKey);
            });

            return order;
        }

        // One after another in the order above: every machine computes the same addresses.
        byte[] Layout(int[] order, bool withDebug, out uint[] offsets)
        {
            offsets = new uint[_records.Count];

            var position = BlobchegFormat.HeaderSize;

            for (var i = 0; i < order.Length; i++)
            {
                var ticket = order[i];

                position = BlobchegFormat.AlignUp(position);
                offsets[ticket] = (uint)position;
                position += SpanOf(ticket);
            }

            var debugOffset = 0;
            byte[] debugSection = null;
            if (withDebug)
            {
                position = BlobchegFormat.AlignUp(position);
                debugOffset = position;
                debugSection = BuildDebugSection(order, offsets, (uint)debugOffset);
                position += debugSection.Length;
            }

            var file = new byte[position];
            for (var i = 0; i < order.Length; i++)
            {
                var record = _records[order[i]];
                Buffer.BlockCopy(record.Bytes, 0, file, (int)offsets[order[i]], record.Bytes.Length);
            }

            if (debugSection != null)
                Buffer.BlockCopy(debugSection, 0, file, debugOffset, debugSection.Length);

            DebugOffset = (uint)debugOffset;
            return file;
        }

        /// <summary>
        /// How much room a record takes in the layout. A record of zero length takes one byte, not
        /// zero: otherwise the position after it does not move, the next alignment returns the same
        /// address, and two different records get ONE address — and the address is the only identity a
        /// record has.
        /// </summary>
        int SpanOf(int ticket)
        {
            var length = _records[ticket].Bytes.Length;
            return length > 0 ? length : 1;
        }

        uint DebugOffset { get; set; }

        // The entries of the section run by ascending offset: BlobchegDebugSection.Find is a binary
        // search over them.
        byte[] BuildDebugSection(int[] layoutOrder, uint[] offsets, uint sectionOffset)
        {
            var order = (int[])layoutOrder.Clone();
            Array.Sort(order, (a, b) => offsets[a].CompareTo(offsets[b]));

            var count = order.Length;
            var namesStart = sectionOffset + BlobchegDebugSection.PrologSize + (uint)(count * BlobchegDebugSection.EntrySize);

            var names = new MemoryStream();
            var nameOffsets = new uint[count];
            for (var i = 0; i < count; i++)
            {
                nameOffsets[i] = namesStart + (uint)names.Length;
                var record = _records[order[i]];
                BlobchegBytes.WriteString(names, record.TypeName ?? string.Empty);
                BlobchegBytes.WriteString(names, record.NodeName);
            }

            var section = new MemoryStream();
            var w = new BinaryWriter(section);
            w.Write(BlobchegDebugSection.Magic);
            w.Write((uint)count);
            for (var i = 0; i < count; i++)
            {
                var index = order[i];
                w.Write(offsets[index]);
                w.Write((uint)_records[index].Bytes.Length);
                w.Write(_records[index].TypeHash);
                w.Write(nameOffsets[i]);
            }

            w.Write(names.ToArray());
            w.Flush();
            return section.ToArray();
        }

    }
}
