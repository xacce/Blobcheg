using System;
using UnityEngine;

namespace Blobcheg.Authoring
{
    /// <summary>
    /// Единица данных в едиторе. Сама решает, в какие базы писать; в каждую — ровно одну запись.
    /// Кнопки Save у неё нет: сохранил ассет — пайплайн пересобрал домен сам.
    /// </summary>
    public abstract class BlobchegNodeSo : ScriptableObject
    {
        [Tooltip("Стабильное имя ноды. Переживает переименование ассета, компакт и удаление соседей — "
                 + "по нему считается хеш, которым сейв адресует запись. Пустое заполняется один раз "
                 + "именем ассета; дальше это имя менять нельзя, чужие сейвы его уже запомнили.")]
        [SerializeField] string blobchegName;

        /// <summary>
        /// Стабильное имя ноды. Всё остальное про неё — GUID, имя файла, оффсеты, id — либо не
        /// видно потребителю, либо переживает не всё: адреса и id уезжают на компакте, а имя файла
        /// человек меняет мышкой.
        /// </summary>
        public string BlobchegName => blobchegName;

        /// <summary>Домены, в которые нода обещает писать. Расхождение с фактом — ошибка сборки.</summary>
        public abstract Type[] OutTypes { get; }

        public abstract void Write(ref BlobchegNodeWriter writer);

        /// <summary>
        /// Пустое имя заполняется именем ассета. Зовёт только пересборка и только до
        /// <see cref="Write"/>: запись может положить в себя хеш собственного имени.
        /// Возвращает, тронуто ли поле.
        /// </summary>
        internal bool EnsureName()
        {
            if (!string.IsNullOrEmpty(blobchegName))
                return false;

            if (string.IsNullOrEmpty(name))
                return false;

            blobchegName = name;
            return true;
        }
    }

    /// <summary>
    /// Нода роутера с <c>FixedIndex</c>: номер своей строки она объявляет сама. Откуда берёт — её
    /// дело: сериализованное поле, const, enum, строка таблицы. Пакет только спрашивает.
    ///
    /// Интерфейс, а не член базового класса: реализуют его только ноды детерминированных роутеров,
    /// и «не реализовала» — это проверка типа, а не сентинел вроде -1.
    /// </summary>
    public interface IBlobchegIndexed
    {
        /// <summary>Строка ноды в файле роутера, 0..<see cref="BlobchegId.MaxIndex"/>.</summary>
        uint Index { get; }
    }
}
