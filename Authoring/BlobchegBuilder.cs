using System;
using System.Collections.Generic;
using System.Reflection;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;

namespace Blobcheg.Authoring
{
    /// <summary>Открытый билдер глазами коллектора: закрыть брошенный и освободить память.</summary>
    interface IBlobchegOpenBuilder
    {
        bool Closed { get; }

        string RecordTypeName { get; }

        /// <summary>Освобождает чанки без сборки записи — путь упавшего или забывшего End Write.</summary>
        void Abandon();
    }

    /// <summary>
    /// Сборщик записи с массивами. Размер записи известен только после всех
    /// <see cref="Allocate{T}"/>, поэтому структ-литерал не годится: билдер держит голову и по
    /// чанку unmanaged-памяти на массив, а <see cref="End"/> раскладывает чанки хвостом за головой,
    /// заполняет само-относительные оффсеты и отдаёт байты коллектору тем же маршрутом, что Add.
    ///
    /// Чанки не переезжают до End, поэтому <see cref="BlobchegBuilderArray{T}"/> соседнего массива
    /// можно держать через Allocate следующего.
    /// </summary>
    public sealed unsafe class BlobchegBuilder<TRoot> : IBlobchegOpenBuilder where TRoot : unmanaged
    {
        struct Chunk
        {
            public byte* Ptr;
            public int Bytes;
            public int Align;
        }

        struct Patch
        {
            public int OwnerChunk;
            public int FieldOffset;
            public int TargetChunk;
            public int Elements;
        }

        readonly string _nodeName;
        readonly Action<byte[]> _sink;
        readonly List<Chunk> _chunks = new List<Chunk>();
        readonly List<Patch> _patches = new List<Patch>();
        readonly HashSet<long> _boundFields = new HashSet<long>();

        bool _closed;

        internal BlobchegBuilder(string nodeName, Action<byte[]> sink)
        {
            _nodeName = nodeName;
            _sink = sink;

            var head = new Chunk
            {
                Ptr = (byte*)UnsafeUtility.Malloc(UnsafeUtility.SizeOf<TRoot>(),
                    BlobchegFormat.RecordAlign, Allocator.Persistent),
                Bytes = UnsafeUtility.SizeOf<TRoot>(),
                Align = BlobchegFormat.RecordAlign,
            };

            // Нули, а не мусор аллокатора: незаполненное поле обязано читаться как ноль и пустой
            // массив, и падинги обязаны быть детерминированными — на байтах записи стоит ревизия.
            UnsafeUtility.MemClear(head.Ptr, head.Bytes);
            _chunks.Add(head);
        }

        public bool Closed => _closed;

        public string RecordTypeName => typeof(TRoot).FullName;

        /// <summary>Голова записи; поля заполняются как обычно. После End — ошибка.</summary>
        public ref TRoot Root
        {
            get
            {
                RequireOpen(nameof(Root));
                return ref *(TRoot*)_chunks[0].Ptr;
            }
        }

        /// <summary>
        /// Резервирует место под массив и привязывает его к полю. Поле обязано лежать в этой же
        /// записи — в голове или в элементе уже выделенного массива (так строится вложенность).
        /// </summary>
        public BlobchegBuilderArray<T> Allocate<T>(ref BlobchegArray<T> field, int length) where T : unmanaged
        {
            RequireOpen(nameof(Allocate));

            if (length < 0)
                throw new ArgumentOutOfRangeException(nameof(length),
                    $"Blobcheg: нода '{_nodeName}' просит массив '{typeof(T).Name}' отрицательной длины {length}");

            if (UnsafeUtility.AlignOf<T>() > BlobchegFormat.RecordAlign)
                throw new InvalidOperationException(
                    $"Blobcheg: у элемента '{typeof(T).FullName}' выравнивание {UnsafeUtility.AlignOf<T>()} " +
                    $"больше выравнивания записи {BlobchegFormat.RecordAlign} — внутри записи его не обеспечить");

            var fieldAddress = (byte*)UnsafeUtility.AddressOf(ref field);
            var owner = OwnerOf(fieldAddress);
            if (owner < 0)
                throw new InvalidOperationException(
                    $"Blobcheg: нода '{_nodeName}' привязывает массив к полю не из этой записи — " +
                    $"ref обязан указывать в Root или в элемент уже выделенного массива '{typeof(TRoot).Name}'");

            var fieldOffset = (int)(fieldAddress - _chunks[owner].Ptr);
            if (!_boundFields.Add((long)owner << 32 | (uint)fieldOffset))
                throw new InvalidOperationException(
                    $"Blobcheg: нода '{_nodeName}' выделяет массив в поле " +
                    $"'{FieldNameAt(owner, fieldOffset)}' второй раз — второй Allocate осиротил бы первый");

            // Пустой массив легален: поле остаётся нулём, чанка нет, чтение — без разыменования.
            if (length == 0)
            {
                *(int*)fieldAddress = 0;
                *((int*)fieldAddress + 1) = 0;
                return new BlobchegBuilderArray<T>(null, 0, _nodeName);
            }

            var chunk = new Chunk
            {
                Ptr = (byte*)UnsafeUtility.Malloc((long)length * sizeof(T),
                    UnsafeUtility.AlignOf<T>(), Allocator.Persistent),
                Bytes = length * sizeof(T),
                Align = UnsafeUtility.AlignOf<T>(),
            };
            UnsafeUtility.MemClear(chunk.Ptr, chunk.Bytes);
            _chunks.Add(chunk);

            _patches.Add(new Patch
            {
                OwnerChunk = owner,
                FieldOffset = fieldOffset,
                TargetChunk = _chunks.Count - 1,
                Elements = length,
            });

            return new BlobchegBuilderArray<T>((T*)chunk.Ptr, length, _nodeName);
        }

        /// <summary>
        /// Считает раскладку: чанки ложатся за головой в порядке Allocate, каждый выровнен на
        /// AlignOf своего элемента от начала записи. Заполняет оффсеты, собирает байты, отдаёт их
        /// коллектору и освобождает память.
        /// </summary>
        public void End()
        {
            RequireOpen(nameof(End));

            var starts = new int[_chunks.Count];
            var position = 0;
            for (var i = 0; i < _chunks.Count; i++)
            {
                var align = _chunks[i].Align;
                position = (position + align - 1) / align * align;
                starts[i] = position;
                position += _chunks[i].Bytes;
            }

            foreach (var patch in _patches)
            {
                var fieldAt = _chunks[patch.OwnerChunk].Ptr + patch.FieldOffset;
                *(int*)fieldAt = starts[patch.TargetChunk] - (starts[patch.OwnerChunk] + patch.FieldOffset);
                *((int*)fieldAt + 1) = patch.Elements;
            }

            var bytes = new byte[position];
            fixed (byte* destination = bytes)
            {
                for (var i = 0; i < _chunks.Count; i++)
                    UnsafeUtility.MemCpy(destination + starts[i], _chunks[i].Ptr, _chunks[i].Bytes);
            }

            Free();
            _sink(bytes);
        }

        public void Abandon() => Free();

        void Free()
        {
            foreach (var chunk in _chunks)
                UnsafeUtility.Free(chunk.Ptr, Allocator.Persistent);

            _chunks.Clear();
            _closed = true;
        }

        void RequireOpen(string what)
        {
            if (_closed)
                throw new InvalidOperationException(
                    $"Blobcheg: {what} у ноды '{_nodeName}' после End — запись '{typeof(TRoot).Name}' уже собрана");
        }

        int OwnerOf(byte* fieldAddress)
        {
            for (var i = 0; i < _chunks.Count; i++)
            {
                if (fieldAddress >= _chunks[i].Ptr
                    && fieldAddress + sizeof(int) * 2 <= _chunks[i].Ptr + _chunks[i].Bytes)
                    return i;
            }

            return -1;
        }

        /// <summary>Имя поля по смещению в чанке — для текста ошибки. Не нашлось — само смещение.</summary>
        string FieldNameAt(int chunkIndex, int fieldOffset)
        {
            // У головы тип — TRoot; у чанка массива тип элемента восстанавливается по патчу,
            // который этот чанк завёл.
            var type = typeof(TRoot);
            if (chunkIndex > 0)
            {
                foreach (var patch in _patches)
                {
                    if (patch.TargetChunk != chunkIndex)
                        continue;

                    var elementBytes = _chunks[chunkIndex].Bytes / patch.Elements;
                    return FieldNameIn(ElementTypeOf(patch), fieldOffset % elementBytes)
                           ?? "@" + fieldOffset;
                }

                return "@" + fieldOffset;
            }

            return FieldNameIn(type, fieldOffset) ?? "@" + fieldOffset;
        }

        Type ElementTypeOf(Patch patch)
        {
            // Тип элемента чанка в патчах не хранится: восстанавливается по полю-владельцу.
            var ownerType = patch.OwnerChunk == 0 ? typeof(TRoot) : null;
            if (ownerType == null)
                return null;

            var field = FieldAt(ownerType, patch.FieldOffset);
            return field != null && field.FieldType.IsGenericType
                ? field.FieldType.GenericTypeArguments[0]
                : null;
        }

        static string FieldNameIn(Type type, int offset)
        {
            if (type == null)
                return null;

            var field = FieldAt(type, offset);
            return field?.Name;
        }

        static FieldInfo FieldAt(Type type, int offset)
        {
            foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (UnsafeUtility.GetFieldOffset(field) == offset)
                    return field;
            }

            return null;
        }
    }

    /// <summary>
    /// Окно записи в выделенный массив: указатель и длина. ref struct — жить дольше Write ему
    /// незачем, а чанки билдера до End не переезжают, поэтому держать окно через соседний Allocate
    /// можно.
    /// </summary>
    public unsafe ref struct BlobchegBuilderArray<T> where T : unmanaged
    {
        readonly T* _ptr;
        readonly int _length;
        readonly string _nodeName;

        internal BlobchegBuilderArray(T* ptr, int length, string nodeName)
        {
            _ptr = ptr;
            _length = length;
            _nodeName = nodeName;
        }

        public int Length => _length;

        public ref T this[int index]
        {
            get
            {
                if ((uint)index >= (uint)_length)
                    throw new IndexOutOfRangeException(
                        $"Blobcheg: нода '{_nodeName}' пишет в элемент {index} массива длины {_length}");

                return ref _ptr[index];
            }
        }
    }
}
