using System.Collections.Generic;

namespace Blobcheg
{
    /// <summary>
    /// Domain names by their keys — for the sake of error messages only. Kept apart from
    /// <see cref="BlobchegBases"/> because that class is read by Burst code, and a managed dictionary
    /// inside it would drag a static constructor along and break the compilation.
    ///
    /// Without this, "domain 22E12032EA346169 is not loaded" is a dead end: an FNV-64 key is not
    /// searchable and is written down nowhere in the project.
    /// </summary>
    public static class BlobchegDomainNames
    {
        static readonly Dictionary<ulong, string> s_Names = new Dictionary<ulong, string>();

        public static void Remember(ulong domainKey, string name)
        {
            if (domainKey == 0 || string.IsNullOrEmpty(name))
                return;

            s_Names[domainKey] = name;
        }

        /// <summary>The domain name, and if it was never seen — the key itself, so the message is not left empty.</summary>
        public static string Of(ulong domainKey)
            => s_Names.TryGetValue(domainKey, out var name) ? name : $"{domainKey:X16}";
    }
}
