using System;
using System.Collections.Generic;
using Unity.Burst;
using Unity.Collections.LowLevel.Unsafe;
using UnityEditor;

namespace Blobcheg.Authoring
{
    /// <summary>What a node handed into a domain: the writer's ticket plus everything a ref asset needs.</summary>
    sealed class BlobchegEntry
    {
        public BlobchegNodeSo Node;
        public Type Domain;
        public int Ticket;
        public string RecordType;

        /// <summary>The record bytes and the type hash — the rebuild puts the same ones into the cache so as not to call Write.</summary>
        public byte[] Bytes;

        public uint TypeHash;
    }

    /// <summary>
    /// The set of open writers for one rebuild. There is no collector layer between the node and the
    /// writer: Authoring is an editor-only assembly and calls <see cref="BlobchegWriter"/> directly.
    /// </summary>
    sealed class BlobchegCollector
    {
        readonly string _directory;
        readonly Dictionary<Type, BlobchegWriter> _writers = new Dictionary<Type, BlobchegWriter>();
        readonly HashSet<string> _written = new HashSet<string>(StringComparer.Ordinal);

        // Everything about a node is asked once per rebuild. The GUID and the name are native calls into
        // the asset database, an ordinary node's OutTypes builds the array anew on every ask, and they
        // are asked for EVERY record: on 10,000 nodes that is tens of thousands of calls for the sake of
        // three unchanging values.
        readonly Dictionary<BlobchegNodeSo, About> _about = new Dictionary<BlobchegNodeSo, About>();

        readonly Dictionary<Type, List<BlobchegRecord>> _pending = new Dictionary<Type, List<BlobchegRecord>>();

        // The open builders of the rebuild. The collector owns them: it hands them out through Begin and
        // closes the abandoned ones after a node's Write — both on a normal exit and on an exception.
        readonly List<IBlobchegOpenBuilder> _builders = new List<IBlobchegOpenBuilder>();

        struct About
        {
            public string Guid;
            public string Name;
            public Type[] OutTypes;
        }

        public BlobchegCollector(string directory) => _directory = directory;

        public IReadOnlyDictionary<Type, BlobchegWriter> Writers => _writers;

        public List<BlobchegEntry> Entries { get; } = new List<BlobchegEntry>();

        public BlobchegWriter WriterOf(Type domain)
        {
            if (!_writers.TryGetValue(domain, out var writer))
            {
                writer = BlobchegWriter.Open(_directory, BlobchegDomains.NameOf(domain));
                _writers.Add(domain, writer);
            }

            return writer;
        }

        public void Add(BlobchegNodeSo node, Type domain, string recordTypeName, uint typeHash, byte[] bytes)
        {
            var about = AboutOf(node);

            // The error text is assembled only when there is an error: on an empty run every record of
            // the project passes through Add, and an interpolation for each is exactly the price of
            // "nothing changed".
            if (Array.IndexOf(BlobchegDomains.All, domain) < 0)
                BlobchegDomains.RequireDeclared(domain, $"the record of node '{about.Name}'");

            if (Array.IndexOf(about.OutTypes, domain) < 0)
                throw new InvalidOperationException(
                    $"Blobcheg: node '{about.Name}' writes into domain '{domain.Name}', which is not in its OutTypes");

            if (!_written.Add(domain.FullName + " " + about.Guid))
                throw new InvalidOperationException(
                    $"Blobcheg: node '{about.Name}' writes into domain '{domain.Name}' a second time — " +
                    "one node gives a base exactly one record");

            // The records pile up as a batch and travel to the writer in Handover: the position in the
            // batch is the ticket.
            if (!_pending.TryGetValue(domain, out var pending))
                _pending[domain] = pending = new List<BlobchegRecord>();

            pending.Add(new BlobchegRecord(recordTypeName, about.Guid, typeHash, about.Name, bytes));
            var ticket = pending.Count - 1;

            Entries.Add(new BlobchegEntry
            {
                Node = node,
                Domain = domain,
                Ticket = ticket,
                RecordType = recordTypeName ?? string.Empty,
                Bytes = bytes,
                TypeHash = typeHash,
            });
        }

        /// <summary>The builder for a record with arrays. The bytes travel by the same Add route — in End.</summary>
        public BlobchegBuilder<T> Begin<T>(BlobchegNodeSo node) where T : unmanaged
        {
            BlobchegRecordTypes.Require(typeof(T));

            var builder = new BlobchegBuilder<T>(AboutOf(node).Name, bytes =>
                Add(node, BlobchegDomains.DomainOf(typeof(T)), typeof(T).FullName,
                    unchecked((uint)BurstRuntime.GetHashCode32<T>()), bytes));

            _builders.Add(builder);
            return builder;
        }

        /// <summary>
        /// Closes the abandoned builders after a node's Write. The memory is always freed; the error
        /// about an unclosed builder is thrown only on a normal exit — a Write that failed already
        /// carries its own, and that one is obliged to arrive as it was.
        /// </summary>
        public void ReleaseBuilders(string nodeName, bool nodeFailed)
        {
            string leaked = null;
            foreach (var builder in _builders)
            {
                if (builder.Closed)
                    continue;

                leaked = leaked ?? builder.RecordTypeName;
                builder.Abandon();
            }

            _builders.Clear();

            if (leaked != null && !nodeFailed)
                throw new InvalidOperationException(
                    $"Blobcheg: node '{nodeName}' opened a builder for record '{leaked}' and never closed it — " +
                    "without End the record is not assembled and never reached the base. Write is obliged to call End");
        }

        /// <summary>The accumulated records travel to the writers. Called once, before Flush.</summary>
        public void Handover()
        {
            foreach (var pair in _pending)
                WriterOf(pair.Key).AppendAll(pair.Value);
        }

        public bool Wrote(BlobchegNodeSo node, Type domain)
            => _written.Contains(domain.FullName + " " + AboutOf(node).Guid);

        About AboutOf(BlobchegNodeSo node)
        {
            if (_about.TryGetValue(node, out var about))
                return about;

            about = new About { Guid = GuidOf(node), Name = node.name, OutTypes = node.OutTypes ?? Type.EmptyTypes };
            _about.Add(node, about);
            return about;
        }

        public static string GuidOf(BlobchegNodeSo node)
        {
            if (!AssetDatabase.TryGetGUIDAndLocalFileIdentifier(node, out var guid, out long _))
                throw new InvalidOperationException(
                    $"Blobcheg: node '{node.name}' is not a project asset — the layout needs a stable ordering key");

            return guid;
        }
    }

    /// <summary>
    /// What a node sees inside <see cref="BlobchegNodeSo.Write"/>. The domain is derived from the marker
    /// interface of the record — there is no need to name it by hand.
    /// </summary>
    public struct BlobchegNodeWriter
    {
        internal BlobchegCollector Collector;
        internal BlobchegNodeSo Node;
        internal BlobchegIdTable Ids;

        /// <summary>
        /// Its own <see cref="BlobchegId"/> — it can be put straight into the record. It is known here
        /// already, because it is handed out by OutTypes, before the write. Zero routers on a node or
        /// several is an exception, not a guess.
        /// </summary>
        public BlobchegId Id => Ids.Single(Node);

        /// <summary>Its own id in a particular router — the form for a node that belongs to several at once.</summary>
        public BlobchegId IdIn<TRouter>() where TRouter : unmanaged, IBlobchegRouter
            => Ids.Of(Node, typeof(TRouter));

        /// <summary>The id of another node — that is how one record references another without knowing its offsets.</summary>
        public BlobchegId IdOf(BlobchegNodeSo other)
        {
            if (other == null)
                throw new ArgumentNullException(nameof(other), "Blobcheg: the id of a node that does not exist");

            return Ids.Single(other);
        }

        public BlobchegId IdOf<TRouter>(BlobchegNodeSo other) where TRouter : unmanaged, IBlobchegRouter
        {
            if (other == null)
                throw new ArgumentNullException(nameof(other), "Blobcheg: the id of a node that does not exist");

            return Ids.Of(other, typeof(TRouter));
        }

        /// <summary>
        /// A record with an array. The form is mandatory: the size of such a record is only known after
        /// all the Allocate calls, and <see cref="Add{T}"/> with a struct literal would quietly produce
        /// arrays of zero length.
        /// </summary>
        public BlobchegBuilder<T> Begin<T>() where T : unmanaged
            => Collector.Begin<T>(Node);

        /// <summary>A typed record. The domain is taken from the marker interface of <typeparamref name="T"/>.</summary>
        public unsafe void Add<T>(in T record) where T : unmanaged
        {
            BlobchegRecordTypes.Require(typeof(T));

            if (BlobchegRecordTypes.RequiresBuilder(typeof(T)))
                throw new InvalidOperationException(
                    $"Blobcheg: record '{typeof(T).FullName}' carries a BlobchegArray and is only assembled " +
                    "by a builder — a literal would quietly produce arrays of zero length. Write through w.Begin<T>()");

            var bytes = new byte[UnsafeUtility.SizeOf<T>()];
            var copy = record;
            fixed (byte* destination = bytes)
                UnsafeUtility.CopyStructureToPtr(ref copy, destination);

            Collector.Add(Node, BlobchegDomains.DomainOf(typeof(T)), typeof(T).FullName,
                unchecked((uint)BurstRuntime.GetHashCode32<T>()), bytes);
        }

        /// <summary>The raw path: the record has no type, so there are no checks by it either.</summary>
        public void AddBytes<TDomain>(ReadOnlySpan<byte> record)
        {
            Collector.Add(Node, typeof(TDomain), null, 0, record.ToArray());
        }
    }
}
