using System;
using System.Collections.Generic;
using System.Reflection;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;

namespace Blobcheg.Authoring
{
    interface IBlobchegOpenBuilder
    {
        bool Closed { get; }

        string RecordTypeName { get; }

        void Abandon(); // frees the chunks without assembling: a failed Write or a forgotten End
    }

    public static class BlobchegBuilder // one record's bytes outside a rebuild: no collector, file or domain
    {
        public static BlobchegBuilder<TRoot> Open<TRoot>(string name, Action<byte[]> sink)
            where TRoot : unmanaged
        {
            if (sink == null)
                throw new ArgumentNullException(nameof(sink),
                    "Blobcheg: a builder without a sink — the assembled record would have nowhere to go");

            BlobchegRecordTypes.Require(typeof(TRoot));
            return new BlobchegBuilder<TRoot>(name, sink);
        }
    }

    // Record size is known only after every Allocate; the chunks do not move before End.
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

            UnsafeUtility.MemClear(head.Ptr, head.Bytes); // zeroes: unfilled fields read empty, padding stays deterministic
            _chunks.Add(head);
        }

        public bool Closed => _closed;

        public string RecordTypeName => typeof(TRoot).FullName;

        public ref TRoot Root
        {
            get
            {
                RequireOpen(nameof(Root));
                return ref *(TRoot*)_chunks[0].Ptr;
            }
        }

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

            if (length == 0) // legal: the field stays zero, no chunk, the read never dereferences
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

        // Chunks land behind the head in Allocate order, each aligned from the record start.
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

        string FieldNameAt(int chunkIndex, int fieldOffset)
        {
            var type = typeof(TRoot);
            if (chunkIndex > 0) // array chunk: the element type comes from the patch that created it
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
            var ownerType = patch.OwnerChunk == 0 ? typeof(TRoot) : null; // not stored: recovered from the owning field
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

    // A write window into an allocated array; stays valid across a neighbouring Allocate.
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
                if (_owner.Closed) // outlived End: points into freed memory
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
