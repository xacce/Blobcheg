using System;
using System.Collections.Generic;
using System.IO;

namespace Blobcheg.Authoring
{
    /// <summary>
    /// A file of the folder the bases are built into, read down to its header and no further.
    ///
    /// The header is the whole point: it carries the length, the identity and the hash of the content,
    /// so comparing it with the buffer of the same domain answers "is the game reading what was last
    /// built" without reading a megabyte of records. The records themselves are deliberately not
    /// decoded from here — a file is not what the game reads, the resident buffer is.
    /// </summary>
    public sealed class BlobchegDiskFile
    {
        public string Path;

        /// <summary>The identity of the file — its name without the extension.</summary>
        public string Name;

        public long Length;
        public DateTime Written;
        public BlobchegHeader Header;
        public BlobchegFileKind Kind;
        public bool Readable;
        public string Trouble;

        /// <summary>
        /// The verdict of the full re-read, or <c>null</c> while nobody has asked for one: the check
        /// costs a read of the whole file and is not done on its own.
        /// </summary>
        public bool? Whole;

        public ulong ComputedHash;
    }

    /// <summary>
    /// The folder of built bases as it lies on disk. The path comes from the same transport the game
    /// loads through, so the window looks at exactly the files the game would open.
    /// </summary>
    public static unsafe class BlobchegOnDisk
    {
        // The folder the editor transport is built from, so the window sees the files the game opens.
        public static string Directory => BlobchegEditorOutput.Directory;

        /// <summary>
        /// Every <c>.bcheg</c> of the folder, header-deep. A folder that does not exist is not an
        /// error: the bases have simply never been built in this checkout.
        /// </summary>
        public static List<BlobchegDiskFile> Files()
        {
            var files = new List<BlobchegDiskFile>();

            var directory = Directory;
            if (string.IsNullOrEmpty(directory) || !System.IO.Directory.Exists(directory))
                return files;

            foreach (var path in System.IO.Directory.GetFiles(directory, "*" + BlobchegNaming.Extension))
            {
                var file = new BlobchegDiskFile
                {
                    Path = path,
                    Name = System.IO.Path.GetFileNameWithoutExtension(path),
                };

                try
                {
                    var info = new FileInfo(path);
                    file.Length = info.Length;
                    file.Written = info.LastWriteTime;
                    ReadHeader(file);
                }
                catch (IOException e)
                {
                    file.Trouble = e.Message;
                }

                files.Add(file);
            }

            files.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
            return files;
        }

        /// <summary>
        /// The full check: re-reads the file and recomputes the hash of everything past the header.
        /// This is the same arithmetic the load does, so a file that fails here would not come up in
        /// the game either.
        /// </summary>
        public static void Verify(BlobchegDiskFile file)
        {
            if (file == null || !file.Readable)
                return;

            try
            {
                var bytes = File.ReadAllBytes(file.Path);
                if (bytes.Length != file.Header.FileLength)
                {
                    file.Whole = false;
                    file.Trouble = $"по шапке {file.Header.FileLength} Б, на диске {bytes.Length} Б";
                    return;
                }

                file.ComputedHash = BlobchegHash.Of(
                    bytes, BlobchegFormat.HeaderSize, bytes.Length - BlobchegFormat.HeaderSize);

                file.Whole = file.ComputedHash == file.Header.ContentHash;
            }
            catch (IOException e)
            {
                file.Whole = false;
                file.Trouble = e.Message;
            }
        }

        /// <summary>
        /// Whether the file and the loaded buffer of the same identity carry one content. Both hashes
        /// ride in their headers, so the answer costs no read: a mismatch means the base was rebuilt
        /// after the world raised it, and what the game reads is the older bytes.
        /// </summary>
        public static bool SameContent(BlobchegDiskFile file, BlobchegLiveFile live)
            => file != null && live != null && file.Readable && live.Readable
               && file.Header.ContentHash == live.Header.ContentHash;

        static void ReadHeader(BlobchegDiskFile file)
        {
            if (file.Length < BlobchegFormat.HeaderSize)
            {
                file.Trouble = $"файл {file.Length} Б короче {BlobchegFormat.HeaderSize}-байтной шапки";
                return;
            }

            var head = new byte[BlobchegFormat.HeaderSize];
            using (var stream = File.OpenRead(file.Path))
            {
                var read = 0;
                while (read < head.Length)
                {
                    var got = stream.Read(head, read, head.Length - read);
                    if (got <= 0)
                        break;

                    read += got;
                }

                if (read < head.Length)
                {
                    file.Trouble = "шапка не дочиталась";
                    return;
                }
            }

            fixed (byte* bytes = head)
                file.Header = *(BlobchegHeader*)bytes;

            if (file.Header.Magic != BlobchegFormat.Magic)
            {
                file.Trouble = $"это не blobcheg-файл: magic {file.Header.Magic:X8}";
                return;
            }

            if (file.Header.Version != BlobchegFormat.Version)
            {
                file.Trouble = $"версия формата {file.Header.Version}, читатель понимает {BlobchegFormat.Version}";
                return;
            }

            try
            {
                file.Kind = file.Header.Kind;
            }
            catch (InvalidOperationException e)
            {
                file.Trouble = e.Message;
                return;
            }

            if (file.Header.NameHash != BlobchegNaming.NameHash(file.Name))
            {
                file.Trouble = "шапка называет не это имя — файлы переименованы или перепутаны местами";
                return;
            }

            file.Readable = true;
        }
    }
}
