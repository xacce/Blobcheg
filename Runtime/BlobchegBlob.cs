using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Unity.Burst;
using Unity.Collections.LowLevel.Unsafe;

namespace Blobcheg
{
    /// <summary>
    /// Резидентный буфер одной базы. Всю работу делает он; типизированный фасад
    /// (<c>[Blobcheg]</c>-партиал) — тонкая обёртка сверху, добавляющая констрейнт домена.
    ///
    /// Чтение — реинтерпретация по оффсету. Что лежит внутри записи, буфер не знает и знать не
    /// должен: это вопрос доверия. Всегда проверяются целостность файла и его личность (разово, на
    /// подъёме); границы и тип записи — за <c>ENABLE_UNITY_COLLECTIONS_CHECKS</c>, то есть в
    /// редакторе и в development-билде. Тип сверяется по отладочному контуру, а его в релизном
    /// плеере нет — там чтение снова становится чистой реинтерпретацией.
    /// </summary>
    public unsafe struct BlobchegBlob : IDisposable
    {
        BlobchegBuffer _buffer;
        uint _debugOffset;
        ulong _domainKey;
        int _version;

        /// <summary>Забирает владение буфером, валидирует header и целостность.</summary>
        public BlobchegBlob(BlobchegBuffer buffer, string what)
        {
            if (!buffer.IsCreated)
                throw new ArgumentException($"Blobcheg: пустой буфер базы '{what}'", nameof(buffer));

            _buffer = buffer;
            _debugOffset = 0;
            _domainKey = 0;

            // Номер снимается здесь, а не тем, кто поднимает: подъёмов три — кодогенный, рукописный
            // и тестовый, — и забытый номер выглядит как «база не менялась», то есть врёт молча.
#if UNITY_EDITOR
            _version = BlobchegFileVersions.Of(BlobchegNaming.FileName(what));
#else
            _version = 0;
#endif

            ref var header = ref UnsafeUtility.AsRef<BlobchegHeader>(buffer.Ptr);
            var contentHash = BlobchegHash.Of(
                buffer.Ptr + BlobchegFormat.HeaderSize, buffer.Length - BlobchegFormat.HeaderSize);

            header.Validate(what, buffer.Length, contentHash);

            if (header.HasDebug)
            {
                BlobchegDebugSection.ValidateProlog(*(uint*)(buffer.Ptr + header.DebugOffset));
                _debugOffset = header.DebugOffset;
            }

            // Личность домена уже проверена выше, значит и ключ реестра — она же. Регистрируемся
            // здесь, а не в патче: базу поднимают и без Entities, а вопрос «в слоте адрес или ещё
            // оффсет» задают все одинаково.
            _domainKey = header.NameHash;
            BlobchegDomainNames.Remember(_domainKey, what);
            BlobchegBases.Register(_domainKey, buffer.Ptr, buffer.Length, _debugOffset);
        }

        /// <summary>Ключ домена в <see cref="BlobchegBases"/> — личность файла из header'а.</summary>
        public ulong DomainKey => _domainKey;

        /// <summary>
        /// Номер пересборки файла, из которого прочитан ЭТОТ буфер. Нужен тому, у кого от базы есть
        /// производное — кеш, таблица, чертёж: он держит свою отметку и сверяет её с этим числом.
        /// Разошлись — производное собрано по прошлой базе.
        ///
        /// В плеере всегда ноль: там файл никто не переписывает, и производное протухнуть не может.
        /// </summary>
        public int Version => _version;

        public bool IsCreated => _buffer.IsCreated;

        public int Length => _buffer.Length;

        /// <summary>Есть ли в файле отладочный контур. В релизном билде его не бывает.</summary>
        public bool HasDebug => _debugOffset != 0;

        /// <summary>
        /// Единственный способ достать запись. Оффсет потребитель хранит сам — в
        /// <see cref="BlobchegRefSo"/> и больше нигде.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ref readonly T Read<T>(uint offset) where T : unmanaged
        {
            CheckRead<T>(offset);
            return ref UnsafeUtility.AsRef<T>(_buffer.Ptr + offset);
        }

        public void Dispose()
        {
            if (_domainKey != 0)
            {
                BlobchegBases.Unregister(_domainKey, _buffer.Ptr);
                _domainKey = 0;
            }

            _buffer.Dispose();
            _debugOffset = 0;
        }

        [Conditional("ENABLE_UNITY_COLLECTIONS_CHECKS")]
        void CheckRead<T>(uint offset) where T : unmanaged
        {
            if (_buffer.Ptr == null)
                throw new InvalidOperationException("Blobcheg.Read: база не поднята");

            if ((offset & (BlobchegFormat.RecordAlign - 1)) != 0)
                throw new InvalidOperationException("Blobcheg.Read: оффсет не выровнен на 16 — это не начало записи");

            if (offset < BlobchegFormat.HeaderSize || offset + UnsafeUtility.SizeOf<T>() > (uint)_buffer.Length)
                throw new InvalidOperationException("Blobcheg.Read: запись не помещается в буфер базы");

            CheckType<T>(offset);
        }

        /// <summary>
        /// Отладочный контур: есть ли по этому адресу запись и та ли она. Зовётся из
        /// <see cref="CheckRead{T}"/>, то есть живёт под тем же <c>ENABLE_UNITY_COLLECTIONS_CHECKS</c> —
        /// в редакторе и в development-билде. Раньше он висел на отдельном <c>BLOBCHEG_DEBUG</c>,
        /// которого не ставил никто, и единственная проверка типа существовала на бумаге.
        ///
        /// Секции в файле может не быть (релизный билд, файл, собранный чужим инструментом) — тогда
        /// проверять нечем, и это не ошибка чтения.
        /// </summary>
        void CheckType<T>(uint offset) where T : unmanaged
        {
            if (_debugOffset == 0)
                return;

            var entry = BlobchegDebugSection.Find(_buffer.Ptr, _debugOffset, offset);
            if (entry == null)
                throw new InvalidOperationException("Blobcheg.Read: по этому оффсету записи нет");

            if (entry->TypeHash != unchecked((uint)BurstRuntime.GetHashCode32<T>()))
                throw new InvalidOperationException("Blobcheg.Read: по этому оффсету лежит запись другого типа");
        }

        /// <summary>
        /// Имена типа и ноды по оффсету — только для инструментов едитора. Спрашивать имеет смысл
        /// после <see cref="HasDebug"/>; без секции или без записи по оффсету — ошибка, а не пустой ответ.
        /// </summary>
        public void Describe(uint offset, out string typeName, out string nodeName)
        {
            if (_debugOffset == 0)
                throw new InvalidOperationException(
                    "Blobcheg.Describe: в файле нет отладочного контура — он собран для релизного плеера");

            var entry = BlobchegDebugSection.Find(_buffer.Ptr, _debugOffset, offset);
            if (entry == null)
                throw new InvalidOperationException($"Blobcheg.Describe: по оффсету {offset} записи нет");

            BlobchegDebugSection.ReadNames(_buffer.Ptr, *entry, out typeName, out nodeName);
        }
    }
}
