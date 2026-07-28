using System;
using System.Text;

namespace Blobcheg
{
    /// <summary>
    /// Единственное место, где имя домена превращается в имя файла и в личность этого файла.
    /// Обязательно и для писателя, и для транспорта — разъедутся, и база просто не найдётся.
    /// </summary>
    public static class BlobchegNaming
    {
        public const string Extension = ".bcheg";

        /// <summary>Дефолтная папка внутри StreamingAssets проекта.</summary>
        public const string DefaultFolder = "Blobcheg";

        public static string FileName(string domainName)
        {
            if (string.IsNullOrEmpty(domainName))
                throw new ArgumentException("Blobcheg: пустое имя домена", nameof(domainName));

            return domainName + Extension;
        }

        /// <summary>
        /// Личность файла: fnv1a-64 по имени домена или роутера. Едет в header и сверяется на
        /// подъёме — иначе два подменённых местами .bcheg поднимаются оба и молча отдают чужие байты.
        ///
        /// Считается по имени, а не по содержимому: содержимое меняется каждой пересборкой, а
        /// личность обязана пережить её.
        /// </summary>
        public static ulong NameHash(string name)
        {
            const ulong offsetBasis = 14695981039346656037;
            const ulong prime = 1099511628211;

            var hash = offsetBasis;
            foreach (var b in Encoding.UTF8.GetBytes(name ?? string.Empty))
            {
                hash ^= b;
                hash *= prime;
            }

            return hash;
        }

        /// <summary>
        /// Тег роутера — старший байт <see cref="BlobchegId"/>. Ноль зарезервирован под «id не
        /// назначен», поэтому тег живёт в 1..255; уникальность тегов по проекту доказывает
        /// едиторный реестр роутеров, а не надежда на хеш.
        /// </summary>
        public static byte TagOf(string routerName)
        {
            if (string.IsNullOrEmpty(routerName))
                throw new ArgumentException("Blobcheg: пустое имя роутера", nameof(routerName));

            return (byte)(NameHash(routerName) % 255 + 1);
        }
    }
}
