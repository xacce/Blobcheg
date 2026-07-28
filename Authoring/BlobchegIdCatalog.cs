using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace Blobcheg.Authoring
{
    /// <summary>
    /// Кандидаты для поля-id: носители id того роутера, который стоит параметром поля. Собирается
    /// обходом нод — по той же причине, что и каталог записей: поисковый индекс отстаёт от импорта,
    /// а пикер, показавший чужое, ошибётся молча.
    /// </summary>
    public static class BlobchegIdCatalog
    {
        public static List<BlobchegIdSo> Candidates(string routerName)
        {
            return BlobchegCache.Fill()
                .SelectMany(BlobchegCache.IdsOf)
                .Where(carrier => routerName == null
                                  || string.Equals(carrier.RouterName, routerName, StringComparison.Ordinal))
                .OrderBy(carrier => carrier.name, StringComparer.Ordinal)
                .ToList();
        }

        public static bool Matches(BlobchegIdSo carrier, string routerName)
            => carrier != null
               && (routerName == null || string.Equals(carrier.RouterName, routerName, StringComparison.Ordinal));

        /// <summary>Имя роутера по типу его структуры — константа, выпущенная кодогеном.</summary>
        public static string RouterNameOf(Type router)
        {
            if (router == null)
                return null;

            var field = router.GetField("RouterName", BindingFlags.Public | BindingFlags.Static);
            return field != null && field.IsLiteral ? (string)field.GetRawConstantValue() : router.Name;
        }
    }
}
