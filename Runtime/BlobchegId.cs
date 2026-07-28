using System;

namespace Blobcheg
{
    /// <summary>
    /// Имя ноды, общее для всех баз роутера. Один <c>uint</c> из двух частей: старший байт — тег
    /// роутера, младшие три — позиция строки ноды в его файле.
    ///
    /// Позиция, а не хеш: адрес строки — <c>array[index]</c>, без таблиц, коллизий и пробирования.
    /// Отсюда свойства — правка значений id не двигает, двигают только появление и удаление ноды.
    ///
    /// Тег нужен затем, что голое число родства не помнит: id, выданный ДРУГИМ роутером, попадал бы
    /// в диапазон этого и отдавал чужую строку молча. Тег заодно закрывает и вторую дыру: тег ноль
    /// зарезервирован, поэтому <c>default(BlobchegId)</c> — это «не назначен», а не первая нода базы.
    ///
    /// Цена — потолок в 16 777 216 нод на роутер. Столько нод не бывает; столько ассетов не
    /// открывается.
    /// </summary>
    [Serializable]
    public readonly struct BlobchegId : IEquatable<BlobchegId>
    {
        /// <summary>Сколько младших бит занимает позиция строки.</summary>
        public const int IndexBits = 24;

        public const uint IndexMask = (1u << IndexBits) - 1;

        /// <summary>Строк в одном роутере не больше этого.</summary>
        public const uint MaxIndex = IndexMask;

        /// <summary>«Id не назначен». Это же значение у любого нулём инициализированного поля.</summary>
        public const uint NoneValue = 0;

        public readonly uint Value;

        public BlobchegId(uint value) => Value = value;

        public static BlobchegId None => default;

        /// <summary>Тег роутера, выдавшего id. Ноль — id не выдавали.</summary>
        public byte Tag => (byte)(Value >> IndexBits);

        /// <summary>Позиция строки в файле роутера.</summary>
        public uint Index => Value & IndexMask;

        public bool IsValid => (Value >> IndexBits) != 0;

        /// <summary>Собрать id из тега и позиции. Тег ноль и позиция за потолком — ошибка.</summary>
        public static BlobchegId Make(byte tag, uint index)
        {
            if (tag == 0)
                throw new ArgumentOutOfRangeException(nameof(tag),
                    "Blobcheg: тег роутера ноль зарезервирован под «id не назначен»");

            if (index > MaxIndex)
                throw new ArgumentOutOfRangeException(nameof(index),
                    $"Blobcheg: строка {index} за потолком роутера {MaxIndex}");

            return new BlobchegId(((uint)tag << IndexBits) | index);
        }

        /// <summary>
        /// Id строки по имени роутера — путь инструментов и тестов. Потребитель id не собирает: он
        /// берёт его с носителя или из сейва.
        /// </summary>
        public static BlobchegId In(string routerName, uint index)
            => Make(BlobchegNaming.TagOf(routerName), index);

        public bool Equals(BlobchegId other) => Value == other.Value;

        public override bool Equals(object obj) => obj is BlobchegId other && Equals(other);

        public override int GetHashCode() => (int)Value;

        public override string ToString() => IsValid ? Tag + ":" + Index : "none";

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
