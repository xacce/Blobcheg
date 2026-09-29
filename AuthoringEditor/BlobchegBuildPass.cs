using System;
using System.Collections.Generic;

namespace Blobcheg.Authoring
{
    // Derived-file pass after bases flush; must be deterministic, since the gate rebuilds twice.
    public interface IBlobchegBuildPass
    {
        void Run(BlobchegBuildLayout layout, ref BlobchegBuildReport report);
    }

    public readonly struct BlobchegBuildLayout // Read-only: a derived file cannot spoil the layout.
    {
        readonly BlobchegIdTable _ids;
        readonly Dictionary<(BlobchegNodeSo, Type), uint> _offsets;

        internal BlobchegBuildLayout(BlobchegIdTable ids, Dictionary<(BlobchegNodeSo, Type), uint> offsets)
        {
            _ids = ids;
            _offsets = offsets;
        }

        public string OutputDirectory => BlobchegBuild.OutputDirectory;

        public bool WithDebug => BlobchegBuild.WithDebug; // Off for a release player.

        public IReadOnlyList<Type> Routers => BlobchegRouters.All; // Name order.

        public IReadOnlyList<Type> DomainsOf(Type router) => BlobchegRouters.DomainsOf(router); // Bit order.

        public string NameOf(Type router) => BlobchegRouters.NameOf(router);

        public ulong LayoutHashOf(Type router) => BlobchegRouters.LayoutHashOf(router);

        public IReadOnlyList<BlobchegNodeSo> NodesOf(Type router) => _ids.NodesOf(router); // null is a deleted node's hole; its number is never reused.

        public bool TryOffset(BlobchegNodeSo node, Type domain, out uint offset)
            => _offsets.TryGetValue((node, domain), out offset);

        public void SyncManifest(string name, BlobchegFileKind kind, BlobchegNodeSo[] nodes, // Core owns the manifest rule; no copy of it in a foreign pass.
            int recordCount, ulong contentHash, bool fileChanged, ref BlobchegBuildReport report)
            => BlobchegBuild.SyncManifest(name, kind, nodes, recordCount, contentHash, fileChanged, ref report);
    }
}
