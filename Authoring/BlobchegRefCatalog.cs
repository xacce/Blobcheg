using System;
using System.Collections.Generic;
using System.Linq;

namespace Blobcheg.Authoring
{
    /// <summary>
    /// Кандидаты для поля-ссылки: все ref-ассеты проекта, у которых запись нужного типа.
    ///
    /// Собирается обходом нод, а не поиском по лейблам или по индексу: нативный пикер фильтровать
    /// по типу записи не умеет вовсе, а поисковый индекс отстаёт от импорта. Пикер, показавший
    /// не тот список, — это молчаливая ошибка, которую заметят на бейке или в рантайме.
    /// </summary>
    public static class BlobchegRefCatalog
    {
        /// <param name="recordType">Тип записи; <c>null</c> — сырой ref, годится всё.</param>
        public static List<BlobchegRefSo> Candidates(Type recordType)
        {
            var wanted = recordType?.FullName;

            return BlobchegCache.Fill()
                .SelectMany(BlobchegCache.RefsOf)
                .Where(reference => wanted == null
                                    || string.Equals(reference.RecordType, wanted, StringComparison.Ordinal))
                .OrderBy(reference => reference.name, StringComparer.Ordinal)
                .ToList();
        }

        public static bool Matches(BlobchegRefSo reference, Type recordType)
            => reference != null
               && (recordType == null
                   || string.Equals(reference.RecordType, recordType.FullName, StringComparison.Ordinal));
    }
}
