using System;
using System.Text;

namespace Blobcheg
{
    /// <summary>
    /// The key of a hash table: <c>"{Router}:{Name}"</c>, folded into a <c>ulong</c>. A pure function —
    /// it needs neither a table, nor a rebuild, nor a loaded base, which is why it is called the same
    /// way by a node at bake time, by a tool, and by a consumer who keeps the name as a string in a
    /// config.
    ///
    /// There is no domain in the key, on purpose. A hash unfolds into a router row number, and a row is
    /// a notion of the router: there is one per node regardless of how many domains it writes into. A
    /// domain in the key would give one node several hashes leading into one and the same row.
    ///
    /// The router in the key is mandatory for the same reason the tag lives in <see cref="BlobchegId"/>:
    /// without it two nodes with the same name in different routers give one hash for two different
    /// rows.
    ///
    /// The algorithm is fnv1a-64, the same as in <see cref="BlobchegNaming.NameHash"/>: there is no
    /// second family of hashes in the package.
    /// </summary>
    public static class BlobchegHashKey
    {
        /// <summary>The separator between the router name and the node name.</summary>
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

            // Zero is taken: it marks an empty table slot and it is also what any field that has not
            // been given a hash yet is initialised to. One more step is computed — the product of an odd
            // number by an odd number is never zero, so the step is exactly one and it is
            // deterministic.
            if (hash == 0)
            {
                hash ^= 0xFF;
                hash *= Prime;
            }

            return hash;
        }

        /// <summary>The router name is taken from the type parameter, not written by hand.</summary>
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
