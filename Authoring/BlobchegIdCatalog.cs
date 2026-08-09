using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace Blobcheg.Authoring
{
    /// <summary>
    /// The candidates for an id field: the id carriers of the router that stands as the field's
    /// parameter. Gathered by walking the nodes — for the same reason as the record catalogue: the
    /// search index lags behind the import, and a picker that showed something foreign errs silently.
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

        /// <summary>The router name by its struct type — a constant emitted by the codegen.</summary>
        public static string RouterNameOf(Type router)
        {
            if (router == null)
                return null;

            var field = router.GetField("RouterName", BindingFlags.Public | BindingFlags.Static);
            return field != null && field.IsLiteral ? (string)field.GetRawConstantValue() : router.Name;
        }
    }
}
