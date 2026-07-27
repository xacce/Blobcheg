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
}
