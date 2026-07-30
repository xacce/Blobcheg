using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Unity.Collections.LowLevel.Unsafe;

namespace Blobcheg
{
    /// <summary>
    /// Слот ссылки на запись внутри компонента. Восемь байт, и в них по очереди живут две разные
    /// вещи: до патча — оффсет записи в файле, после патча — её адрес в поднятом буфере. Ровно так
    /// же устроен <c>BlobAssetReferenceData</c> у Unity, и по той же причине: сериализуемая форма
    /// обязана пережить процесс, а читать хочется без сложения.
    ///
    /// Тип отдельный и нетипизированный, потому что по нему обход полей узнаёт наши слоты в чужой
    /// структуре — сравнением типа поля, а не имени. Ноль означает «не назначено» бесплатно: оффсеты
    /// начинаются с <see cref="BlobchegFormat.HeaderSize"/>, а нулевого адреса не бывает.
    /// </summary>
    public struct BlobchegReferenceData
    {
        public ulong Value;
    }

    /// <summary>
    /// Ссылка на запись, живущая в компоненте сущности. Кладётся бейкером оффсетом, превращается в
    /// адрес патчем на импорте сцены; читается без базы и без сложения.
    ///
    /// Это не замена <see cref="BlobchegRef{T}"/>: тот — редакторное поле-носитель адреса, этот —
    /// рантайм-слот в компоненте. Обычный путь «оффсет плюс <c>Read</c>» никуда не девается.
    /// </summary>
    public unsafe struct BlobchegReference<T> : IEquatable<BlobchegReference<T>> where T : unmanaged
    {
        public BlobchegReferenceData Data;

        /// <summary>Из адреса записи в редакторе: <c>new BlobchegReference&lt;GunData&gt;(a.gun.Offset)</c>.</summary>
        public BlobchegReference(uint offset) => Data = new BlobchegReferenceData { Value = offset };

        public bool IsSet => Data.Value != 0;

        /// <summary>
        /// Пропатчено ли поле. Не «валидно»: непропатченное поле — это нормальное состояние
        /// сущности, которая ещё не доехала до патча.
        /// </summary>
        public bool IsResolved => Data.Value != 0 && BlobchegBases.IsKnownAddress(Data.Value);

        /// <summary>
        /// Сама запись. В релизе — чистая реинтерпретация по адресу; в редакторе и development-билде
        /// сверяется, что в слоте действительно адрес поднятой базы, а не оставшийся оффсет.
        /// </summary>
        public ref readonly T Value
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
                CheckResolved();
                return ref UnsafeUtility.AsRef<T>((void*)Data.Value);
            }
        }

        /// <summary>
        /// Две ссылки равны, если в них одно и то же. Сравнение обязано отвечать одинаково до и
        /// после патча — иначе привычное <c>if (a == b)</c> в игровом коде начинает врать ровно
        /// после загрузки сцены; оба состояния сравниваются по содержимому слота, и оба сходятся.
        /// </summary>
        public bool Equals(BlobchegReference<T> other) => Data.Value == other.Data.Value;

        public override bool Equals(object obj) => obj is BlobchegReference<T> other && Equals(other);

        public override int GetHashCode() => Data.Value.GetHashCode();

        public static bool operator ==(BlobchegReference<T> a, BlobchegReference<T> b) => a.Equals(b);

        public static bool operator !=(BlobchegReference<T> a, BlobchegReference<T> b) => !a.Equals(b);

        [Conditional("ENABLE_UNITY_COLLECTIONS_CHECKS")]
        void CheckResolved()
        {
            if (Data.Value == 0)
                throw new InvalidOperationException(
                    $"Blobcheg: пустой BlobchegReference<{typeof(T).Name}> — запись не назначена");

            if (!BlobchegBases.IsKnownAddress(Data.Value))
                throw new InvalidOperationException(
                    $"Blobcheg: BlobchegReference<{typeof(T).Name}> не пропатчен — в слоте оффсет {Data.Value}, " +
                    "а не адрес. Сущность не проходила патч импорта, либо база домена не поднята");
        }
    }

    /// <summary>
    /// То же без параметра — под записи из <c>AddBytes</c>, у которых типа нет. Отдаёт байты, потому
    /// что реинтерпретировать нечего: дыра ровно там же, где у <see cref="BlobchegRawRef"/>.
    /// </summary>
    public unsafe struct BlobchegRawReference
    {
        public BlobchegReferenceData Data;

        public BlobchegRawReference(uint offset) => Data = new BlobchegReferenceData { Value = offset };

        public bool IsSet => Data.Value != 0;

        public bool IsResolved => Data.Value != 0 && BlobchegBases.IsKnownAddress(Data.Value);

        public byte* Ptr
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
                CheckResolved();
                return (byte*)Data.Value;
            }
        }

        [Conditional("ENABLE_UNITY_COLLECTIONS_CHECKS")]
        void CheckResolved()
        {
            if (Data.Value == 0)
                throw new InvalidOperationException("Blobcheg: пустой BlobchegRawReference — запись не назначена");

            if (!BlobchegBases.IsKnownAddress(Data.Value))
                throw new InvalidOperationException(
                    $"Blobcheg: BlobchegRawReference не пропатчен — в слоте оффсет {Data.Value}, а не адрес");
        }
    }
}
