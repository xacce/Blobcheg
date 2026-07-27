using System;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;

namespace Blobcheg
{
    /// <summary>
    /// Владеющий буфер файла базы: сырая память, выровненная на <see cref="BlobchegFormat.RecordAlign"/>.
    /// Не <see cref="NativeArray{T}"/> намеренно — тот не гарантирует выравнивание, а конвертированный
    /// из указателя тащит за собой возню с safety handle ради индексации, которой здесь нет: в буфер
    /// ходят реинтерпретацией по оффсету, а не по индексу.
    /// </summary>
    public unsafe struct BlobchegBuffer : IDisposable
    {
        // Иначе джоба с полем-базой не шедулится вообще: safety-система запрещает сырые указатели
        // в джобах. Здесь это безопасно by construction — буфер иммутабелен всю сессию и читается
        // только на чтение, гонок над ним не бывает.
        [NativeDisableUnsafePtrRestriction]
        public byte* Ptr;
        public int Length;
        public Allocator Allocator;

        public bool IsCreated => Ptr != null;

        public static BlobchegBuffer Alloc(int length, Allocator allocator)
        {
            if (length < BlobchegFormat.HeaderSize)
                throw new ArgumentOutOfRangeException(nameof(length),
                    $"Blobcheg: буфер {length} Б короче header'а {BlobchegFormat.HeaderSize} Б");

            return new BlobchegBuffer
            {
                Ptr = (byte*)UnsafeUtility.Malloc(length, BlobchegFormat.RecordAlign, allocator),
                Length = length,
                Allocator = allocator,
            };
        }

        /// <summary>Копия managed-массива в свой выровненный буфер — путь едитора и тестов.</summary>
        public static BlobchegBuffer From(byte[] bytes, Allocator allocator)
        {
            var buffer = Alloc(bytes.Length, allocator);
            fixed (byte* src = bytes)
                UnsafeUtility.MemCpy(buffer.Ptr, src, bytes.Length);

            return buffer;
        }

        public void Dispose()
        {
            if (Ptr == null)
                return;

            UnsafeUtility.Free(Ptr, Allocator);
            Ptr = null;
            Length = 0;
        }
    }
}
