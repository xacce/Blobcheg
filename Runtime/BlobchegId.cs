using System;

namespace Blobcheg
{
    /// <summary>
    /// Имя ноды, общее для всех баз роутера: позиция её строки в файле роутера. Потребитель хранит
    /// один <c>uint</c> вместо пачки оффсетов и спрашивает у роутера то, что нужно.
    ///
    /// Это позиция, а не хеш: адрес строки — <c>array[id]</c>, без таблиц, коллизий и пробирования.
    /// Отсюда и свойства — правка значений id не двигает, двигают только появление и удаление ноды.
    /// </summary>
    [Serializable]
    public readonly struct BlobchegId : IEquatable<BlobchegId>
    {
        /// <summary>«Id не назначен». Валидным не бывает: столько нод не бывает.</summary>
        public const uint NoneValue = uint.MaxValue;

        public readonly uint Value;

        public BlobchegId(uint value) => Value = value;

        public static BlobchegId None => new BlobchegId(NoneValue);

        public bool IsValid => Value != NoneValue;

        public bool Equals(BlobchegId other) => Value == other.Value;

        public override bool Equals(object obj) => obj is BlobchegId other && Equals(other);

        public override int GetHashCode() => (int)Value;

        public override string ToString() => IsValid ? Value.ToString() : "none";

        public static bool operator ==(BlobchegId a, BlobchegId b) => a.Value == b.Value;

        public static bool operator !=(BlobchegId a, BlobchegId b) => a.Value != b.Value;
    }

    /// <summary>
    /// Реализуется кодогеном на каждой структуре роутера. Нужен ровно затем, чтобы поле
    /// <see cref="BlobchegIdRef{TRouter}"/> умело спросить у своего параметра имя роутера и отбить
    /// ассет чужого.
    /// </summary>
    public interface IBlobchegRouter
    {
        /// <summary>Имя роутера, оно же имя его файла.</summary>
        string Name { get; }
    }
}
