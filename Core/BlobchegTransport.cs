using System;
using System.IO;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.IO.LowLevel.Unsafe;

namespace Blobcheg
{
    /// <summary>
    /// Reading a base file. The layer is platform-bound on purpose: StreamingAssets on Android is not
    /// a file system, and the knowledge of that lives only inside Unity. The transport hands back the
    /// final aligned buffer straight away — there is no intermediate managed array and no extra copy.
    /// </summary>
    public interface IBlobchegTransport
    {
        BlobchegLoad Read(string fileName, Allocator allocator);
    }

    /// <summary>
    /// An implementation on <see cref="AsyncReadManager"/>: it knows about the platform virtual file
    /// systems, including StreamingAssets inside an APK. Not Burst code — what Burst is needed for is
    /// not the one who reads the file once at startup, but the one who later enters the buffer from
    /// jobs.
    /// </summary>
    public sealed class BlobchegFileTransport : IBlobchegTransport
    {
        readonly string _directory;

        public BlobchegFileTransport(string directory)
        {
            _directory = directory ?? throw new ArgumentNullException(nameof(directory));
        }

        public string Directory => _directory;

        public unsafe BlobchegLoad Read(string fileName, Allocator allocator)
        {
            var path = Path.Combine(_directory, fileName);

            var load = new BlobchegLoad
            {
                Path = path,
                Allocator = allocator,
                Info = (FileInfoResult*)UnsafeUtility.Malloc(
                    sizeof(FileInfoResult), UnsafeUtility.AlignOf<FileInfoResult>(), Allocator.Persistent),
                Command = (ReadCommand*)UnsafeUtility.Malloc(
                    sizeof(ReadCommand), UnsafeUtility.AlignOf<ReadCommand>(), Allocator.Persistent),
                At = BlobchegLoad.Stage.Size,
            };

            *load.Info = default;
            *load.Command = default;
            load.SizeHandle = AsyncReadManager.GetFileInfo(path, load.Info);
            return load;
        }
    }

    /// <summary>The default transport — StreamingAssets/Blobcheg of this project.</summary>
    public static class BlobchegTransport
    {
        static IBlobchegTransport _default;

        public static IBlobchegTransport Default
        {
            get
            {
                if (_default == null)
                    _default = new BlobchegFileTransport(
                        Path.Combine(UnityEngine.Application.streamingAssetsPath, BlobchegNaming.DefaultFolder));

                return _default;
            }
            set => _default = value ?? throw new ArgumentNullException(nameof(value));
        }
    }
}
