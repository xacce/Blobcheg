using System;
using System.Collections.Generic;
using Unity.Burst;
using Unity.Collections.LowLevel.Unsafe;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace Blobcheg.Authoring
{
    sealed class BlobchegEntry
    {
        public BlobchegNodeSo Node;
        public Type Domain;
        public int Ticket;
        public string RecordType;

        public byte[] Bytes; // reused by the rebuild cache so Write is not called again

        public uint TypeHash;
    }

    sealed class BlobchegCollector
    {
        readonly string _directory;
        readonly Dictionary<Type, BlobchegWriter> _writers = new Dictionary<Type, BlobchegWriter>();
        readonly HashSet<string> _written = new HashSet<string>(StringComparer.Ordinal);

        // Node facts are asked once per rebuild: GUID/name are native calls, OutTypes allocates per ask.
        readonly Dictionary<BlobchegNodeSo, About> _about = new Dictionary<BlobchegNodeSo, About>();

        readonly Dictionary<Type, List<BlobchegRecord>> _pending = new Dictionary<Type, List<BlobchegRecord>>();

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

            // Error text is built only on error: every record passes Add, so per-record interpolation costs.
            if (Array.IndexOf(BlobchegDomains.All, domain) < 0)
                BlobchegDomains.RequireDeclared(domain, $"the record of node '{about.Name}'");

            if (Array.IndexOf(about.OutTypes, domain) < 0)
                throw new InvalidOperationException(
                    $"Blobcheg: node '{about.Name}' writes into domain '{domain.Name}', which is not in its OutTypes");

            if (!_written.Add(domain.FullName + " " + about.Guid))
                throw new InvalidOperationException(
                    $"Blobcheg: node '{about.Name}' writes into domain '{domain.Name}' a second time — " +
                    "one node gives a base exactly one record");

            if (!_pending.TryGetValue(domain, out var pending))
                _pending[domain] = pending = new List<BlobchegRecord>();

            pending.Add(new BlobchegRecord(recordTypeName, about.Guid, typeHash, about.Name, bytes));
            var ticket = pending.Count - 1; // batch index; the batch reaches the writer in Handover

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

        public BlobchegBuilder<T> Begin<T>(BlobchegNodeSo node) where T : unmanaged
        {
            BlobchegRecordTypes.Require(typeof(T));

            var builder = new BlobchegBuilder<T>(AboutOf(node).Name, bytes =>
                Add(node, BlobchegDomains.DomainOf(typeof(T)), typeof(T).FullName,
                    unchecked((uint)BurstRuntime.GetHashCode32<T>()), bytes));

            _builders.Add(builder);
            return builder;
        }

        // Memory is always freed; the leak error throws only on normal exit so a failed Write keeps its own.
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

        public void Handover() // once, before Flush
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

        // Editor-only: outside it there are no assets, so asking is a caller bug.
        public static string GuidOf(BlobchegNodeSo node)
        {
#if UNITY_EDITOR
            if (!AssetDatabase.TryGetGUIDAndLocalFileIdentifier(node, out var guid, out long _))
                throw new InvalidOperationException(
                    $"Blobcheg: node '{node.name}' is not a project asset — the layout needs a stable ordering key");

            return guid;
#else
            throw new InvalidOperationException(
                $"Blobcheg: the GUID of node '{node.name}' is asked for outside the editor — a rebuild " +
                "does not happen in a player, and there is no asset database to ask");
#endif
        }
    }

    public struct BlobchegNodeWriter
    {
        internal BlobchegCollector Collector;
        internal BlobchegNodeSo Node;
        internal BlobchegIdTable Ids;

        // Known before Write (handed out by OutTypes); zero or several routers throw.
        public BlobchegId Id => Ids.Single(Node);

        public BlobchegId IdIn<TRouter>() where TRouter : unmanaged, IBlobchegRouter
            => Ids.Of(Node, typeof(TRouter));

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

        public BlobchegBuilder<T> Begin<T>() where T : unmanaged
            => Collector.Begin<T>(Node);

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

        // Untyped raw path: no record-type checks apply.
        public void AddBytes<TDomain>(ReadOnlySpan<byte> record)
        {
            Collector.Add(Node, typeof(TDomain), null, 0, record.ToArray());
        }
    }
}
