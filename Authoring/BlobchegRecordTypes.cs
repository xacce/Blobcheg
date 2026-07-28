using System;
using System.Collections.Generic;
using System.Reflection;

namespace Blobcheg.Authoring
{
    /// <summary>
    /// Пригодна ли структура к тому, чтобы лечь в файл. Констрейнт <c>where T : unmanaged</c>
    /// отвечает только за «нет managed-ссылок» — указатель он пропускает, потому что указатель
    /// формально unmanaged. А в файле, который переживёт перезапуск процесса, адрес чужой памяти не
    /// значит ничего: при чтении он даёт заряженный мусор, неотличимый от валидного значения.
    ///
    /// Проверка едиторная и разовая на тип: цена — один словарный лукап на запись.
    /// </summary>
    static class BlobchegRecordTypes
    {
        /// <summary>Путь до негодного поля или <c>null</c>, если тип пригоден.</summary>
        static readonly Dictionary<Type, string> Verdicts = new Dictionary<Type, string>();

        public static void Require(Type recordType)
        {
            if (!Verdicts.TryGetValue(recordType, out var bad))
            {
                bad = FindPointer(recordType, recordType.Name, new HashSet<Type>());
                Verdicts.Add(recordType, bad);
            }

            if (bad != null)
                throw new InvalidOperationException(
                    $"Blobcheg: запись '{recordType.FullName}' несёт указатель в поле '{bad}'. " +
                    "Адрес памяти в файле не значит ничего: он переживает запись, но не перезапуск " +
                    "процесса, и при чтении отдаёт мусор, неотличимый от значения");
        }

        static string FindPointer(Type type, string path, HashSet<Type> visiting)
        {
            if (!visiting.Add(type))
                return null;

            try
            {
                foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    var at = path + "." + field.Name;
                    var kind = field.FieldType;

                    if (kind.IsPointer || kind == typeof(IntPtr) || kind == typeof(UIntPtr))
                        return at;

                    // Примитивы и enum'ы дна достигли. Всё остальное, что не структура, до сюда не
                    // доходит: наверху стоит unmanaged.
                    if (kind.IsPrimitive || kind.IsEnum || !kind.IsValueType)
                        continue;

                    var deeper = FindPointer(kind, at, visiting);
                    if (deeper != null)
                        return deeper;
                }

                return null;
            }
            finally
            {
                visiting.Remove(type);
            }
        }
    }
}
