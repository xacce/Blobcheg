using System;
using System.Diagnostics;
using Unity.Burst;
using Unity.Collections.LowLevel.Unsafe;

namespace Blobcheg
{
    /// <summary>
    /// Типизированный массив переменной длины внутри записи: восемь байт — само-относительный
    /// оффсет и длина. Оффсет меряется от адреса ЭТОГО поля, хвост лежит внутри байтового блока
    /// той же записи, поэтому запись остаётся непрозрачным блоком, который ездит по файлу целиком:
    /// ни Flush, ни целостность, ни ревизия, ни патч ссылок о массиве не знают.
    ///
    /// Все члены — readonly намеренно. <see cref="BlobchegBlob.Read{T}"/> отдаёт
    /// <c>ref readonly</c>, и доступ к не-readonly члену через такую ссылку компилятор обслуживает
    /// защитной копией — а копия имеет другой адрес, и само-относительный оффсет из неё ведёт в
    /// никуда молча и на нормальном пути.
    ///
    /// Заполняет поле только билдер записи в едиторе. Нулевой оффсет — пустой массив, он читается
    /// без разыменования.
    /// </summary>
    public unsafe struct BlobchegArray<T> where T : unmanaged
    {
        internal int _offset;   // байты от адреса этого поля до первого элемента; 0 — пусто
        internal int _length;

        public readonly int Length => _length;

        public readonly bool IsEmpty => _length == 0;

        public readonly ref readonly T this[int index]
        {
            get
            {
                fixed (int* self = &_offset)
                {
                    var element = (byte*)self + _offset + (long)index * sizeof(T);
                    CheckElement(index, element);
                    return ref *(T*)element;
                }
            }
        }

        /// <summary>
        /// Указатель на первый элемент — форма для горячего цикла: адрес проверяется один раз,
        /// дальше цикл бесплатный. У пустого массива указателя нет — <c>null</c> без разыменования.
        /// </summary>
        public readonly T* GetUnsafePtr()
        {
            if (_length == 0)
                return null;

            fixed (int* self = &_offset)
            {
                var first = (byte*)self + _offset;
                CheckSpan(first, first + (long)_length * sizeof(T) - 1);
                return (T*)first;
            }
        }

        [Conditional("ENABLE_UNITY_COLLECTIONS_CHECKS")]
        readonly void CheckElement(int index, byte* element)
        {
            if ((uint)index >= (uint)_length)
                throw new IndexOutOfRangeException("Blobcheg: индекс за границей массива записи");

            CheckSpan(element, element + sizeof(T) - 1);
        }

        /// <summary>
        /// Первый и последний байт диапазона обязаны лежать в буфере какой-то поднятой базы.
        /// Кратность проверяется у абсолютного адреса элемента, а не у самого оффсета: поле массива
        /// может лежать в записи на 4 при 8-байтовом элементе, и тогда оффсет некратен при
        /// правильно выровненном элементе — важен адрес, по которому читают.
        /// </summary>
        [Conditional("ENABLE_UNITY_COLLECTIONS_CHECKS")]
        readonly void CheckSpan(byte* first, byte* last)
        {
            if (_offset == 0)
                throw new InvalidOperationException(
                    "Blobcheg: у непустого массива нулевой оффсет — поле не заполнял билдер записи");

            if ((ulong)first % (ulong)UnsafeUtility.AlignOf<T>() != 0)
                throw new InvalidOperationException(
                    "Blobcheg: адрес элемента не кратен выравниванию его типа — оффсет массива бит");

            if (BlobchegBases.IsKnownAddress((ulong)first) && BlobchegBases.IsKnownAddress((ulong)last))
                return;

            ThrowCopied();
            throw new InvalidOperationException(
                "Blobcheg: адрес элемента вне буферов поднятых баз — запись скопирована из блоба " +
                "по значению, а само-относительный оффсет живёт только по исходному адресу. " +
                "Держите запись как ref readonly, не копируйте её в локальную переменную");
        }

        /// <summary>
        /// Managed-версия той же ошибки, с именем типа элемента: это самая частая человеческая
        /// ошибка, и её обязано быть видно без угадывания. Под Burst метод выброшен — там бросает
        /// литеральный текст выше.
        /// </summary>
        [BurstDiscard]
        static void ThrowCopied()
            => throw new InvalidOperationException(
                $"Blobcheg: массив элементов '{typeof(T).FullName}' читается из копии записи — " +
                "запись скопирована из блоба по значению, а само-относительный оффсет живёт только " +
                "по исходному адресу. Держите запись как ref readonly, не копируйте её в локальную " +
                "переменную");
    }
}
