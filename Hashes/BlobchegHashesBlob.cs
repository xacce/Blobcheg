using System;
using System.Runtime.CompilerServices;
using Unity.Collections.LowLevel.Unsafe;

namespace Blobcheg
{
    /// <summary>
    /// Резидентная таблица хешей. Всю работу делает она; типизированный фасад
    /// (<c>[BlobchegHashes]</c>-партиал) — тонкая обёртка сверху, знающая номера бит своих баз.
    ///
    /// Подъём — это проверка header'а, целостности и пролога плюс шесть указателей. Ни одной
    /// вставки: таблица посчитана пересборкой и лежит в файле готовой.
    ///
    /// Сообщения исключений — литералы: под Бёрстом интерполяция не компилируется.
    /// </summary>
    public unsafe struct BlobchegHashesBlob : IDisposable
    {
        BlobchegBuffer _buffer;
        ulong* _keys;
        uint* _rows;
        ulong* _rowHash;
        uint* _backIndex;
        uint* _backOffsets;
        uint* _backRows;
        uint _count;
        uint _capacity;
        uint _domainCount;
        byte _tag;
        int _version;

        /// <summary>
        /// Забирает владение буфером. Личность файла — <paramref name="what"/> (имя роутера плюс
        /// суффикс), а тег для сборки <see cref="BlobchegId"/> считается по
        /// <paramref name="routerName"/>: это разные имена, и оба приезжают константами из кодогена.
        /// </summary>
        public BlobchegHashesBlob(BlobchegBuffer buffer, string what, string routerName,
            int domainCount, ulong layoutHash)
        {
            if (!buffer.IsCreated)
                throw new ArgumentException($"Blobcheg: пустой буфер таблицы '{what}'", nameof(buffer));

            _buffer = buffer;
            _tag = BlobchegNaming.TagOf(routerName);

            // Номер снимается по личности файла, а не по имени роутера: у таблицы свой файл, и
            // пересборка поднимает номер именно ему.
#if UNITY_EDITOR
            _version = BlobchegFileVersions.Of(BlobchegNaming.FileName(what));
#else
            _version = 0;
#endif

            ref var header = ref UnsafeUtility.AsRef<BlobchegHeader>(buffer.Ptr);
            var contentHash = BlobchegHash.Of(
                buffer.Ptr + BlobchegFormat.HeaderSize, buffer.Length - BlobchegFormat.HeaderSize);

            header.Validate(what, buffer.Length, contentHash, BlobchegFileKind.Hashes);

            if (buffer.Length < BlobchegHashesFormat.PrologOffset + BlobchegHashesFormat.PrologSize)
                throw new InvalidOperationException($"Blobcheg: таблица '{what}' короче пролога");

            ref var prolog = ref UnsafeUtility.AsRef<BlobchegHashesProlog>(
                buffer.Ptr + BlobchegHashesFormat.PrologOffset);

            prolog.Validate(what, buffer.Length, domainCount, layoutHash);

            _count = prolog.Count;
            _capacity = prolog.Capacity;
            _domainCount = prolog.DomainCount;
            _keys = (ulong*)(buffer.Ptr + prolog.KeysOffset);
            _rows = (uint*)(buffer.Ptr + prolog.RowsOffset);
            _rowHash = (ulong*)(buffer.Ptr + prolog.RowHashOffset);
            _backIndex = (uint*)(buffer.Ptr + prolog.BackIndexOffset);
            _backOffsets = (uint*)(buffer.Ptr + prolog.BackOffsetsOffset);
            _backRows = (uint*)(buffer.Ptr + prolog.BackRowsOffset);

            // Длина дорожек лежит в прологе и обязана сойтись с их же границами: расходятся — файл
            // собран не этим писателем, и дальше проверять нечего.
            if (_backIndex[_domainCount] != prolog.Total)
                throw new InvalidOperationException(
                    $"Blobcheg: таблица '{what}' — границы обратных дорожек не сходятся с их длиной");
        }

        public bool IsCreated => _buffer.IsCreated;

        /// <summary>
        /// Номер пересборки файла, из которого прочитан ЭТОТ буфер. Сверяется потребителем, у
        /// которого от таблицы есть производное; в плеере всегда ноль.
        /// </summary>
        public int Version => _version;

        /// <summary>Строк, то есть нод роутера, включая дырки от удалённых.</summary>
        public int Count => (int)_count;

        /// <summary>Тег роутера — старший байт id, которые отдаёт эта таблица.</summary>
        public byte Tag => _tag;

        /// <summary>
        /// Номер строки по хешу. Ноль хешем не бывает: им помечен пустой слот, и спрашивать его —
        /// это спрашивать «не назначено».
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryGetRow(ulong hash, out uint row)
        {
            if (hash == 0)
            {
                row = 0;
                return false;
            }

            var slot = (uint)hash & (_capacity - 1);

            while (true)
            {
                var key = _keys[slot];

                if (key == hash)
                {
                    row = _rows[slot];
                    return true;
                }

                if (key == 0)
                {
                    row = 0;
                    return false;
                }

                slot = (slot + 1) & (_capacity - 1);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public uint GetRow(ulong hash)
        {
            if (!TryGetRow(hash, out var row))
                throw new InvalidOperationException(
                    "Blobcheg.Hashes: неизвестный хеш — ноды с таким именем в этом роутере нет");

            return row;
        }

        /// <summary>Хеш строки по её номеру. Дырка от удалённой ноды — ноль.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ulong HashOfRow(uint row)
        {
            if (row >= _count)
                throw new InvalidOperationException(
                    "Blobcheg.Hashes: строки с таким номером в таблице нет");

            return _rowHash[row];
        }

        /// <summary>
        /// Хеш по адресу записи в базе <paramref name="bit"/>. Путь сейва, не горячий: дорожка
        /// отсортирована по оффсету, поиск двоичный.
        /// </summary>
        public bool TryHashOfOffset(int bit, uint offset, out ulong hash)
        {
            if (bit < 0 || (uint)bit >= _domainCount)
                throw new InvalidOperationException(
                    "Blobcheg.Hashes: номер базы за пределами роутера");

            var start = _backIndex[bit];
            var end = _backIndex[bit + 1];

            while (start < end)
            {
                var mid = start + (end - start) / 2;
                var at = _backOffsets[mid];

                if (at == offset)
                {
                    hash = _rowHash[_backRows[mid]];
                    return true;
                }

                if (at < offset)
                    start = mid + 1;
                else
                    end = mid;
            }

            hash = 0;
            return false;
        }

        public void Dispose()
        {
            _buffer.Dispose();
            _keys = null;
            _rows = null;
            _rowHash = null;
            _backIndex = null;
            _backOffsets = null;
            _backRows = null;
            _count = 0;
            _capacity = 0;
            _domainCount = 0;
        }
    }
}
