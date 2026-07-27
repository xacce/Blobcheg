using System;
using System.Collections.Generic;
using System.Linq;

namespace Blobcheg.Authoring
{
    /// <summary>
    /// Раздача <see cref="BlobchegId"/> на одну пересборку. Считается ДО записи: роутеры ноды
    /// выводятся из её <c>OutTypes</c>, а это декларация — поэтому второго прохода по <c>Write</c> не
    /// нужно, id уже на руках у первого.
    ///
    /// Id — позиция ноды в списке нод роутера, отсортированном по GUID ассета. GUID, а не путь:
    /// переименование ноды не должно двигать id, как правка значения не двигает оффсет.
    /// </summary>
    sealed class BlobchegIdTable
    {
        readonly Dictionary<Type, List<BlobchegNodeSo>> _nodes = new Dictionary<Type, List<BlobchegNodeSo>>();
        readonly Dictionary<Type, Dictionary<BlobchegNodeSo, uint>> _ids =
            new Dictionary<Type, Dictionary<BlobchegNodeSo, uint>>();

        public static BlobchegIdTable Assign(IReadOnlyList<BlobchegNodeSo> nodes)
        {
            var table = new BlobchegIdTable();

            foreach (var router in BlobchegRouters.All)
            {
                var domains = BlobchegRouters.DomainsOf(router);

                var members = nodes
                    .Where(node => node.OutTypes != null && node.OutTypes.Any(domain => Array.IndexOf(domains, domain) >= 0))
                    .OrderBy(BlobchegCollector.GuidOf, StringComparer.Ordinal)
                    .ToList();

                var ids = new Dictionary<BlobchegNodeSo, uint>();
                for (var i = 0; i < members.Count; i++)
                    ids.Add(members[i], (uint)i);

                table._nodes.Add(router, members);
                table._ids.Add(router, ids);
            }

            return table;
        }

        /// <summary>Ноды роутера в порядке id — он же порядок строк в файле.</summary>
        public IReadOnlyList<BlobchegNodeSo> NodesOf(Type router)
            => _nodes.TryGetValue(router, out var found) ? found : Array.Empty<BlobchegNodeSo>();

        public BlobchegId Of(BlobchegNodeSo node, Type router)
        {
            if (!_ids.TryGetValue(router, out var ids))
                throw new InvalidOperationException(
                    $"Blobcheg: '{router.Name}' не помечен [BlobchegRouter] — id в нём не бывает");

            if (!ids.TryGetValue(node, out var id))
                throw new InvalidOperationException(
                    $"Blobcheg: нода '{node.name}' не пишет ни в одну базу роутера '{router.Name}' — id у неё там нет");

            return new BlobchegId(id);
        }

        /// <summary>Id ноды, когда роутер у неё один. Ноль или несколько — ошибка, а не догадка.</summary>
        public BlobchegId Single(BlobchegNodeSo node)
        {
            var routers = BlobchegRouters.RoutersOf(node);

            if (routers.Count == 0)
                throw new InvalidOperationException(
                    $"Blobcheg: нода '{node.name}' не пишет ни в одну базу роутера — id у неё нет. " +
                    "Роутер базы объявляется именем члена в [Blobcheg(typeof(...), \"имя\")]");

            if (routers.Count > 1)
                throw new InvalidOperationException(
                    $"Blobcheg: нода '{node.name}' входит сразу в роутеры " +
                    $"{string.Join(", ", routers.Select(r => r.Name))} — спрашивай IdIn<T>()");

            return Of(node, routers[0]);
        }
    }
}
