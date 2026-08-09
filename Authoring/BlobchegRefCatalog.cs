using System;
using System.Collections.Generic;
using System.Linq;

namespace Blobcheg.Authoring
{
    /// <summary>
    /// The candidates for a reference field: every ref asset in the project whose record is of the
    /// needed type.
    ///
    /// Gathered by walking the nodes rather than by searching labels or the index: the native picker
    /// cannot filter by record type at all, and the search index lags behind the import. A picker that
    /// showed the wrong list is a silent error that gets noticed at bake time or at runtime.
    /// </summary>
    public static class BlobchegRefCatalog
    {
        /// <param name="recordType">The record type; <c>null</c> means a raw ref, anything will do.</param>
        public static List<BlobchegRefSo> Candidates(Type recordType)
        {
            var wanted = recordType?.FullName;

            return BlobchegCache.Fill()
                .SelectMany(BlobchegCache.RefsOf)
                .Where(reference => wanted == null
                                    || string.Equals(reference.RecordType, wanted, StringComparison.Ordinal))
                .OrderBy(reference => reference.name, StringComparer.Ordinal)
                .ToList();
        }

        public static bool Matches(BlobchegRefSo reference, Type recordType)
            => reference != null
               && (recordType == null
                   || string.Equals(reference.RecordType, recordType.FullName, StringComparison.Ordinal));
    }
}
