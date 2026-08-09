using System;
using System.Collections.Generic;
using System.Reflection;

namespace Blobcheg.Authoring
{
    /// <summary>
    /// Whether a struct is fit to lie in a file. The <c>where T : unmanaged</c> constraint only answers
    /// for "there are no managed references" — it lets a pointer through, because a pointer is formally
    /// unmanaged. And in a file that outlives a restart of the process the address of someone else's
    /// memory means nothing: on a read it yields loaded garbage, indistinguishable from a valid value.
    ///
    /// The check is an editor one and happens once per type: the price is one dictionary lookup per
    /// record.
    /// </summary>
    static class BlobchegRecordTypes
    {
        struct Verdict
        {
            /// <summary>The path to the unfit field, or <c>null</c> if there are no pointers.</summary>
            public string PointerField;

            /// <summary>The type carries a <see cref="BlobchegArray{T}"/> at some depth.</summary>
            public bool RequiresBuilder;
        }

        /// <summary>The reflection walk happens once per type — the verdicts are cached as a pair.</summary>
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

        /// <summary>
        /// A type with an array is only assembled by a builder: the size of the record is known only
        /// after all the Allocate calls, and a struct literal would quietly produce arrays of zero
        /// length.
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

                    // The array itself is two ints, there is no pointer in it. But its element does not
                    // occur among the fields at all, so the walk is obliged to enter the type argument
                    // separately: both for a pointer inside the element and for a nested array.
                    if (kind.IsGenericType && kind.GetGenericTypeDefinition() == typeof(BlobchegArray<>))
                    {
                        requiresBuilder = true;

                        var inElement = Inspect(kind.GenericTypeArguments[0], at + "[]", visiting,
                            ref requiresBuilder);
                        if (inElement != null)
                            return inElement;

                        continue;
                    }

                    // Primitives and enums have reached the bottom. Everything else that is not a struct
                    // never gets here: unmanaged stands above.
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
