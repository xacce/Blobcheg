using System;
using System.Collections.Generic;
using System.Reflection;

namespace Blobcheg.Authoring
{
    // The candidates for an id field: the id carriers of the router that stands as the field parameter.
    public static class BlobchegIdCatalog
    {
        public static List<BlobchegIdSo> Candidates(string routerName)
        {
            using var _ = BlobchegProfile.Begin("candidates for an id field");

            BlobchegFreshness.Ensure("an id field was opened");

            var found = new List<BlobchegIdSo>();

            foreach (var node in BlobchegBuild.FindNodes())
            {
                foreach (var carrier in BlobchegBuild.IdsOf(node))
                {
                    if (routerName == null || string.Equals(carrier.RouterName, routerName, StringComparison.Ordinal))
                        found.Add(carrier);
                }
            }

            found.Sort((a, b) => string.CompareOrdinal(a.name, b.name));
            return found;
        }

        public static bool Matches(BlobchegIdSo carrier, string routerName)
            => carrier != null
               && (routerName == null || string.Equals(carrier.RouterName, routerName, StringComparison.Ordinal));

        // The router name by its struct type — a constant emitted by the codegen.
        public static string RouterNameOf(Type router)
        {
            if (router == null)
                return null;

            var field = router.GetField("RouterName", BindingFlags.Public | BindingFlags.Static);
            return field != null && field.IsLiteral ? (string)field.GetRawConstantValue() : router.Name;
        }
    }
}
