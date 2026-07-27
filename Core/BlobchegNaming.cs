using System;

namespace Blobcheg
{
    /// <summary>
    /// Единственное место, где имя домена превращается в имя файла. Обязательно и для писателя,
    /// и для транспорта — разъедутся, и база просто не найдётся.
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
    }
}
