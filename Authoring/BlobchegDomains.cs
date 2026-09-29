using System;
using System.Collections.Generic;
using System.Linq;

namespace Blobcheg.Authoring
{
    // Gathered from [Blobcheg] attributes, not a hand-written list: one fewer place to forget.
    public static class BlobchegDomains
    {
        static Type[] _all;

        public static Type[] All
        {
            get
            {
                if (_all == null)
                    _all = BlobchegTypeScan.WithAttribute<BlobchegAttribute>()
                        .Select(db => db.GetCustomAttributes(typeof(BlobchegAttribute), false))
                        .Where(attributes => attributes.Length > 0)
                        .Select(attributes => ((BlobchegAttribute)attributes[0]).Domain)
                        .Distinct()
                        .OrderBy(domain => domain.FullName, StringComparer.Ordinal)
                        .ToArray();

                return _all;
            }
        }

        public static void Forget() => _all = null;

        public static string NameOf(Type domain) => domain.Name;

        public static Type DomainOf(Type recordType)
        {
            var domains = All;
            Type found = null;

            foreach (var candidate in recordType.GetInterfaces())
            {
                if (Array.IndexOf(domains, candidate) < 0)
                    continue;

                if (found != null)
                    throw new InvalidOperationException(
                        $"Blobcheg: record '{recordType.FullName}' is marked with two domains at once " +
                        $"('{found.Name}' and '{candidate.Name}') — it is obliged to belong to one base");

                found = candidate;
            }

            if (found == null)
                throw new InvalidOperationException(
                    $"Blobcheg: record '{recordType.FullName}' belongs to no domain. " +
                    "A domain is a marker interface declared through [Blobcheg(typeof(IDomain))] on the base struct");

            return found;
        }

        public static void RequireDeclared(Type domain, string what)
        {
            if (Array.IndexOf(All, domain) < 0)
                throw new InvalidOperationException(
                    $"Blobcheg: {what} references domain '{domain.FullName}', which is declared by no " +
                    "base — a [Blobcheg(typeof(...))] on the base struct is needed");
        }

        public static IEnumerable<Type> DomainsOf(BlobchegNodeSo node)
        {
            var declared = node.OutTypes;
            if (declared == null || declared.Length == 0)
                throw new InvalidOperationException(
                    $"Blobcheg: node '{node.name}' declared no domain at all in OutTypes");

            foreach (var domain in declared)
            {
                RequireDeclared(domain, $"node '{node.name}'");
                yield return domain;
            }
        }
    }
}
