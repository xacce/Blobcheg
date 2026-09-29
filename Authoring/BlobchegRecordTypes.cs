using System;
using System.Collections.Generic;
using System.Reflection;

namespace Blobcheg.Authoring
{
    // `unmanaged` still admits pointers, and a pointer in a file reads back as plausible garbage.
    static class BlobchegRecordTypes
    {
        struct Verdict
        {
            public string PointerField; // null when there are no pointers
            public bool RequiresBuilder;
        }

        static readonly Dictionary<Type, Verdict> Verdicts = new Dictionary<Type, Verdict>();

        public static void Require(Type recordType)
        {
            var bad = Of(recordType).PointerField;
            if (bad != null)
                throw new InvalidOperationException(
                    $"Blobcheg: record '{recordType.FullName}' carries a pointer in field '{bad}'. " +
                    "A memory address in a file means nothing: it outlives the write but not a restart of " +
                    "the process, and on a read it hands out garbage indistinguishable from a value");
        }

        public static bool RequiresBuilder(Type recordType) => Of(recordType).RequiresBuilder; // literal = empty arrays

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

                    // The element type never appears among the fields, so walk the type argument itself.
                    if (kind.IsGenericType && kind.GetGenericTypeDefinition() == typeof(BlobchegArray<>))
                    {
                        requiresBuilder = true;

                        var inElement = Inspect(kind.GenericTypeArguments[0], at + "[]", visiting,
                            ref requiresBuilder);
                        if (inElement != null)
                            return inElement;

                        continue;
                    }

                    if (kind.IsPrimitive || kind.IsEnum || !kind.IsValueType) // non-structs excluded by unmanaged
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
