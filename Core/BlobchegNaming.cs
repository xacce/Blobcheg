using System;
using System.Text;

namespace Blobcheg
{
    /// <summary>
    /// The single place where a domain name turns into a file name and into the identity of that
    /// file. Binding on both the writer and the transport — let them drift apart and the base simply
    /// is not found.
    /// </summary>
    public static class BlobchegNaming
    {
        public const string Extension = ".bcheg";

        /// <summary>The default folder inside the project's StreamingAssets.</summary>
        public const string DefaultFolder = "Blobcheg";

        public static string FileName(string domainName)
        {
            if (string.IsNullOrEmpty(domainName))
                throw new ArgumentException("Blobcheg: empty domain name", nameof(domainName));

            return domainName + Extension;
        }

        /// <summary>
        /// The identity of a file: fnv1a-64 over the domain or router name. It rides in the header and
        /// is checked on load — otherwise two .bcheg files swapped by mistake both come up and quietly
        /// hand out someone else's bytes.
        ///
        /// Computed over the name, not over the content: the content changes with every rebuild, the
        /// identity is obliged to outlive it.
        /// </summary>
        public static ulong NameHash(string name)
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

        /// <summary>
        /// The router tag — the high byte of <see cref="BlobchegId"/>. Zero is reserved for "id not
        /// assigned", so the tag lives in 1..255; uniqueness of tags across the project is proven by
        /// the editor router registry, not by hoping the hash does not collide.
        /// </summary>
        public static byte TagOf(string routerName)
        {
            if (string.IsNullOrEmpty(routerName))
                throw new ArgumentException("Blobcheg: empty router name", nameof(routerName));

            return (byte)(NameHash(routerName) % 255 + 1);
        }
    }
}
