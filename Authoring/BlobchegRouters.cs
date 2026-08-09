using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEditor;

namespace Blobcheg.Authoring
{
    /// <summary>
    /// The registry of routers: who exists, which bases it is assembled from and which bit number each
    /// base has in it. Gathered from the attributes, like the registry of domains — a hand-written list
    /// is one more place to forget in.
    ///
    /// The bit numbering here and in the codegen is computed independently, so the fact that they agree
    /// is proven by a hash: <see cref="RequireCodeGenAgrees"/> checks its own <c>LayoutHash</c> against
    /// the constant the generator emitted. A discrepancy is a build error and not a surprise at
    /// runtime.
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
        static HashSet<Type> _fixed;

        /// <summary>Every router of the project, by name. Empty means v1: the bases live on their own.</summary>
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
            _fixed = null;
        }

        public static string NameOf(Type router) => router.Name;

        /// <summary>
        /// A router whose row numbers are declared by the nodes. The rebuild does not hand them out and
        /// does not read carriers for them — neither on an ordinary run nor on a compaction.
        /// </summary>
        public static bool IsFixed(Type router)
        {
            Build();
            return _fixed.Contains(router);
        }

        /// <summary>The bases of a router in bit order: the domains by ordinal FullName.</summary>
        public static Type[] DomainsOf(Type router)
        {
            Build();

            if (!_domains.TryGetValue(router, out var domains))
                throw new InvalidOperationException($"Blobcheg: '{router.Name}' is not marked [BlobchegRouter]");

            return domains;
        }

        /// <summary>The router of a domain, or <c>null</c> if the domain joined none.</summary>
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

        /// <summary>The bit number of a domain in its router.</summary>
        public static int BitOf(Type domain)
        {
            var router = RouterOf(domain);
            if (router == null)
                throw new InvalidOperationException(
                    $"Blobcheg: domain '{domain.Name}' belongs to no router — it has no bit");

            return Array.IndexOf(DomainsOf(router), domain);
        }

        /// <summary>The routers a node writes into. Derived from OutTypes, so known before Write.</summary>
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
        /// The codegen sees the bases of its own compilation and of its references, while this registry
        /// sees the whole project. If they diverge, a base lies in an assembly the router cannot see:
        /// its bits were computed over less than all the bases, and the file assembled here will not
        /// load at runtime. We catch it at bake time.
        /// </summary>
        public static void RequireCodeGenAgrees(Type router)
        {
            var field = router.GetField("LayoutHash", BindingFlags.Public | BindingFlags.Static);
            if (field == null || !field.IsLiteral)
                throw new InvalidOperationException(
                    $"Blobcheg: router '{router.Name}' has no LayoutHash constant — the codegen did not run. " +
                    "The struct is obliged to be partial and not nested");

            var generated = (ulong)field.GetRawConstantValue();
            var expected = LayoutHashOf(router);

            if (generated == expected)
                return;

            var domains = string.Join(", ", DomainsOf(router).Select(d => d.Name));
            throw new InvalidOperationException(
                $"Blobcheg: router '{router.Name}' was built by the codegen for a different set of bases " +
                $"(the code says {generated:X16}, the project says {expected:X16}). The project sees: {domains}. " +
                "Most likely a base lies in an assembly the router's assembly does not reference — " +
                "move the base or add the reference");
        }

        static void Build()
        {
            if (_all != null)
                return;

            var routers = TypeCache.GetTypesWithAttribute<BlobchegRouterAttribute>()
                .OrderBy(type => type.Name, StringComparer.Ordinal)
                .ToArray();

            // The flag is asked once per registry build: Assign is called on every rebuild, and
            // GetCustomAttributes is reflection on every ask.
            var fixedIndex = new HashSet<Type>();
            foreach (var router in routers)
            {
                var attribute = (BlobchegRouterAttribute)router
                    .GetCustomAttributes(typeof(BlobchegRouterAttribute), false)
                    .FirstOrDefault();

                if (attribute != null && attribute.FixedIndex)
                    fixedIndex.Add(router);
            }

            var byDomain = new Dictionary<Type, Membership>();
            var domains = routers.ToDictionary(router => router, _ => new List<Type>());

            foreach (var database in TypeCache.GetTypesWithAttribute<BlobchegAttribute>())
            {
                var attribute = (BlobchegAttribute)database
                    .GetCustomAttributes(typeof(BlobchegAttribute), false)
                    .FirstOrDefault();

                if (attribute == null || string.IsNullOrEmpty(attribute.Member))
                    continue;

                // The router is looked for in the ASSEMBLY of the base and not across the whole project:
                // a router's codegen sees only its own compilation, so a base from a foreign assembly
                // would never make it into its bits anyway. As a bonus, the package's test router does
                // not get in the consumer's way — it has its own assembly.
                var router = attribute.Router;
                if (router == null)
                {
                    var visible = routers.Where(candidate => candidate.Assembly == database.Assembly).ToArray();
                    if (visible.Length != 1)
                        throw new InvalidOperationException(
                            $"Blobcheg: base '{database.Name}' has the member name '{attribute.Member}' set, but no " +
                            $"router is chosen: assembly '{database.Assembly.GetName().Name}' holds {visible.Length} routers. " +
                            "Set Router = typeof(...) in [Blobcheg]");

                    router = visible[0];
                }

                if (router.Assembly != database.Assembly)
                    throw new InvalidOperationException(
                        $"Blobcheg: base '{database.Name}' from assembly '{database.Assembly.GetName().Name}' joins " +
                        $"router '{router.Name}' from assembly '{router.Assembly.GetName().Name}'. A router and its bases " +
                        "are obliged to lie in one assembly: a router's generator sees only its own compilation");

                if (!domains.TryGetValue(router, out var list))
                    throw new InvalidOperationException(
                        $"Blobcheg: base '{database.Name}' named '{router.Name}' as its router, " +
                        "and that one is not marked [BlobchegRouter]");

                if (byDomain.TryGetValue(attribute.Domain, out var already))
                    throw new InvalidOperationException(
                        $"Blobcheg: domain '{attribute.Domain.Name}' joined a router twice " +
                        $"('{already.Router.Name}' and '{router.Name}') — a domain belongs to one router");

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
                        $"Blobcheg: not a single base joined router '{pair.Key.Name}' — " +
                        "name the member in [Blobcheg(typeof(...), \"name\")]");

                if (list.Length > BlobchegRouterFormat.MaxDomains)
                    throw new InvalidOperationException(
                        $"Blobcheg: router '{pair.Key.Name}' holds {list.Length} bases, the ceiling is " +
                        $"{BlobchegRouterFormat.MaxDomains}");

                var members = new HashSet<string>(StringComparer.Ordinal);
                foreach (var domain in list)
                {
                    if (!members.Add(byDomain[domain].Member))
                        throw new InvalidOperationException(
                            $"Blobcheg: in router '{pair.Key.Name}' the member name '{byDomain[domain].Member}' is taken twice");
                }

                ordered.Add(pair.Key, list);
            }

            foreach (var router in routers)
            {
                foreach (var domain in BlobchegDomains.All)
                {
                    if (string.Equals(router.Name, BlobchegDomains.NameOf(domain), StringComparison.Ordinal))
                        throw new InvalidOperationException(
                            $"Blobcheg: a router and a domain share a name ('{router.Name}') — their files will collide");
                }
            }

            // The tag is the high byte of an id, and the whole defence against a foreign id stands on it.
            // If two routers share one, there is no defence, so uniqueness is proven here instead of
            // being left as a hope about a hash.
            var byTag = new Dictionary<byte, Type>();
            foreach (var router in routers)
            {
                var tag = BlobchegNaming.TagOf(router.Name);
                if (byTag.TryGetValue(tag, out var already))
                    throw new InvalidOperationException(
                        $"Blobcheg: routers '{already.Name}' and '{router.Name}' met on one tag {tag} — " +
                        "an id of one would become valid in the other. Rename one of them");

                byTag.Add(tag, router);
            }

            _byDomain = byDomain;
            _domains = ordered;
            _fixed = fixedIndex;
            _all = routers;
        }
    }
}
