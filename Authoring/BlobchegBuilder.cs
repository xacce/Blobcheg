using System;
using System.Collections.Generic;
using System.Reflection;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;

namespace Blobcheg.Authoring
{
    /// <summary>An open builder as the collector sees it: close an abandoned one and free the memory.</summary>
    interface IBlobchegOpenBuilder
    {
        bool Closed { get; }

        string RecordTypeName { get; }

        /// <summary>Frees the chunks without assembling the record — the path of a Write that failed or forgot End.</summary>
        void Abandon();
    }

    /// <summary>
    /// The assembler of a record with arrays. The size of the record is known only after all the
    /// <see cref="Allocate{T}"/> calls, so a struct literal will not do: the builder holds the head and
    /// one chunk of unmanaged memory per array, and <see cref="End"/> lays the chunks out as a tail
    /// behind the head, fills the self-relative offsets and hands the bytes to the collector by the
    /// same route as Add.
    ///
    /// The chunks do not move before End, so a <see cref="BlobchegBuilderArray{T}"/> of a neighbouring
    /// array may be held across the Allocate of the next one.
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

            // Zeroes and not allocator garbage: an unfilled field is obliged to read as zero and as an
            // empty array, and the padding is obliged to be deterministic — the revision stands on the
            // bytes of the record.
            UnsafeUtility.MemClear(head.Ptr, head.Bytes);
            _chunks.Add(head);
        }

        public bool Closed => _closed;

        public string RecordTypeName => typeof(TRoot).FullName;

        /// <summary>The head of the record; the fields are filled as usual. After End — an error.</summary>
        public ref TRoot Root
        {
            get
            {
                RequireOpen(nameof(Root));
                return ref *(TRoot*)_chunks[0].Ptr;
            }
        }

        /// <summary>
        /// Reserves room for an array and binds it to a field. The field is obliged to lie in this same
        /// record — in the head or in an element of an already allocated array (that is how nesting is
        /// built).
        /// </summary>
        public BlobchegBuilderArray<T> Allocate<T>(ref BlobchegArray<T> field, int length) where T : unmanaged
        {
            RequireOpen(nameof(Allocate));

            if (length < 0)
                throw new ArgumentOutOfRangeException(nameof(length),
                    $"Blobcheg: node '{_nodeName}' asks for an array of '{typeof(T).Name}' of negative length {length}");

            if (UnsafeUtility.AlignOf<T>() > BlobchegFormat.RecordAlign)
                throw new InvalidOperationException(
                    $"Blobcheg: element '{typeof(T).FullName}' has alignment {UnsafeUtility.AlignOf<T>()}, " +
                    $"greater than the record alignment {BlobchegFormat.RecordAlign} — it cannot be provided inside a record");

            var fieldAddress = (byte*)UnsafeUtility.AddressOf(ref field);
            var owner = OwnerOf(fieldAddress);
            if (owner < 0)
                throw new InvalidOperationException(
                    $"Blobcheg: node '{_nodeName}' binds an array to a field that is not from this record — " +
                    $"the ref is obliged to point into Root or into an element of an already allocated array of '{typeof(TRoot).Name}'");

            var fieldOffset = (int)(fieldAddress - _chunks[owner].Ptr);
            if (!_boundFields.Add((long)owner << 32 | (uint)fieldOffset))
                throw new InvalidOperationException(
                    $"Blobcheg: node '{_nodeName}' allocates an array in field " +
                    $"'{FieldNameAt(owner, fieldOffset)}' a second time — a second Allocate would orphan the first");

            // An empty array is legal: the field stays zero, there is no chunk, and the read happens
            // without dereferencing.
            if (length == 0)
            {
                *(int*)fieldAddress = 0;
                *((int*)fieldAddress + 1) = 0;
                return new BlobchegBuilderArray<T>(null, 0, _nodeName, this);
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

            return new BlobchegBuilderArray<T>((T*)chunk.Ptr, length, _nodeName, this);
        }

        /// <summary>
        /// Computes the layout: the chunks land behind the head in Allocate order, each aligned to the
        /// AlignOf of its element from the start of the record. It fills the offsets, assembles the
        /// bytes, hands them to the collector and frees the memory.
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
                    $"Blobcheg: {what} on node '{_nodeName}' after End — record '{typeof(TRoot).Name}' is already assembled");
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

        /// <summary>The field name by its offset in a chunk — for the error text. If not found, the offset itself.</summary>
        string FieldNameAt(int chunkIndex, int fieldOffset)
        {
            // The head's type is TRoot; for an array chunk the element type is recovered from the patch
            // that created that chunk.
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
            // The element type of a chunk is not stored in the patches: it is recovered from the owning
            // field.
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
    /// A window for writing into an allocated array: a pointer and a length. A ref struct — it has no
    /// reason to live longer than Write, and the builder's chunks do not move before End, so the window
    /// may be held across a neighbouring Allocate.
    /// </summary>
    public unsafe ref struct BlobchegBuilderArray<T> where T : unmanaged
    {
        readonly T* _ptr;
        readonly int _length;
        readonly string _nodeName;
        readonly IBlobchegOpenBuilder _owner;

        internal BlobchegBuilderArray(T* ptr, int length, string nodeName, IBlobchegOpenBuilder owner)
        {
            _ptr = ptr;
            _length = length;
            _nodeName = nodeName;
            _owner = owner;
        }

        public int Length => _length;

        public ref T this[int index]
        {
            get
            {
                // A window that outlived End points into freed memory — writing there is not allowed
                // under any circumstances, and neither is staying silent about it.
                if (_owner.Closed)
                    throw new InvalidOperationException(
                        $"Blobcheg: node '{_nodeName}' writes into an array window after End — the record is " +
                        "already assembled and the chunk memory is freed. Fill the array before End");

                if ((uint)index >= (uint)_length)
                    throw new IndexOutOfRangeException(
                        $"Blobcheg: node '{_nodeName}' writes into element {index} of an array of length {_length}");

                return ref _ptr[index];
            }
        }
    }
}
