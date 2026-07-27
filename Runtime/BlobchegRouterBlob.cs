using System;
using System.Runtime.CompilerServices;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;

namespace Blobcheg
{
    /// <summary>
    /// Строка роутера: одна нода во всех базах сразу. Маска говорит, в каких базах она есть,
    /// оффсеты лежат подряд без дырок — отсюда <c>flag → index</c> это popcount младших бит.
    ///
    /// Сообщения исключений — литералы: под Бёрстом интерполяция не компилируется.
    /// </summary>
    public readonly unsafe struct BlobchegRouterRow
    {
        // Указатель в чужом буфере: строка живёт ровно столько, сколько поднятый роутер.
        [NativeDisableUnsafePtrRestriction]
        readonly uint* _offsets;

        readonly ulong _mask;

        internal BlobchegRouterRow(uint* offsets, ulong mask)
        {
            _offsets = offsets;
            _mask = mask;
        }

        /// <summary>Битовая маска баз, в которых нода есть. Кодоген отдаёт её своим enum'ом.</summary>
        public ulong Mask => _mask;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Has(int bit) => (_mask & (1ul << bit)) != 0;

        /// <summary>
        /// Оффсет записи в базе <paramref name="bit"/>. Записи нет — бросает: сентинела «нет записи»
        /// в пакете не существует, молчаливый ноль поехал бы в <c>Read</c> и лёг бы в чужие байты.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public uint Offset(int bit)
        {
            if (!Has(bit))
                throw new InvalidOperationException(
                    "Blobcheg.Router: у этой ноды нет записи в этой базе — спрашивай Has или TryGet");

            return _offsets[math.countbits(_mask & ((1ul << bit) - 1))];
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryOffset(int bit, out uint offset)
        {
            if (!Has(bit))
            {
                offset = 0;
                return false;
            }

            offset = _offsets[math.countbits(_mask & ((1ul << bit) - 1))];
            return true;
        }
    }

    /// <summary>
    /// Резидентный буфер роутера. Всю работу делает он; типизированный фасад
    /// (<c>[BlobchegRouter]</c>-партиал) — тонкая обёртка сверху, знающая номера бит своих баз.
    /// </summary>
    public unsafe struct BlobchegRouterBlob : IDisposable
    {
        BlobchegBuffer _buffer;
        byte* _masks;
        uint* _rowStart;
        uint* _offsets;
        uint _count;
        uint _maskWidth;
        uint _debugOffset;

        /// <summary>Забирает владение буфером, валидирует header, целостность и пролог.</summary>
        public BlobchegRouterBlob(BlobchegBuffer buffer, string what, int domainCount, ulong layoutHash)
        {
            if (!buffer.IsCreated)
                throw new ArgumentException($"Blobcheg: пустой буфер роутера '{what}'", nameof(buffer));

            _buffer = buffer;
            _debugOffset = 0;

            ref var header = ref UnsafeUtility.AsRef<BlobchegHeader>(buffer.Ptr);
            var contentHash = BlobchegHash.Of(
                buffer.Ptr + BlobchegFormat.HeaderSize, buffer.Length - BlobchegFormat.HeaderSize);

            header.Validate(what, buffer.Length, contentHash, true);

            if (buffer.Length < BlobchegRouterFormat.PrologOffset + BlobchegRouterFormat.PrologSize)
                throw new InvalidOperationException($"Blobcheg: роутер '{what}' короче пролога");

            ref var prolog = ref UnsafeUtility.AsRef<BlobchegRouterProlog>(buffer.Ptr + BlobchegRouterFormat.PrologOffset);
            prolog.Validate(what, buffer.Length, domainCount, layoutHash);

            _count = prolog.Count;
            _maskWidth = prolog.MaskWidth;
            _masks = buffer.Ptr + prolog.MasksOffset;
            _rowStart = (uint*)(buffer.Ptr + prolog.RowStartOffset);
            _offsets = (uint*)(buffer.Ptr + prolog.OffsetsOffset);

            if (header.HasDebug)
            {
                if (*(uint*)(buffer.Ptr + header.DebugOffset) != BlobchegRouterFormat.DebugMagic)
                    throw new InvalidOperationException(
                        $"Blobcheg: роутер '{what}' — debug-секция не там, где обещал header");

                _debugOffset = header.DebugOffset;
            }
        }

        public bool IsCreated => _buffer.IsCreated;

        /// <summary>Сколько строк, то есть нод. Он же потолок валидного id.</summary>
        public int Count => (int)_count;

        public bool HasDebug => _debugOffset != 0;

        /// <summary>
        /// Строка ноды. Проверка id НЕ за дефайном: это одно сравнение, а протухший id в билде читал
        /// бы чужую память.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public BlobchegRouterRow Get(BlobchegId id)
        {
            if (id.Value >= _count)
                throw new InvalidOperationException(
                    "Blobcheg.Router: неизвестный id — строки с таким номером в роутере нет");

            return new BlobchegRouterRow(_offsets + _rowStart[id.Value], MaskOf(id.Value));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryGet(BlobchegId id, out BlobchegRouterRow row)
        {
            if (id.Value >= _count)
            {
                row = default;
                return false;
            }

            row = new BlobchegRouterRow(_offsets + _rowStart[id.Value], MaskOf(id.Value));
            return true;
        }

        public void Dispose()
        {
            _buffer.Dispose();
            _masks = null;
            _rowStart = null;
            _offsets = null;
            _count = 0;
            _debugOffset = 0;
        }

        /// <summary>Имя ноды по id — только для инструментов едитора; без BLOBCHEG_DEBUG секции нет.</summary>
        public string Describe(BlobchegId id)
        {
            if (_debugOffset == 0)
                throw new InvalidOperationException(
                    "Blobcheg.Router.Describe: в файле нет отладочного контура — он собран без BLOBCHEG_DEBUG");

            if (id.Value >= _count)
                throw new InvalidOperationException($"Blobcheg.Router.Describe: id {id.Value} при {_count} строках");

            var nameOffset = *(uint*)(_buffer.Ptr + _debugOffset + 8 + id.Value * 4);
            var p = _buffer.Ptr + nameOffset;
            var length = *(ushort*)p;
            return System.Text.Encoding.UTF8.GetString(p + 2, length);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        ulong MaskOf(uint id)
        {
            switch (_maskWidth)
            {
                case 1: return _masks[id];
                case 2: return *(ushort*)(_masks + id * 2);
                case 4: return *(uint*)(_masks + id * 4);
                default: return *(ulong*)(_masks + id * 8);
            }
        }
    }
}
