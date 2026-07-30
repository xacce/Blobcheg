#if UNITY_EDITOR
using System.Collections.Generic;

namespace Blobcheg
{
    /// <summary>
    /// Счётчик пересборок файла. Пересборка поднимает номер каждому файлу, который переписала, а
    /// тот, кто этот файл поднял в мир, по номеру видит, что его база протухла, и перечитывает.
    ///
    /// Ключ — имя файла, а не имя домена: у базы и у роутера общего имени нет, а файл есть у обоих,
    /// и переписывается ровно он.
    ///
    /// Только редактор: в плеере файлы не пересобираются, сторожить нечего. Номера не переживают
    /// перезагрузку домена — и не должны: вместе с ними умирают и миры, которые их запомнили.
    /// </summary>
    public static class BlobchegFileVersions
    {
        static readonly Dictionary<string, int> Versions = new Dictionary<string, int>();

        /// <summary>Файл переписан. Зовёт пересборка — по разу на каждый изменившийся файл.</summary>
        public static void Bump(string fileName)
        {
            Versions.TryGetValue(fileName, out var version);
            Versions[fileName] = version + 1;
        }

        /// <summary>Текущий номер файла. Файла никто не переписывал — ноль.</summary>
        public static int Of(string fileName)
        {
            Versions.TryGetValue(fileName, out var version);
            return version;
        }

        /// <summary>
        /// Переписан ли файл с тех пор, как спрашивающий его читал. <paramref name="seen"/> — его
        /// собственная отметка, здесь же и обновляется, поэтому вопрос задаётся одной строкой и
        /// второй раз подряд отвечает «нет».
        /// </summary>
        public static bool Changed(string fileName, ref int seen)
        {
            var version = Of(fileName);
            if (version == seen)
                return false;

            seen = version;
            return true;
        }
    }
}
#endif
