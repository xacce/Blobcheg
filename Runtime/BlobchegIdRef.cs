using System;
using UnityEngine;

namespace Blobcheg
{
    /// <summary>
    /// Носитель <see cref="BlobchegId"/> из едитора в билд: sub-asset на пару (нода × роутер).
    /// Отдельный ассет нужен по той же причине, что и <see cref="BlobchegRefSo"/>: нода живёт в
    /// editor-only сборке, и рантайм-authoring на неё сослаться не может.
    /// </summary>
    public sealed class BlobchegIdSo : ScriptableObject
    {
        /// <summary>
        /// Значение <see cref="BlobchegId"/>: тег роутера и позиция строки. Перевыставляется каждой
        /// пересборкой. Ноль — «не назначен», он же значение свежесозданного носителя.
        /// </summary>
        public uint id = BlobchegId.NoneValue;

        [SerializeField] internal string routerName;

        public string RouterName => routerName;
    }

    /// <summary>
    /// Поле у потребителя: <c>public BlobchegIdRef&lt;GameRouter&gt; gun;</c>. Чужой роутер не
    /// присвоится компилятором, чужой ассет отобьёт драйвер, а пустое поле бросит вместо нуля.
    /// </summary>
    [Serializable]
    public struct BlobchegIdRef<TRouter> where TRouter : unmanaged, IBlobchegRouter
    {
        [SerializeField] internal BlobchegIdSo asset;

        public BlobchegIdRef(BlobchegIdSo asset) => this.asset = asset;

        /// <summary>Сам ассет — для <c>DependsOn</c> в бейкере.</summary>
        public BlobchegIdSo Asset => asset;

        public bool IsSet => asset != null;

        /// <summary>Имя роутера этого поля. Берётся у параметра типа, а не пишется руками.</summary>
        public static string RouterName => default(TRouter).Name;

        /// <summary>Id ноды. Пустое поле, ассет чужого роутера или неназначенный id — исключение.</summary>
        public BlobchegId Id
        {
            get
            {
                if (asset == null)
                    throw new InvalidOperationException(
                        $"Blobcheg: пустой BlobchegIdRef<{typeof(TRouter).Name}> — нода не назначена");

                var expected = RouterName;
                if (!string.Equals(asset.routerName, expected, StringComparison.Ordinal))
                    throw new InvalidOperationException(
                        $"Blobcheg: в BlobchegIdRef<{typeof(TRouter).Name}> лежит ассет '{asset.name}' роутера " +
                        $"'{asset.routerName}' — ожидался '{expected}'");

                var id = new BlobchegId(asset.id);
                if (!id.IsValid)
                    throw new InvalidOperationException(
                        $"Blobcheg: ассет '{asset.name}' без id — пересборка до него не дошла");

                return id;
            }
        }
    }
}
