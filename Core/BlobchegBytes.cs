using System;
using System.IO;
using System.Text;

namespace Blobcheg
{
    /// <summary>
    /// Byte-level odds and ends shared by the base writer and the router writer: little-endian
    /// primitives, stamping the header and writing a file atomically. Kept in one place because the
    /// two files of the package must have a byte-identical header — let them drift apart and the
    /// reader only finds out at runtime.
    /// </summary>
    public static class BlobchegBytes
    {
        public static void WriteU16(byte[] to, int at, ushort value)
        {
            to[at] = (byte)(value & 0xFF);
            to[at + 1] = (byte)(value >> 8);
        }

        public static void WriteU32(byte[] to, int at, uint value)
        {
            to[at] = (byte)(value & 0xFF);
            to[at + 1] = (byte)((value >> 8) & 0xFF);
            to[at + 2] = (byte)((value >> 16) & 0xFF);
            to[at + 3] = (byte)((value >> 24) & 0xFF);
        }

        public static void WriteU64(byte[] to, int at, ulong value)
        {
            WriteU32(to, at, (uint)(value & 0xFFFFFFFF));
            WriteU32(to, at + 4, (uint)(value >> 32));
        }

        /// <summary>A fixed-width mask of 1/2/4/8 bytes.</summary>
        public static void WriteMask(byte[] to, int at, ulong value, int width)
        {
            switch (width)
            {
                case 1:
                    to[at] = (byte)value;
                    return;
                case 2:
                    WriteU16(to, at, (ushort)value);
                    return;
                case 4:
                    WriteU32(to, at, (uint)value);
                    return;
                case 8:
                    WriteU64(to, at, value);
                    return;
                default:
                    throw new ArgumentOutOfRangeException(nameof(width), $"Blobcheg: mask width {width} B");
            }
        }

        public static ushort ReadU16(byte[] from, int at) => (ushort)(from[at] | (from[at + 1] << 8));

        public static uint ReadU32(byte[] from, int at)
            => (uint)(from[at] | (from[at + 1] << 8) | (from[at + 2] << 16) | (from[at + 3] << 24));

        public static ulong ReadU64(byte[] from, int at) => ReadU32(from, at) | ((ulong)ReadU32(from, at + 4) << 32);

        /// <summary>A length-prefixed UTF8 string — that is how names lie both in the base debug section and in the router.</summary>
        public static void WriteString(Stream stream, string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value ?? string.Empty);
            if (bytes.Length > ushort.MaxValue)
                throw new InvalidOperationException($"Blobcheg: a name of {bytes.Length} B does not fit into the debug section");

            stream.WriteByte((byte)(bytes.Length & 0xFF));
            stream.WriteByte((byte)(bytes.Length >> 8));
            stream.Write(bytes, 0, bytes.Length);
        }

        /// <summary>
        /// Stamps the header over the assembled body and returns the content hash. The identity of
        /// the file (<paramref name="nameHash"/>) comes from the outside: the base writer and the
        /// router writer know their own name, the bytes do not.
        /// </summary>
        public static ulong Seal(byte[] file, ushort flags, uint debugOffset, ulong nameHash)
        {
            var contentHash = BlobchegHash.Of(file, BlobchegFormat.HeaderSize, file.Length - BlobchegFormat.HeaderSize);

            WriteU32(file, 0, BlobchegFormat.Magic);
            WriteU16(file, 4, BlobchegFormat.Version);
            WriteU16(file, 6, flags);
            WriteU32(file, 8, (uint)file.Length);
            WriteU32(file, 12, debugOffset);
            WriteU64(file, 16, contentHash);
            WriteU64(file, 24, nameHash);

            return contentHash;
        }

        /// <summary>
        /// Writes the file if the content differs from what lies on disk. Returns whether the file
        /// was touched: an untouched file does not wake the importer, and so does not rebake what
        /// depends on it.
        /// </summary>
        public static bool WriteIfChanged(string directory, string path, byte[] file, ulong contentHash)
        {
            Directory.CreateDirectory(directory);

            if (SameOnDisk(path, file, contentHash))
                return false;

            var temp = path + ".tmp";
            File.WriteAllBytes(temp, file);
            Swap(temp, path);
            return true;
        }

        /// <summary>
        /// Swapping the file with retries. The assembled file lies in StreamingAssets, so Unity
        /// imports it, and in Unity 6 the import runs in separate worker processes — at the moment
        /// of the swap the file may be open in one of them, and the exchange fails with "cannot
        /// delete the file being replaced".
        ///
        /// Waiting here is allowed and necessary: they hold the file for milliseconds. Writing over
        /// it silently is not an option — right here a torn write would leave half a blob that looks
        /// alive and lies.
        /// </summary>
        static void Swap(string temp, string path)
        {
            const int attempts = 20;

            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    if (File.Exists(path))
                        File.Replace(temp, path, null);
                    else
                        File.Move(temp, path);

                    return;
                }
                catch (IOException) when (attempt < attempts)
                {
                    System.Threading.Thread.Sleep(20);
                }
                catch (UnauthorizedAccessException) when (attempt < attempts)
                {
                    System.Threading.Thread.Sleep(20);
                }
            }
        }

        static bool SameOnDisk(string path, byte[] file, ulong contentHash)
        {
            if (!File.Exists(path))
                return false;

            if (new FileInfo(path).Length != file.Length)
                return false;

            var head = new byte[BlobchegFormat.HeaderSize];
            using (var stream = File.OpenRead(path))
            {
                if (stream.Read(head, 0, head.Length) != head.Length)
                    return false;
            }

            return ReadU32(head, 0) == BlobchegFormat.Magic
                   && ReadU16(head, 4) == BlobchegFormat.Version
                   && ReadU16(head, 6) == ReadU16(file, 6)
                   && ReadU32(head, 8) == (uint)file.Length
                   && ReadU64(head, 16) == contentHash;
        }
    }
}
