using System;
using System.Collections.Generic;
using System.Linq;

namespace Blobcheg.Authoring
{
    /// <summary>
    /// Handing out <see cref="BlobchegId"/>s for one rebuild. Computed BEFORE the write: the routers of
    /// a node are derived from its <c>OutTypes</c>, and that is a declaration — so no second pass over
    /// <c>Write</c> is needed, the first one already has the id in hand.
    ///
    /// An id handed out once stays with the node forever: it lies on its <see cref="BlobchegIdSo"/>
    /// carrier and is read back from there by the next rebuild. A new node gets an id in the tail, a
    /// deleted one leaves a hole — an empty row in the router file. Recomputing the positions from
    /// scratch is not allowed: an id travels into other people's saves and into baked subscenes, and a
    /// shift there quietly leads to a different node.
    ///
    /// The high byte of an id is the router tag (<see cref="BlobchegNaming.TagOf"/>): by it a foreign id
    /// is rejected at lookup, and a zero-initialised field does not pretend to be the first node.
    ///
    /// The GUID order is left only for the newcomers — so that two rebuilds in a row hand out the same
    /// thing.
    ///
    /// A router with <c>FixedIndex</c> lives differently: the row number is declared by the node
    /// (<see cref="IBlobchegIndexed"/>), the carriers are not asked, and "losing" a number together with
    /// its carrier is impossible — there is nowhere to lose it.
    /// </summary>
    sealed class BlobchegIdTable
    {
        readonly Dictionary<Type, BlobchegNodeSo[]> _rows = new Dictionary<Type, BlobchegNodeSo[]>();
        readonly Dictionary<Type, Dictionary<BlobchegNodeSo, uint>> _ids =
            new Dictionary<Type, Dictionary<BlobchegNodeSo, uint>>();

        public static BlobchegIdTable Assign(IReadOnlyList<BlobchegNodeSo> nodes, BlobchegCarriers carriers = null)
        {
            var table = new BlobchegIdTable();

            foreach (var router in BlobchegRouters.All)
            {
                var domains = BlobchegRouters.DomainsOf(router);
                var routerName = BlobchegRouters.NameOf(router);
                var tag = BlobchegNaming.TagOf(routerName);

                var members = nodes
                    .Where(node => node.OutTypes != null && node.OutTypes.Any(domain => Array.IndexOf(domains, domain) >= 0))
                    .OrderBy(BlobchegCollector.GuidOf, StringComparer.Ordinal)
                    .ToList();

                var ids = new Dictionary<BlobchegNodeSo, uint>();
                var taken = new Dictionary<uint, BlobchegNodeSo>();

                if (BlobchegRouters.IsFixed(router))
                    Declared(members, routerName, tag, ids, taken);
                else
                    HandedOut(members, carriers, routerName, tag, ids, taken);

                var rows = new BlobchegNodeSo[RowCount(taken)];
                foreach (var pair in taken)
                    rows[pair.Key] = pair.Value;

                table._rows.Add(router, rows);
                table._ids.Add(router, ids);
            }

            return table;
        }

        /// <summary>
        /// An ordinary router: the number is inherited from the carrier, a newcomer settles into the tail
        /// in GUID order. This is the journal — and also what gets lost together with a carrier that
        /// never made it into git.
        /// </summary>
        static void HandedOut(List<BlobchegNodeSo> members, BlobchegCarriers carriers, string routerName,
            byte tag, Dictionary<BlobchegNodeSo, uint> ids, Dictionary<uint, BlobchegNodeSo> taken)
        {
            foreach (var node in members)
            {
                var carrier = carriers?.Id(node, routerName);
                if (carrier == null)
                    continue;

                // A foreign tag means the carrier came from another router (or from the times when the
                // router was named differently). Such an id is not inherited: the node gets a new one,
                // in the tail.
                var was = new BlobchegId(carrier.id);
                if (!was.IsValid || was.Tag != tag)
                    continue;

                // Two on one id — that happens after a node is copied together with its carrier. The
                // place stays with whoever comes first by GUID, the second one moves into the tail as a
                // newcomer.
                if (taken.ContainsKey(was.Index))
                    continue;

                taken.Add(was.Index, node);
                ids.Add(node, was.Value);
            }

            var next = RowCount(taken);

            foreach (var node in members)
            {
                if (ids.ContainsKey(node))
                    continue;

                if (next > BlobchegId.MaxIndex)
                    throw new InvalidOperationException(
                        $"Blobcheg: router '{routerName}' ran out of rows — the ceiling is " +
                        $"{BlobchegId.MaxIndex}. A compaction will reclaim the holes left by deleted nodes");

                ids.Add(node, BlobchegId.Make(tag, next).Value);
                taken.Add(next, node);
                next++;
            }
        }

        /// <summary>
        /// A deterministic router: the row number is declared by the node. The carriers are not asked
        /// here at all — neither on an ordinary rebuild nor on a compaction — and that is the whole
        /// guarantee: wipe every carrier, rebuild, and the same ids come back.
        ///
        /// The traversal order is by GUID, as in an ordinary router, but it does not affect the result:
        /// the place of every node is named by the node itself.
        /// </summary>
        static void Declared(List<BlobchegNodeSo> members, string routerName, byte tag,
            Dictionary<BlobchegNodeSo, uint> ids, Dictionary<uint, BlobchegNodeSo> taken)
        {
            foreach (var node in members)
            {
                if (!(node is IBlobchegIndexed indexed))
                    throw new InvalidOperationException(
                        $"Blobcheg: node '{node.name}' writes into router '{routerName}', which has " +
                        $"FixedIndex — row numbers there are declared by the nodes. Implement IBlobchegIndexed " +
                        $"on '{node.GetType().Name}': the router itself hands out no numbers");

                var index = indexed.Index;

                if (index > BlobchegId.MaxIndex)
                    throw new InvalidOperationException(
                        $"Blobcheg: node '{node.name}' declared row {index} in router " +
                        $"'{routerName}' — the ceiling is {BlobchegId.MaxIndex}");

                if (taken.TryGetValue(index, out var already))
                    throw new InvalidOperationException(
                        $"Blobcheg: nodes '{already.name}' and '{node.name}' declared the same row " +
                        $"{index} in router '{routerName}' — a number belongs to one node");

                taken.Add(index, node);
                ids.Add(node, BlobchegId.Make(tag, index).Value);
            }
        }

        /// <summary>Rows in the file — up to and including the last taken number.</summary>
        static uint RowCount(Dictionary<uint, BlobchegNodeSo> taken)
        {
            var count = 0u;
            foreach (var index in taken.Keys)
            {
                if (index >= count)
                    count = index + 1;
            }

            return count;
        }

        /// <summary>
        /// The rows of a router by id — which is also the index in the array. <c>null</c> is a hole from
        /// a deleted node: the row is in the file but empty, and its id is never handed out to anyone
        /// again.
        /// </summary>
        public IReadOnlyList<BlobchegNodeSo> NodesOf(Type router)
            => _rows.TryGetValue(router, out var found) ? found : Array.Empty<BlobchegNodeSo>();

        public BlobchegId Of(BlobchegNodeSo node, Type router)
        {
            if (!_ids.TryGetValue(router, out var ids))
                throw new InvalidOperationException(
                    $"Blobcheg: '{router.Name}' is not marked [BlobchegRouter] — there are no ids in it");

            if (!ids.TryGetValue(node, out var id))
                throw new InvalidOperationException(
                    $"Blobcheg: node '{node.name}' writes into no base of router '{router.Name}' — it has no id there");

            return new BlobchegId(id);
        }

        /// <summary>The id of a node, if it has one in this router. The cache asks it, not the consumer.</summary>
        public bool TryOf(BlobchegNodeSo node, Type router, out BlobchegId id)
        {
            id = BlobchegId.None;

            if (!_ids.TryGetValue(router, out var ids) || !ids.TryGetValue(node, out var found))
                return false;

            id = new BlobchegId(found);
            return true;
        }

        /// <summary>The id of a node when it has one router. Zero or several is an error, not a guess.</summary>
        public BlobchegId Single(BlobchegNodeSo node)
        {
            var routers = BlobchegRouters.RoutersOf(node);

            if (routers.Count == 0)
                throw new InvalidOperationException(
                    $"Blobcheg: node '{node.name}' writes into no base of any router — it has no id. " +
                    "The router of a base is declared by the member name in [Blobcheg(typeof(...), \"name\")]");

            if (routers.Count > 1)
                throw new InvalidOperationException(
                    $"Blobcheg: node '{node.name}' belongs to routers " +
                    $"{string.Join(", ", routers.Select(r => r.Name))} at once — ask IdIn<T>()");

            return Of(node, routers[0]);
        }
    }
}
