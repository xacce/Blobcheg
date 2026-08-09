using System.Runtime.CompilerServices;
using Unity.Collections;

namespace Blobcheg
{
    /// <summary>
    /// A content hash is not addressing. It measures the integrity of a file and the revision of a
    /// node; there is nothing to look up by it, which is why v1 carries no other hashes.
    /// </summary>
    public static class BlobchegHash
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static unsafe ulong Of(void* data, long length)
        {
            var h = xxHash3.Hash64(data, length);
            return ((ulong)h.y << 32) | h.x;
        }

        public static unsafe ulong Of(byte[] data, int start, int length)
        {
            // An empty body is not "zero" but an honest hash of emptiness: otherwise writer and
            // reader diverge on exactly the empty file, and a base whose last node was deleted stops
            // coming up. Taking a pointer past the end of the array is not allowed, so the hash is
            // computed off a stub.
            if (length == 0)
            {
                byte empty = 0;
                return Of(&empty, 0);
            }

            fixed (byte* p = &data[start])
                return Of(p, length);
        }

        public static ulong Of(byte[] data) => Of(data, 0, data.Length);
    }
}
