using System;
using System.Collections.Generic;
using System.IO;

namespace Blobcheg
{
    /// <summary>Одна клетка строки роутера: в какой базе нода лежит и по какому оффсету.</summary>
    public readonly struct BlobchegRouterCell
    {
        /// <summary>Номер бита базы, он же позиция домена в отсортированном списке роутера.</summary>
        public readonly int Bit;

        public readonly uint Offset;

        public BlobchegRouterCell(int bit, uint offset)
        {
            Bit = bit;
            Offset = offset;
        }
    }

    /// <summary>
    /// Писатель роутера. Строки добавляются в порядке id — id и есть порядок вызова
    /// <see cref="Append"/>, поэтому раздача id живёт у того, кто знает ноды, а не здесь.
    ///
    /// Оффсеты приходят готовыми: роутер собирается ПОСЛЕ <c>Flush</c> всех баз, иначе оффсетов ещё
    /// не существует.
    /// </summary>
    public sealed class BlobchegRouterWriter
    {
        readonly List<ulong> _masks = new List<ulong>();
        readonly List<uint> _offsets = new List<uint>();
        readonly List<int> _rowStart = new List<int>();
        readonly List<string> _names = new List<string>();
        readonly int _domainCount;
        readonly int _maskWidth;
        readonly ulong _layoutHash;

        bool _flushed;

        BlobchegRouterWriter(string directory, string routerName, int domainCount, ulong layoutHash)
        {
            Directory = directory ?? throw new ArgumentNullException(nameof(directory));
            RouterName = routerName;
            FilePath = Path.Combine(directory, BlobchegNaming.FileName(routerName));

            _domainCount = domainCount;
            _maskWidth = BlobchegRouterFormat.MaskWidthFor(domainCount);
            _layoutHash = layoutHash;
        }

        public string Directory { get; }
        public string RouterName { get; }
        public string FilePath { get; }

        public ulong ContentHash { get; private set; }

        public bool FileChanged { get; private set; }

        public int RowCount => _masks.Count;

        public static BlobchegRouterWriter Open(string directory, string routerName, int domainCount, ulong layoutHash)
            => new BlobchegRouterWriter(directory, routerName, domainCount, layoutHash);

        /// <summary>Кладёт строку и возвращает её id. Пустая строка допустима — нода без записей.</summary>
        public uint Append(string nodeName, IReadOnlyList<BlobchegRouterCell> cells)
        {
            if (_flushed)
                throw new InvalidOperationException(
                    $"Blobcheg: Append в роутер '{RouterName}' после Flush — файл уже собран");

            var mask = 0ul;
            var start = _offsets.Count;

            // Ячейки кладутся по возрастанию бита: лукап берёт popcount младших бит, и порядок в
            // файле обязан этому отвечать.
            var sorted = new List<BlobchegRouterCell>(cells);
            sorted.Sort((a, b) => a.Bit.CompareTo(b.Bit));

            foreach (var cell in sorted)
            {
                if (cell.Bit < 0 || cell.Bit >= _domainCount)
                    throw new ArgumentOutOfRangeException(nameof(cells),
                        $"Blobcheg: роутер '{RouterName}' — бит {cell.Bit} при {_domainCount} базах");

                var bit = 1ul << cell.Bit;
                if ((mask & bit) != 0)
                    throw new InvalidOperationException(
                        $"Blobcheg: роутер '{RouterName}' — нода '{nodeName}' дважды указала базу {cell.Bit}");

                mask |= bit;
                _offsets.Add(cell.Offset);
            }

            _rowStart.Add(start);
            _masks.Add(mask);
            _names.Add(nodeName ?? string.Empty);
            return (uint)(_masks.Count - 1);
        }

        public void Flush(bool withDebug = false)
        {
            if (_flushed)
                throw new InvalidOperationException($"Blobcheg: повторный Flush роутера '{RouterName}'");

            var count = _masks.Count;

            var masksOffset = BlobchegFormat.AlignUp(BlobchegRouterFormat.PrologOffset + BlobchegRouterFormat.PrologSize);
            var rowStartOffset = BlobchegFormat.AlignUp(masksOffset + count * _maskWidth);
            var offsetsOffset = BlobchegFormat.AlignUp(rowStartOffset + (count + 1) * 4);
            var position = offsetsOffset + _offsets.Count * 4;

            var debugOffset = 0;
            byte[] debugSection = null;
            if (withDebug)
            {
                position = BlobchegFormat.AlignUp(position);
                debugOffset = position;
                debugSection = BuildDebugSection((uint)debugOffset);
                position += debugSection.Length;
            }

            var file = new byte[position];

            BlobchegBytes.WriteU32(file, BlobchegRouterFormat.PrologOffset + 0, (uint)count);
            BlobchegBytes.WriteU32(file, BlobchegRouterFormat.PrologOffset + 4, (uint)_domainCount);
            BlobchegBytes.WriteU64(file, BlobchegRouterFormat.PrologOffset + 8, _layoutHash);
            BlobchegBytes.WriteU32(file, BlobchegRouterFormat.PrologOffset + 16, (uint)masksOffset);
            BlobchegBytes.WriteU32(file, BlobchegRouterFormat.PrologOffset + 20, (uint)rowStartOffset);
            BlobchegBytes.WriteU32(file, BlobchegRouterFormat.PrologOffset + 24, (uint)offsetsOffset);
            BlobchegBytes.WriteU32(file, BlobchegRouterFormat.PrologOffset + 28, (uint)_maskWidth);

            for (var i = 0; i < count; i++)
                BlobchegBytes.WriteMask(file, masksOffset + i * _maskWidth, _masks[i], _maskWidth);

            for (var i = 0; i < count; i++)
                BlobchegBytes.WriteU32(file, rowStartOffset + i * 4, (uint)_rowStart[i]);

            BlobchegBytes.WriteU32(file, rowStartOffset + count * 4, (uint)_offsets.Count);

            for (var i = 0; i < _offsets.Count; i++)
                BlobchegBytes.WriteU32(file, offsetsOffset + i * 4, _offsets[i]);

            if (debugSection != null)
                Buffer.BlockCopy(debugSection, 0, file, debugOffset, debugSection.Length);

            var flags = (ushort)(BlobchegFormat.FlagRouter | (withDebug ? BlobchegFormat.FlagHasDebug : 0));
            ContentHash = BlobchegBytes.Seal(file, flags, (uint)debugOffset);

            _flushed = true;
            FileChanged = BlobchegBytes.WriteIfChanged(Directory, FilePath, file, ContentHash);
        }

        /// <summary>Имя ноды по id — только для инструментов едитора, поэтому за BLOBCHEG_DEBUG.</summary>
        byte[] BuildDebugSection(uint sectionOffset)
        {
            var count = _masks.Count;
            var namesStart = sectionOffset + 8 + (uint)(count * 4);

            var names = new MemoryStream();
            var nameOffsets = new uint[count];
            for (var i = 0; i < count; i++)
            {
                nameOffsets[i] = namesStart + (uint)names.Length;
                BlobchegBytes.WriteString(names, _names[i]);
            }

            var section = new byte[8 + count * 4 + (int)names.Length];
            BlobchegBytes.WriteU32(section, 0, BlobchegRouterFormat.DebugMagic);
            BlobchegBytes.WriteU32(section, 4, (uint)count);

            for (var i = 0; i < count; i++)
                BlobchegBytes.WriteU32(section, 8 + i * 4, nameOffsets[i]);

            var body = names.ToArray();
            Buffer.BlockCopy(body, 0, section, 8 + count * 4, body.Length);
            return section;
        }
    }
}
