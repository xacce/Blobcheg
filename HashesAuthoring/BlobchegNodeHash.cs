using System;

namespace Blobcheg.Authoring
{
    /// <summary>
    /// Хеш ноды на бейке. Живёт отдельно от <see cref="BlobchegNodeWriter"/> намеренно: основной
    /// писатель о хешах не знает, а нода, которой хеш нужен в записи, ссылается на эту сборку сама.
    /// </summary>
    public static class BlobchegNodeHash
    {
        /// <summary>
        /// Хеш имени ноды в этом роутере — его можно положить прямо в запись, как <c>writer.Id</c>.
        /// Имя известно до <see cref="BlobchegNodeSo.Write"/>: пересборка проставляет его раньше.
        /// </summary>
        public static ulong HashIn<TRouter>(this BlobchegNodeSo node)
            where TRouter : unmanaged, IBlobchegRouter
        {
            if (node == null)
                throw new ArgumentNullException(nameof(node), "Blobcheg: хеш несуществующей ноды");

            var routerName = default(TRouter).Name;

            var name = node.BlobchegName;
            if (string.IsNullOrEmpty(name))
                throw new InvalidOperationException(
                    $"Blobcheg: у ноды '{node.name}' пустое имя — хеш считать не от чего. " +
                    "Пересборка проставляет его сама, значит этот вызов идёт мимо неё");

            // Нода вне роутера получила бы хеш, которому в таблице нечего отдать: строки у неё там
            // нет. Молчать об этом нельзя — ошибка вылезла бы только в рантайме, на загрузке сейва.
            if (!BlobchegRouters.RoutersOf(node).Contains(typeof(TRouter)))
                throw new InvalidOperationException(
                    $"Blobcheg: нода '{node.name}' не пишет ни в одну базу роутера '{routerName}' — " +
                    "строки в его таблице у неё нет, и хеш вёл бы в никуда");

            return BlobchegHashKey.Of(routerName, name);
        }
    }
}
