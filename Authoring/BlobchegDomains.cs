using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;

namespace Blobcheg.Authoring
{
    /// <summary>
    /// Реестр доменов. Домен — это маркер-интерфейс, объявленный через <see cref="BlobchegAttribute"/>:
    /// одна база, один файл. Реестр собирается из атрибутов, а не из ручного списка — ручной список
    /// это ещё одно место, где можно забыть.
    /// </summary>
    public static class BlobchegDomains
    {
        static Type[] _all;

        public static Type[] All
        {
            get
            {
                if (_all == null)
                    _all = TypeCache.GetTypesWithAttribute<BlobchegAttribute>()
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

        /// <summary>
        /// Домен записи выводится из её маркер-интерфейса. Ни одного домена или больше одного —
        /// ошибка: запись обязана принадлежать ровно одной базе.
        /// </summary>
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
                        $"Blobcheg: запись '{recordType.FullName}' помечена сразу двумя доменами " +
                        $"('{found.Name}' и '{candidate.Name}') — она обязана принадлежать одной базе");

                found = candidate;
            }

            if (found == null)
                throw new InvalidOperationException(
                    $"Blobcheg: запись '{recordType.FullName}' не принадлежит ни одному домену. " +
                    "Домен — маркер-интерфейс, объявленный через [Blobcheg(typeof(IДомен))] на структуре базы");

            return found;
        }

        public static void RequireDeclared(Type domain, string what)
        {
            if (Array.IndexOf(All, domain) < 0)
                throw new InvalidOperationException(
                    $"Blobcheg: {what} ссылается на домен '{domain.FullName}', который не объявлен " +
                    "ни одной базой — нужен [Blobcheg(typeof(...))] на структуре базы");
        }

        public static IEnumerable<Type> DomainsOf(BlobchegNodeSo node)
        {
            var declared = node.OutTypes;
            if (declared == null || declared.Length == 0)
                throw new InvalidOperationException(
                    $"Blobcheg: нода '{node.name}' не объявила ни одного домена в OutTypes");

            foreach (var domain in declared)
            {
                RequireDeclared(domain, $"нода '{node.name}'");
                yield return domain;
            }
        }
    }
}
