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
    /// Выданный однажды id остаётся за нодой навсегда: он лежит на её носителе
    /// <see cref="BlobchegIdSo"/> и оттуда же читается следующей пересборкой. Новая нода получает
    /// id в хвосте, удалённая оставляет дырку — пустую строку в файле роутера. Пересчитывать
    /// позиции заново нельзя: id уезжает в чужие сейвы и в запечённые субсцены, и сдвиг там
    /// молча приводит к другой ноде.
    ///
    /// Старший байт id — тег роутера (<see cref="BlobchegNaming.TagOf"/>): по нему чужой id
    /// отбивается на лукапе, а нулём инициализированное поле не притворяется первой нодой.
    ///
    /// Порядок GUID остался только для новичков — чтобы две пересборки подряд раздали одно и то же.
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

                foreach (var node in members)
                {
                    var carrier = carriers?.Id(node, routerName);
                    if (carrier == null)
                        continue;

                    // Тег чужой — значит носитель приехал от другого роутера (или из времён, когда
                    // роутер звался иначе). Такой id не наследуется: нода получит новый, в хвосте.
                    var was = new BlobchegId(carrier.id);
                    if (!was.IsValid || was.Tag != tag)
                        continue;

                    // Двое на одном id — так бывает после копии ноды вместе с носителем. Место
                    // остаётся за тем, кто раньше по GUID, второй уезжает в хвост как новичок.
                    if (taken.ContainsKey(was.Index))
                        continue;

                    taken.Add(was.Index, node);
                    ids.Add(node, was.Value);
                }

                var next = 0u;
                foreach (var index in taken.Keys)
                {
                    if (index >= next)
                        next = index + 1;
                }

                foreach (var node in members)
                {
                    if (ids.ContainsKey(node))
                        continue;

                    if (next > BlobchegId.MaxIndex)
                        throw new InvalidOperationException(
                            $"Blobcheg: в роутере '{routerName}' кончились строки — потолок " +
                            $"{BlobchegId.MaxIndex}. Компакт вернёт дырки от удалённых нод");

                    ids.Add(node, BlobchegId.Make(tag, next).Value);
                    taken.Add(next, node);
                    next++;
                }

                var rows = new BlobchegNodeSo[next];
                foreach (var pair in taken)
                    rows[pair.Key] = pair.Value;

                table._rows.Add(router, rows);
                table._ids.Add(router, ids);
            }

            return table;
        }

        /// <summary>
        /// Строки роутера по id — он же индекс в массиве. <c>null</c> — дырка от удалённой ноды:
        /// строка в файле есть, но пустая, и id за ней больше никому не выдаётся.
        /// </summary>
        public IReadOnlyList<BlobchegNodeSo> NodesOf(Type router)
            => _rows.TryGetValue(router, out var found) ? found : Array.Empty<BlobchegNodeSo>();

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

        /// <summary>Id ноды, если он у неё в этом роутере есть. Спрашивает кеш, а не потребитель.</summary>
        public bool TryOf(BlobchegNodeSo node, Type router, out BlobchegId id)
        {
            id = BlobchegId.None;

            if (!_ids.TryGetValue(router, out var ids) || !ids.TryGetValue(node, out var found))
                return false;

            id = new BlobchegId(found);
            return true;
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
