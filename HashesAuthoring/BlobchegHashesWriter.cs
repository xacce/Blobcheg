using System;
using System.Collections.Generic;
using System.IO;

namespace Blobcheg.Authoring
{
    /// <summary>
    /// Писатель таблицы хешей. Строки добавляются в порядке номеров — номер и есть порядок вызова
    /// <see cref="Append"/>, ровно как у писателя роутера.
    ///
    /// Раскладку таблицы считает он: слоты расставляются здесь, чтобы в рантайме не осталось ни
    /// одной вставки. Политика (чьё имя, что делать с дублем) живёт выше, в
    /// <see cref="BlobchegHashesBuild"/>: писателю приезжает уже готовый ключ.
    /// </summary>
    public sealed class BlobchegHashesWriter
    {
        readonly List<ulong> _keys = new List<ulong>();
        readonly List<List<(uint Offset, uint Row)>> _tracks = new List<List<(uint, uint)>>();
        readonly int _domainCount;
        readonly ulong _layoutHash;

        bool _flushed;

        BlobchegHashesWriter(string directory, string routerName, int domainCount, ulong layoutHash)
        {
            if (domainCount <= 0)
                throw new ArgumentOutOfRangeException(nameof(domainCount),
                    "Blobcheg: таблица хешей над роутером без единой базы");

            Directory = directory ?? throw new ArgumentNullException(nameof(directory));
            RouterName = routerName;
            Identity = BlobchegHashesFormat.IdentityOf(routerName);
            FilePath = Path.Combine(directory, BlobchegNaming.FileName(Identity));

            _domainCount = domainCount;
            _layoutHash = layoutHash;

            for (var bit = 0; bit < domainCount; bit++)
                _tracks.Add(new List<(uint, uint)>());
        }

        public string Directory { get; }
        public string RouterName { get; }

        /// <summary>Личность файла: имя роутера плюс суффикс. Оно же имя манифеста.</summary>
        public string Identity { get; }

        public string FilePath { get; }

        public ulong ContentHash { get; private set; }

        public bool FileChanged { get; private set; }

        public int RowCount => _keys.Count;

        public static BlobchegHashesWriter Open(string directory, string routerName, int domainCount, ulong layoutHash)
            => new BlobchegHashesWriter(directory, routerName, domainCount, layoutHash);

        /// <summary>Кладёт строку. Ключ ноль — дырка от удалённой ноды: строка есть, но пустая.</summary>
        public void Append(ulong key)
        {
            RequireOpen();
            _keys.Add(key);
        }

        /// <summary>Обратная дорожка: по этому адресу в базе <paramref name="bit"/> лежит эта строка.</summary>
        public void Track(int bit, uint offset, uint row)
        {
            RequireOpen();

            if (bit < 0 || bit >= _domainCount)
                throw new ArgumentOutOfRangeException(nameof(bit),
                    $"Blobcheg: таблица '{Identity}' — бит {bit} при {_domainCount} базах");

            _tracks[bit].Add((offset, row));
        }

        public void Flush()
        {
            RequireOpen();

            var count = _keys.Count;
            var capacity = BlobchegHashesFormat.CapacityFor(count);

            var slotKeys = new ulong[capacity];
            var slotRows = new uint[capacity];
            var rowHash = new ulong[count];

            for (var row = 0; row < count; row++)
            {
                var key = _keys[row];
                rowHash[row] = key;

                if (key == 0)
                    continue;

                var slot = (uint)key & (capacity - 1);

                while (slotKeys[slot] != 0)
                {
                    if (slotKeys[slot] == key)
                        throw new InvalidOperationException(
                            $"Blobcheg: таблица '{Identity}' — ключ {key:X16} положен дважды");

                    slot = (slot + 1) & (capacity - 1);
                }

                slotKeys[slot] = key;
                slotRows[slot] = (uint)row;
            }

            // Внутри дорожки — по возрастанию оффсета: лукап ищет двоичным поиском.
            var total = 0;
            foreach (var track in _tracks)
            {
                track.Sort((a, b) => a.Offset.CompareTo(b.Offset));
                total += track.Count;
            }

            var keysOffset = BlobchegFormat.AlignUp(
                BlobchegHashesFormat.PrologOffset + BlobchegHashesFormat.PrologSize);
            var rowsOffset = BlobchegFormat.AlignUp(keysOffset + (int)capacity * 8);
            var rowHashOffset = BlobchegFormat.AlignUp(rowsOffset + (int)capacity * 4);
            var backIndexOffset = BlobchegFormat.AlignUp(rowHashOffset + count * 8);
            var backOffsetsOffset = BlobchegFormat.AlignUp(backIndexOffset + (_domainCount + 1) * 4);
            var backRowsOffset = BlobchegFormat.AlignUp(backOffsetsOffset + total * 4);
            var position = backRowsOffset + total * 4;

            var file = new byte[position];

            var at = BlobchegHashesFormat.PrologOffset;
            BlobchegBytes.WriteU32(file, at + 0, (uint)count);
            BlobchegBytes.WriteU32(file, at + 4, (uint)_domainCount);
            BlobchegBytes.WriteU64(file, at + 8, _layoutHash);
            BlobchegBytes.WriteU32(file, at + 16, capacity);
            BlobchegBytes.WriteU32(file, at + 20, (uint)keysOffset);
            BlobchegBytes.WriteU32(file, at + 24, (uint)rowsOffset);
            BlobchegBytes.WriteU32(file, at + 28, (uint)rowHashOffset);
            BlobchegBytes.WriteU32(file, at + 32, (uint)backIndexOffset);
            BlobchegBytes.WriteU32(file, at + 36, (uint)backOffsetsOffset);
            BlobchegBytes.WriteU32(file, at + 40, (uint)backRowsOffset);
            BlobchegBytes.WriteU32(file, at + 44, (uint)total);

            for (var slot = 0; slot < capacity; slot++)
            {
                BlobchegBytes.WriteU64(file, keysOffset + slot * 8, slotKeys[slot]);
                BlobchegBytes.WriteU32(file, rowsOffset + slot * 4, slotRows[slot]);
            }

            for (var row = 0; row < count; row++)
                BlobchegBytes.WriteU64(file, rowHashOffset + row * 8, rowHash[row]);

            var written = 0;
            for (var bit = 0; bit < _domainCount; bit++)
            {
                BlobchegBytes.WriteU32(file, backIndexOffset + bit * 4, (uint)written);

                foreach (var pair in _tracks[bit])
                {
                    BlobchegBytes.WriteU32(file, backOffsetsOffset + written * 4, pair.Offset);
                    BlobchegBytes.WriteU32(file, backRowsOffset + written * 4, pair.Row);
                    written++;
                }
            }

            BlobchegBytes.WriteU32(file, backIndexOffset + _domainCount * 4, (uint)written);

            // Отладочного контура у таблицы нет: имя ноды по id уже отдаёт роутер.
            ContentHash = BlobchegBytes.Seal(file, BlobchegFormat.FlagsOf(BlobchegFileKind.Hashes), 0,
                BlobchegNaming.NameHash(Identity));

            _flushed = true;
            FileChanged = BlobchegBytes.WriteIfChanged(Directory, FilePath, file, ContentHash);
        }

        void RequireOpen()
        {
            if (_flushed)
                throw new InvalidOperationException(
                    $"Blobcheg: таблица '{Identity}' уже собрана — дописывать в неё нечего");
        }
    }
}
