using System;
using System.Collections.Generic;
using System.Linq;

namespace Blobcheg.Authoring
{
    // A row is held by the node itself; the table only seats the newcomers.
    sealed class BlobchegIdTable
    {
        readonly Dictionary<Type, BlobchegNodeSo[]> _rows = new Dictionary<Type, BlobchegNodeSo[]>();
        readonly Dictionary<Type, Dictionary<BlobchegNodeSo, uint>> _ids =
            new Dictionary<Type, Dictionary<BlobchegNodeSo, uint>>();

        public static BlobchegIdTable Assign(IReadOnlyList<BlobchegNodeSo> nodes, Func<BlobchegNodeSo, string, int> held)
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
                    Inherited(members, routerName, tag, held, ids, taken);

                var rows = new BlobchegNodeSo[RowCount(taken)];
                foreach (var pair in taken)
                    rows[pair.Key] = pair.Value;

                table._rows.Add(router, rows);
                table._ids.Add(router, ids);
            }

            return table;
        }

        // Of two claims on one row (a duplicated asset, a merge) the lower GUID keeps it, the other is new.
        static void Inherited(List<BlobchegNodeSo> members, string routerName, byte tag,
            Func<BlobchegNodeSo, string, int> held, Dictionary<BlobchegNodeSo, uint> ids,
            Dictionary<uint, BlobchegNodeSo> taken)
        {
            var newcomers = new List<BlobchegNodeSo>();

            foreach (var node in members)
            {
                var row = held(node, routerName);
                if (row < 0 || row > BlobchegId.MaxIndex || taken.ContainsKey((uint)row))
                {
                    newcomers.Add(node);
                    continue;
                }

                taken.Add((uint)row, node);
                ids.Add(node, BlobchegId.Make(tag, (uint)row).Value);
            }

            var next = RowCount(taken);

            foreach (var node in newcomers)
            {
                if (next > BlobchegId.MaxIndex)
                    throw new InvalidOperationException(
                        $"Blobcheg: router '{routerName}' ran out of rows — the ceiling is {BlobchegId.MaxIndex}");

                taken.Add(next, node);
                ids.Add(node, BlobchegId.Make(tag, next).Value);
                next++;
            }
        }

        // A deterministic router: the row number is named by the node itself.
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
