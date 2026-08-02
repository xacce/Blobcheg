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
        /// <summary>Домены, в которые нода обещает писать. Расхождение с фактом — ошибка сборки.</summary>
        public abstract Type[] OutTypes { get; }

        public abstract void Write(ref BlobchegNodeWriter writer);
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
