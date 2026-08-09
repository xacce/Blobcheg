using System;
using System.Collections.Generic;

namespace Blobcheg.Authoring
{
    /// <summary>
    /// A foreign pass over the finished layout. The rebuild finds the implementations through
    /// <c>TypeCache</c> and calls them after the bases are flushed and the routers assembled — that is,
    /// when the addresses and row numbers already exist, but the rebuild is not over yet and its report
    /// is still being written.
    ///
    /// It exists so that derived files (the hash table being the first of them) get assembled while the
    /// core knows nothing about them: otherwise every such file would sprout a branch in
    /// <see cref="BlobchegBuild"/>.
    ///
    /// An implementation is obliged to be deterministic: the pre-build gate runs the rebuild twice and
    /// demands that the second run change nothing.
    /// </summary>
    public interface IBlobchegBuildPass
    {
        void Run(BlobchegBuildLayout layout, ref BlobchegBuildReport report);
    }

    /// <summary>
    /// The layout as it came out: the rows of the routers by number and the address of every record.
    /// Everything a derived file needs, and nothing it could spoil the layout with.
    /// </summary>
    public readonly struct BlobchegBuildLayout
    {
        readonly BlobchegIdTable _ids;
        readonly Dictionary<(BlobchegNodeSo, Type), uint> _offsets;

        internal BlobchegBuildLayout(BlobchegIdTable ids, Dictionary<(BlobchegNodeSo, Type), uint> offsets)
        {
            _ids = ids;
            _offsets = offsets;
        }

        /// <summary>Where the file is to land — the same folder the bases and routers lie in.</summary>
        public string OutputDirectory => BlobchegBuild.OutputDirectory;

        /// <summary>Whether the debug contour is written. The pre-build gate takes it off for a release player.</summary>
        public bool WithDebug => BlobchegBuild.WithDebug;

        /// <summary>The routers of the project in name order.</summary>
        public IReadOnlyList<Type> Routers => BlobchegRouters.All;

        /// <summary>The bases of a router in bit order.</summary>
        public IReadOnlyList<Type> DomainsOf(Type router) => BlobchegRouters.DomainsOf(router);

        public string NameOf(Type router) => BlobchegRouters.NameOf(router);

        public ulong LayoutHashOf(Type router) => BlobchegRouters.LayoutHashOf(router);

        /// <summary>
        /// The rows of a router by number. <c>null</c> is a hole from a deleted node: the row is in the
        /// file but empty, and its number is never handed out to anyone again.
        /// </summary>
        public IReadOnlyList<BlobchegNodeSo> NodesOf(Type router) => _ids.NodesOf(router);

        /// <summary>The address of a node's record in a base. If there is no record — <c>false</c>, not zero.</summary>
        public bool TryOffset(BlobchegNodeSo node, Type domain, out uint offset)
            => _offsets.TryGetValue((node, domain), out offset);

        /// <summary>
        /// The manifest is written by the core: the rule "rewrite if anything at all diverged from what
        /// was assembled" is one for every file of the package, and no second copy of it may live in a
        /// foreign pass.
        /// </summary>
        public void SyncManifest(string name, BlobchegFileKind kind, BlobchegNodeSo[] nodes,
            int recordCount, ulong contentHash, bool fileChanged, ref BlobchegBuildReport report)
            => BlobchegBuild.SyncManifest(name, kind, nodes, recordCount, contentHash, fileChanged, ref report);
    }
}
