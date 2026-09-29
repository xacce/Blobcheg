using System;
using System.Text;

namespace Blobcheg
{
    // fnv1a-64 of "{Router}:{Name}"; no domain on purpose: a hash names one router row, not a domain.
    public static class BlobchegHashKey
    {
        public const byte Separator = (byte)':';

        const ulong OffsetBasis = 14695981039346656037;
        const ulong Prime = 1099511628211;

        public static ulong Of(string routerName, string name)
        {
            if (string.IsNullOrEmpty(routerName))
                throw new ArgumentException("Blobcheg: an empty router name in a hash key", nameof(routerName));

            if (string.IsNullOrEmpty(name))
                throw new ArgumentException("Blobcheg: an empty node name in a hash key", nameof(name));

            var hash = OffsetBasis;
            Feed(ref hash, Encoding.UTF8.GetBytes(routerName));

            hash ^= Separator;
            hash *= Prime;

            Feed(ref hash, Encoding.UTF8.GetBytes(name));

            if (hash == 0) // zero marks an empty slot / unset field; one odd*odd step never lands on zero
            {
                hash ^= 0xFF;
                hash *= Prime;
            }

            return hash;
        }

        public static ulong Of<TRouter>(string name) where TRouter : unmanaged, IBlobchegRouter
            => Of(default(TRouter).Name, name);

        static void Feed(ref ulong hash, byte[] bytes)
        {
            foreach (var b in bytes)
            {
                hash ^= b;
                hash *= Prime;
            }
        }
    }
}
