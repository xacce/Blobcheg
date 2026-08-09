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
        readonly Dictionary<int, uint> _claims = new Dictionary<int, uint>();

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
        /// The address this record already received in the previous rebuild. The source is the carrier
        /// of the node, so the journal of addresses lives in git next to the node and survives a
        /// checkout without a .bcheg.
        ///
        /// A claim is a request, not an order: a record that grew into someone else's claimed address
        /// loses its own claim and moves to the tail, while the neighbour stays put. The record that
        /// moves is exactly the one that was edited — its consumers get rebaked either way.
        /// </summary>
        public void Claim(int ticket, uint offset)
        {
            if (_flushed)
                throw new InvalidOperationException(
                    $"Blobcheg: Claim into domain '{DomainName}' after Flush — the layout is already computed");

            if (ticket < 0 || ticket >= _records.Count)
                throw new ArgumentOutOfRangeException(nameof(ticket),
                    $"Blobcheg: domain '{DomainName}' — a claim on ticket {ticket}, while there are {_records.Count} records");

            // A garbage address is no reason to lay the file out crooked: the claim is simply ignored,
            // the record gets a place in the tail and the carrier gets a new address.
            if (offset < BlobchegFormat.HeaderSize || offset % BlobchegFormat.RecordAlign != 0)
                return;

            _claims[ticket] = offset;
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

        /// <summary>
        /// Claimed addresses take their own places, everything else lands behind them in the tail. The
        /// hole left by a deleted node stays as zeroes: moving the neighbours means shifting someone
        /// else's addresses, and already baked subscenes are tied to those through DependsOn.
        ///
        /// A record that grew into someone else's claim loses its own and moves away itself — the
        /// neighbours do not budge. One that shrank stays in place, the dead remainder lies as zeroes.
        /// Unplaced records first settle into the holes between claims and only then into the tail —
        /// that is what keeps the base from swelling under active editing of lengths.
        ///
        /// When there are no claims at all (a first build, a compaction) the layout is exactly the one
        /// it always was: groups by type, raw ones in the tail.
        /// </summary>
        byte[] Layout(int[] order, bool withDebug, out uint[] offsets)
        {
            offsets = new uint[_records.Count];
            var placed = new bool[_records.Count];

            var position = BlobchegFormat.HeaderSize;

            // Holes between placed claims, by ascending address. Without them every edit of a length
            // would leave an abandoned chunk behind, and the base would grow by the sum of all the
            // intermediate versions of the record.
            var holes = new List<(int start, int end)>();

            if (_claims.Count > 0)
            {
                var rank = new int[_records.Count];
                for (var i = 0; i < order.Length; i++)
                    rank[order[i]] = i;

                // By ascending address: an overlap is only visible in that order. Identical addresses
                // (a cloned carrier) are separated by the previous deterministic order.
                var claimed = new List<int>(_claims.Keys);
                claimed.Sort((a, b) => _claims[a] != _claims[b]
                    ? _claims[a].CompareTo(_claims[b])
                    : rank[a].CompareTo(rank[b]));

                foreach (var ticket in claimed)
                {
                    var claim = (int)_claims[ticket];
                    if (claim < position)
                        continue;

                    // The growth boundary is the nearest strictly greater claimed address: up to it the
                    // record has the right to grow, past it someone else's place begins. If it does not
                    // fit, the claim is lost by IT, not by the neighbour: the record that moves is
                    // exactly the one that was edited, and only its consumers get rebaked. Equal
                    // addresses (a cloned carrier) are no boundary to each other — the `claim <
                    // position` check above separates them.
                    if (claim + SpanOf(ticket) > BoundaryOf(claimed, claim))
                        continue;

                    if (claim > position)
                        holes.Add((position, claim));

                    offsets[ticket] = (uint)claim;
                    placed[ticket] = true;
                    position = claim + SpanOf(ticket);
                }
            }

            // An unplaced record takes the first hole it fits into with alignment, and only then the
            // tail. The order of the holes is by ascending address, the order of the records is the
            // previous BuildOrder, so the layout stays deterministic.
            for (var i = 0; i < order.Length; i++)
            {
                var ticket = order[i];
                if (placed[ticket])
                    continue;

                var span = SpanOf(ticket);
                var at = -1;

                for (var h = 0; h < holes.Count; h++)
                {
                    var start = BlobchegFormat.AlignUp(holes[h].start);
                    if (start + span > holes[h].end)
                        continue;

                    at = start;

                    // The taken part is cut off, the remainder stays a hole.
                    if (start + span < holes[h].end)
                        holes[h] = (start + span, holes[h].end);
                    else
                        holes.RemoveAt(h);

                    break;
                }

                if (at < 0)
                {
                    position = BlobchegFormat.AlignUp(position);
                    at = position;
                    position += span;
                }

                offsets[ticket] = (uint)at;
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

        /// <summary>
        /// The nearest strictly greater claimed address — the boundary up to which a record may grow
        /// without touching someone else's place. Past the last claim lies only the tail, and there the
        /// boundary is infinite. The list arrived sorted by address, so the search is binary.
        /// </summary>
        int BoundaryOf(List<int> claimed, int claim)
        {
            var boundary = int.MaxValue;

            var lo = 0;
            var hi = claimed.Count - 1;
            while (lo <= hi)
            {
                var mid = lo + (hi - lo) / 2;
                var at = (int)_claims[claimed[mid]];
                if (at > claim)
                {
                    boundary = at;
                    hi = mid - 1;
                }
                else
                {
                    lo = mid + 1;
                }
            }

            return boundary;
        }

        uint DebugOffset { get; set; }

        /// <summary>
        /// The entries of the section run by ascending offset: <see cref="BlobchegDebugSection.Find"/>
        /// searches with a binary search. The layout order is no longer good for that — a claimed
        /// address puts a record anywhere, not right after the previous one.
        /// </summary>
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
