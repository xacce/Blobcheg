using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEditor;

namespace Blobcheg.Authoring
{
    /// <summary>
    /// Реестр роутеров: кто есть, из каких баз собран и какой у него номер бита у каждой базы.
    /// Собирается из атрибутов, как и реестр доменов — ручной список это ещё одно место, где можно
    /// забыть.
    ///
    /// Нумерация бит здесь и в кодогене считается независимо, поэтому сходимость доказывается
    /// хешем: <see cref="RequireCodeGenAgrees"/> сверяет свой <c>LayoutHash</c> с константой,
    /// выпущенной генератором. Расхождение — ошибка сборки, а не сюрприз в рантайме.
    /// </summary>
    public static class BlobchegRouters
    {
        sealed class Membership
        {
            public Type Router;
            public string Member;
        }

        static Type[] _all;
        static Dictionary<Type, Membership> _byDomain;
        static Dictionary<Type, Type[]> _domains;

        /// <summary>Все роутеры проекта, по имени. Пусто — v1: базы живут сами по себе.</summary>
        public static Type[] All
        {
            get
            {
                Build();
                return _all;
            }
        }

        public static void Forget()
        {
            _all = null;
            _byDomain = null;
            _domains = null;
        }

        public static string NameOf(Type router) => router.Name;

        /// <summary>Базы роутера в порядке бит: домены по FullName ordinal.</summary>
        public static Type[] DomainsOf(Type router)
        {
            Build();

            if (!_domains.TryGetValue(router, out var domains))
                throw new InvalidOperationException($"Blobcheg: '{router.Name}' не помечен [BlobchegRouter]");

            return domains;
        }

        /// <summary>Роутер домена или <c>null</c>, если домен ни в один не вступал.</summary>
        public static Type RouterOf(Type domain)
        {
            Build();
            return _byDomain.TryGetValue(domain, out var membership) ? membership.Router : null;
        }

        public static string MemberOf(Type domain)
        {
            Build();
            return _byDomain.TryGetValue(domain, out var membership) ? membership.Member : null;
        }

        /// <summary>Номер бита домена в его роутере.</summary>
        public static int BitOf(Type domain)
        {
            var router = RouterOf(domain);
            if (router == null)
                throw new InvalidOperationException(
                    $"Blobcheg: домен '{domain.Name}' не входит ни в один роутер — бита у него нет");

            return Array.IndexOf(DomainsOf(router), domain);
        }

        /// <summary>Роутеры, в которые нода пишет. Выводятся из OutTypes, поэтому известны до Write.</summary>
        public static List<Type> RoutersOf(BlobchegNodeSo node)
        {
            var found = new List<Type>();

            foreach (var domain in BlobchegDomains.DomainsOf(node))
            {
                var router = RouterOf(domain);
                if (router != null && !found.Contains(router))
                    found.Add(router);
            }

            found.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
            return found;
        }

        public static ulong LayoutHashOf(Type router)
        {
            var domains = DomainsOf(router);
            var pairs = domains.Select(domain =>
                new KeyValuePair<string, string>(domain.FullName, MemberOf(domain))).ToList();

            return BlobchegRouterFormat.LayoutHash(pairs, BlobchegRouterFormat.MaskWidthFor(domains.Length));
        }

        /// <summary>
        /// Кодоген видит базы своей компиляции и её референсов, а этот реестр — весь проект.
        /// Разошлись — значит база лежит в сборке, которую роутер не видит: его биты посчитаны не по
        /// всем базам, и файл, собранный здесь, в рантайме не поднимется. Ловим на бейке.
        /// </summary>
        public static void RequireCodeGenAgrees(Type router)
        {
            var field = router.GetField("LayoutHash", BindingFlags.Public | BindingFlags.Static);
            if (field == null || !field.IsLiteral)
                throw new InvalidOperationException(
                    $"Blobcheg: у роутера '{router.Name}' нет константы LayoutHash — кодоген не отработал. " +
                    "Структура обязана быть partial и не вложенной");

            var generated = (ulong)field.GetRawConstantValue();
            var expected = LayoutHashOf(router);

            if (generated == expected)
                return;

            var domains = string.Join(", ", DomainsOf(router).Select(d => d.Name));
            throw new InvalidOperationException(
                $"Blobcheg: роутер '{router.Name}' собран кодогеном под другой набор баз " +
                $"(в коде {generated:X16}, в проекте {expected:X16}). Проект видит: {domains}. " +
                "Скорее всего база лежит в сборке, на которую сборка роутера не ссылается — " +
                "перенеси базу или добавь ссылку");
        }

        static void Build()
        {
            if (_all != null)
                return;

            var routers = TypeCache.GetTypesWithAttribute<BlobchegRouterAttribute>()
                .OrderBy(type => type.Name, StringComparer.Ordinal)
                .ToArray();

            var byDomain = new Dictionary<Type, Membership>();
            var domains = routers.ToDictionary(router => router, _ => new List<Type>());

            foreach (var database in TypeCache.GetTypesWithAttribute<BlobchegAttribute>())
            {
                var attribute = (BlobchegAttribute)database
                    .GetCustomAttributes(typeof(BlobchegAttribute), false)
                    .FirstOrDefault();

                if (attribute == null || string.IsNullOrEmpty(attribute.Member))
                    continue;

                // Роутер ищется в СБОРКЕ базы, а не по всему проекту: кодоген роутера видит только
                // свою компиляцию, поэтому база из чужой сборки в его биты всё равно не попадёт.
                // Заодно тестовый роутер пакета не мешается потребителю — у него своя сборка.
                var router = attribute.Router;
                if (router == null)
                {
                    var visible = routers.Where(candidate => candidate.Assembly == database.Assembly).ToArray();
                    if (visible.Length != 1)
                        throw new InvalidOperationException(
                            $"Blobcheg: у базы '{database.Name}' задано имя члена '{attribute.Member}', но роутер не " +
                            $"выбран: в сборке '{database.Assembly.GetName().Name}' роутеров {visible.Length}. " +
                            "Укажи Router = typeof(...) в [Blobcheg]");

                    router = visible[0];
                }

                if (router.Assembly != database.Assembly)
                    throw new InvalidOperationException(
                        $"Blobcheg: база '{database.Name}' из сборки '{database.Assembly.GetName().Name}' вступает в " +
                        $"роутер '{router.Name}' из сборки '{router.Assembly.GetName().Name}'. Роутер и его базы " +
                        "обязаны лежать в одной сборке: генератор роутера видит только свою компиляцию");

                if (!domains.TryGetValue(router, out var list))
                    throw new InvalidOperationException(
                        $"Blobcheg: база '{database.Name}' указала роутером '{router.Name}', " +
                        "который не помечен [BlobchegRouter]");

                if (byDomain.TryGetValue(attribute.Domain, out var already))
                    throw new InvalidOperationException(
                        $"Blobcheg: домен '{attribute.Domain.Name}' вступил в роутер дважды " +
                        $"('{already.Router.Name}' и '{router.Name}') — домен принадлежит одному роутеру");

                byDomain.Add(attribute.Domain, new Membership { Router = router, Member = attribute.Member });
                list.Add(attribute.Domain);
            }

            var ordered = new Dictionary<Type, Type[]>();
            foreach (var pair in domains)
            {
                var list = pair.Value
                    .OrderBy(domain => domain.FullName, StringComparer.Ordinal)
                    .ToArray();

                if (list.Length == 0)
                    throw new InvalidOperationException(
                        $"Blobcheg: в роутер '{pair.Key.Name}' не вступила ни одна база — " +
                        "назови член в [Blobcheg(typeof(...), \"имя\")]");

                if (list.Length > BlobchegRouterFormat.MaxDomains)
                    throw new InvalidOperationException(
                        $"Blobcheg: в роутере '{pair.Key.Name}' {list.Length} баз, потолок " +
                        $"{BlobchegRouterFormat.MaxDomains}");

                var members = new HashSet<string>(StringComparer.Ordinal);
                foreach (var domain in list)
                {
                    if (!members.Add(byDomain[domain].Member))
                        throw new InvalidOperationException(
                            $"Blobcheg: в роутере '{pair.Key.Name}' имя члена '{byDomain[domain].Member}' занято дважды");
                }

                ordered.Add(pair.Key, list);
            }

            foreach (var router in routers)
            {
                foreach (var domain in BlobchegDomains.All)
                {
                    if (string.Equals(router.Name, BlobchegDomains.NameOf(domain), StringComparison.Ordinal))
                        throw new InvalidOperationException(
                            $"Blobcheg: роутер и домен зовутся одинаково ('{router.Name}') — их файлы столкнутся");
                }
            }

            _byDomain = byDomain;
            _domains = ordered;
            _all = routers;
        }
    }
}
