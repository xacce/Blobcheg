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
        struct Verdict
        {
            /// <summary>Путь до негодного поля или <c>null</c>, если указателей нет.</summary>
            public string PointerField;

            /// <summary>Тип несёт <see cref="BlobchegArray{T}"/> на какой-то глубине.</summary>
            public bool RequiresBuilder;
        }

        /// <summary>Обход рефлексией разовый на тип — вердикты кешируются парой.</summary>
        static readonly Dictionary<Type, Verdict> Verdicts = new Dictionary<Type, Verdict>();

        public static void Require(Type recordType)
        {
            var bad = Of(recordType).PointerField;
            if (bad != null)
                throw new InvalidOperationException(
                    $"Blobcheg: запись '{recordType.FullName}' несёт указатель в поле '{bad}'. " +
                    "Адрес памяти в файле не значит ничего: он переживает запись, но не перезапуск " +
                    "процесса, и при чтении отдаёт мусор, неотличимый от значения");
        }

        /// <summary>
        /// Тип с массивом собирается только билдером: размер записи известен лишь после всех
        /// Allocate, а структ-литерал молча дал бы массивы нулевой длины.
        /// </summary>
        public static bool RequiresBuilder(Type recordType) => Of(recordType).RequiresBuilder;

        static Verdict Of(Type recordType)
        {
            if (Verdicts.TryGetValue(recordType, out var verdict))
                return verdict;

            verdict = default;
            verdict.PointerField = Inspect(recordType, recordType.Name, new HashSet<Type>(),
                ref verdict.RequiresBuilder);
            Verdicts.Add(recordType, verdict);
            return verdict;
        }

        static string Inspect(Type type, string path, HashSet<Type> visiting, ref bool requiresBuilder)
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

                    // Сам массив — два int'а, указателя в нём нет. Но его элемент среди полей не
                    // встречается вовсе, поэтому обход обязан войти в тип-аргумент отдельно: и за
                    // указателем внутри элемента, и за вложенным массивом.
                    if (kind.IsGenericType && kind.GetGenericTypeDefinition() == typeof(BlobchegArray<>))
                    {
                        requiresBuilder = true;

                        var inElement = Inspect(kind.GenericTypeArguments[0], at + "[]", visiting,
                            ref requiresBuilder);
                        if (inElement != null)
                            return inElement;

                        continue;
                    }

                    // Примитивы и enum'ы дна достигли. Всё остальное, что не структура, до сюда не
                    // доходит: наверху стоит unmanaged.
                    if (kind.IsPrimitive || kind.IsEnum || !kind.IsValueType)
                        continue;

                    var deeper = Inspect(kind, at, visiting, ref requiresBuilder);
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
