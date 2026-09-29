using System;
using System.Text;

namespace Blobcheg
{
    // Shared by writer and transport: if their naming drifts apart the base is simply not found.
    public static class BlobchegNaming
    {
        public const string Extension = ".bcheg";

        public const string DefaultFolder = "Blobcheg";

        public static string FileName(string domainName)
        {
            if (string.IsNullOrEmpty(domainName))
                throw new ArgumentException("Blobcheg: empty domain name", nameof(domainName));

            return domainName + Extension;
        }

        public static ulong NameHash(string name) // checked on load so swapped files fail; outlives rebuilds
        {
            const ulong offsetBasis = 14695981039346656037;
            const ulong prime = 1099511628211;

            var hash = offsetBasis;
            foreach (var b in Encoding.UTF8.GetBytes(name ?? string.Empty))
            {
                hash ^= b;
                hash *= prime;
            }

            return hash;
        }

        // 1..255, zero means "unassigned"; collisions are rejected by the editor router registry.
        public static byte TagOf(string routerName)
        {
            if (string.IsNullOrEmpty(routerName))
                throw new ArgumentException("Blobcheg: empty router name", nameof(routerName));

            return (byte)(NameHash(routerName) % 255 + 1);
        }
    }
}
