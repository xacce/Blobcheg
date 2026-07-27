using System.Runtime.CompilerServices;
using Unity.Collections;

namespace Blobcheg
{
    /// <summary>
    /// Хеш содержимого — не адресация. Им меряется целостность файла и ревизия ноды; искать по
    /// нему нечего, поэтому в v1 больше никаких хешей нет.
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
            // Пустое тело — не «ноль», а честный хеш пустоты: иначе писатель и читатель расходятся
            // ровно на пустом файле, и база, из которой удалили последнюю ноду, перестаёт
            // подниматься. Указатель на конец массива брать нельзя, поэтому считаем от заглушки.
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
