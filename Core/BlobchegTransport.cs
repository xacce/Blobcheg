using System;
using System.IO;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.IO.LowLevel.Unsafe;

namespace Blobcheg
{
    /// <summary>
    /// Чтение файла базы. Слой платформенный намеренно: StreamingAssets на Android — не файловая
    /// система, и знание об этом живёт только в Unity. Транспорт сразу отдаёт финальный выровненный
    /// буфер — промежуточного managed-массива и лишней копии нет.
    /// </summary>
    public interface IBlobchegTransport
    {
        BlobchegLoad Read(string fileName, Allocator allocator);
    }

    /// <summary>
    /// Реализация на <see cref="AsyncReadManager"/>: он знает про платформенные виртуальные файловые
    /// системы, включая StreamingAssets внутри APK. Не Burst-код — под Burst нужен не тот, кто
    /// читает файл однажды при старте, а тот, кто потом ходит в буфер из джоб.
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

    /// <summary>Транспорт по умолчанию — StreamingAssets/Blobcheg этого проекта.</summary>
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
