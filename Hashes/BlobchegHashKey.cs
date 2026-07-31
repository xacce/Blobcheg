using System;
using System.Text;

namespace Blobcheg
{
    /// <summary>
    /// Ключ таблицы хешей: <c>"{Роутер}:{Имя}"</c>, свёрнутый в <c>ulong</c>. Чистая функция — ни
    /// таблицы, ни пересборки, ни поднятой базы ей не нужно, поэтому её одинаково зовут и нода на
    /// бейке, и инструмент, и потребитель, у которого имя лежит строкой в конфиге.
    ///
    /// Домена в ключе нет намеренно. Хеш разворачивается в номер строки роутера, а строка — понятие
    /// роутера: она одна на ноду независимо от того, в сколько доменов та пишет. Домен в ключе дал
    /// бы одной ноде несколько хешей, ведущих в одну и ту же строку.
    ///
    /// Роутер в ключе обязателен по той же причине, по которой в <see cref="BlobchegId"/> живёт тег:
    /// без него две ноды с одинаковым именем в разных роутерах дают один хеш на две разные строки.
    ///
    /// Алгоритм — fnv1a-64, тот же, что у <see cref="BlobchegNaming.NameHash"/>: второго семейства
    /// хешей в пакете нет.
    /// </summary>
    public static class BlobchegHashKey
    {
        /// <summary>Разделитель имени роутера и имени ноды.</summary>
        public const byte Separator = (byte)':';

        const ulong OffsetBasis = 14695981039346656037;
        const ulong Prime = 1099511628211;

        public static ulong Of(string routerName, string name)
        {
            if (string.IsNullOrEmpty(routerName))
                throw new ArgumentException("Blobcheg: пустое имя роутера в ключе хеша", nameof(routerName));

            if (string.IsNullOrEmpty(name))
                throw new ArgumentException("Blobcheg: пустое имя ноды в ключе хеша", nameof(name));

            var hash = OffsetBasis;
            Feed(ref hash, Encoding.UTF8.GetBytes(routerName));

            hash ^= Separator;
            hash *= Prime;

            Feed(ref hash, Encoding.UTF8.GetBytes(name));

            // Ноль занят: им помечен пустой слот таблицы и им же инициализировано любое поле, куда
            // хеш ещё не положили. Досчитываем один шаг — произведение нечётного на нечётное нулём
            // не бывает, поэтому шаг ровно один и он детерминирован.
            if (hash == 0)
            {
                hash ^= 0xFF;
                hash *= Prime;
            }

            return hash;
        }

        /// <summary>Имя роутера берётся у параметра типа, а не пишется руками.</summary>
        public static ulong Of<TRouter>(string name) where TRouter : unmanaged, IBlobchegRouter
            => Of(default(TRouter).Name, name);

        static void Feed(ref ulong hash, byte[] bytes)
        {
            foreach (var b in bytes)
            {
                hash ^= b;
                hash *= Prime;
            }
        }
    }
}
