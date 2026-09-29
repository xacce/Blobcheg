using System;
using System.Collections.Generic;

namespace Blobcheg.Authoring
{
    // The candidates for a reference field. The native picker cannot filter by record type at all.
    public static class BlobchegRefCatalog
    {
        public static List<BlobchegRefSo> Candidates(Type recordType)
        {
            using var _ = BlobchegProfile.Begin("candidates for a reference field");

            BlobchegFreshness.Ensure("a reference field was opened");

            var wanted = recordType?.FullName;
            var found = new List<BlobchegRefSo>();

            foreach (var node in BlobchegBuild.FindNodes())
            {
                foreach (var reference in BlobchegBuild.RefsOf(node))
                {
                    if (wanted == null || string.Equals(reference.RecordType, wanted, StringComparison.Ordinal))
                        found.Add(reference);
                }
            }

            found.Sort((a, b) => string.CompareOrdinal(a.name, b.name));
            return found;
        }

        public static bool Matches(BlobchegRefSo reference, Type recordType)
            => reference != null
               && (recordType == null
                   || string.Equals(reference.RecordType, recordType.FullName, StringComparison.Ordinal));
    }
}
