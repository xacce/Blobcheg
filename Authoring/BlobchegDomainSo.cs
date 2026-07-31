using UnityEngine;

namespace Blobcheg.Authoring
{
    /// <summary>
    /// Манифест домена: то же, что в header'е последнего собранного файла, но со стороны едитора.
    /// Источником истины про состав не является — состав ищется по проекту; манифест нужен, чтобы
    /// сверить «запечено ровно то, что лежит в ассетах», и чтобы это было видно глазами.
    /// </summary>
    public sealed class BlobchegDomainSo : ScriptableObject
    {
        /// <summary>
        /// Какой файл описан. У роутера и у таблицы хешей <c>nodes</c> идут в порядке id, у базы —
        /// в порядке обхода проекта.
        /// </summary>
        public BlobchegFileKind kind;

        /// <summary>Манифест роутера, а не базы. Осталось для глаз и для тестов.</summary>
        public bool IsRouter => kind == BlobchegFileKind.Router;

        public string domainName;
        public string fileName;
        public string builtAt;
        public int recordCount;
        public BlobchegNodeSo[] nodes;

        [SerializeField] long contentHash;

        public ulong ContentHash
        {
            get => unchecked((ulong)contentHash);
            set => contentHash = unchecked((long)value);
        }
    }
}
