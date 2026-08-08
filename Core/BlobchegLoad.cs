using System;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.IO.LowLevel.Unsafe;

namespace Blobcheg
{
    /// <summary>
    /// Идущее чтение файла базы. Unmanaged — поэтому лежит полем прямо в <c>ISystem</c>.
    /// Чтение асинхронное by construction: на Android StreamingAssets лежит в архиве, и блокирующее
    /// ожидание на главном потоке там либо стопорит кадр, либо вешает игру насмерть.
    /// Ошибки не возвращаются, а бросаются: база либо поднялась целиком, либо игра не поехала.
    /// </summary>
    public unsafe struct BlobchegLoad : IDisposable
    {
        internal enum Stage : byte
        {
            Size = 0,
            Body = 1,
            Ready = 2,
            Taken = 3,
        }

        internal FixedString512Bytes Path;
        internal Allocator Allocator;
        internal FileInfoResult* Info;
        internal ReadCommand* Command;
        internal ReadHandle SizeHandle;
        internal ReadHandle BodyHandle;
        internal BlobchegBuffer Buffer;
        internal Stage At;

        /// <summary>
        /// Двигает автомат чтения и говорит, готов ли буфер. Именно метод, а не свойство: без вызова
        /// автомат не поедет, и «IsDone», которое никогда не станет true, было бы ловушкой.
        /// </summary>
        public bool Poll()
        {
            switch (At)
            {
                case Stage.Size:
                {
                    if (SizeHandle.Status == ReadStatus.InProgress)
                        return false;

                    var status = SizeHandle.Status;
                    SizeHandle.Dispose();
                    // Хендла больше нет: если ниже бросит, Dispose не должен его трогать.
                    At = Stage.Taken;
                    RequireStatus(status, "размер");
                    StartBody();
                    return false;
                }

                case Stage.Body:
                {
                    if (BodyHandle.Status == ReadStatus.InProgress)
                        return false;

                    var status = BodyHandle.Status;
                    BodyHandle.Dispose();
                    At = Stage.Taken;
                    RequireStatus(status, "тело");
                    At = Stage.Ready;
                    return true;
                }

                case Stage.Ready:
                    return true;

                default:
                    throw new InvalidOperationException(
                        $"Blobcheg: чтение '{Path}' уже закончено — буфер забран или оборвался");
            }
        }

        /// <summary>Блокирующее ожидание — тесты и едиторные инструменты, не игровой поток.</summary>
        public void Complete()
        {
            while (!Poll())
            {
                var handle = At == Stage.Size ? SizeHandle : BodyHandle;
                handle.JobHandle.Complete();
            }
        }

        /// <summary>Отдаёт буфер и владение им. До готовности — ошибка.</summary>
        public BlobchegBuffer Acquire()
        {
            if (At != Stage.Ready)
                throw new InvalidOperationException(
                    $"Blobcheg: Acquire буфера '{Path}' до готовности — сначала Poll или Complete");

            var buffer = Buffer;
            Buffer = default;
            At = Stage.Taken;
            FreeScratch();
            return buffer;
        }

        public void Dispose()
        {
            if (At == Stage.Size)
            {
                SizeHandle.JobHandle.Complete();
                SizeHandle.Dispose();
            }
            else if (At == Stage.Body)
            {
                BodyHandle.JobHandle.Complete();
                BodyHandle.Dispose();
            }

            Buffer.Dispose();
            FreeScratch();
            At = Stage.Taken;
        }

        void StartBody()
        {
            // Переходный: в редакторе так выглядит домен, приехавший с пуллом раньше своей
            // пересборки. Файл появится, и подъём поедет заново — см. BlobchegTransientException.
            if (Info->FileState != FileState.Exists)
                throw new BlobchegTransientException($"Blobcheg: файла базы '{Path}' нет");

            var size = Info->FileSize;
            if (size < BlobchegFormat.HeaderSize)
                throw new InvalidOperationException(
                    $"Blobcheg: файл базы '{Path}' длиной {size} Б короче header'а");

            if (size > int.MaxValue)
                throw new InvalidOperationException($"Blobcheg: файл базы '{Path}' длиной {size} Б не лезет в буфер");

            Buffer = BlobchegBuffer.Alloc((int)size, Allocator);
            *Command = new ReadCommand { Buffer = Buffer.Ptr, Offset = 0, Size = size };
            BodyHandle = AsyncReadManager.Read(Path.ToString(), Command, 1);
            At = Stage.Body;
        }

        void RequireStatus(ReadStatus status, string what)
        {
            if (status != ReadStatus.Complete)
                throw new InvalidOperationException(
                    $"Blobcheg: чтение ({what}) файла базы '{Path}' не удалось: {status}");
        }

        void FreeScratch()
        {
            if (Info != null)
            {
                UnsafeUtility.Free(Info, Unity.Collections.Allocator.Persistent);
                Info = null;
            }

            if (Command != null)
            {
                UnsafeUtility.Free(Command, Unity.Collections.Allocator.Persistent);
                Command = null;
            }
        }
    }
}
