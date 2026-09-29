using System;
using System.Collections.Generic;
using System.Reflection;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;

namespace Blobcheg
{
    // Kept apart from BlobchegPatchTable: Burst reads that table, and a managed static there breaks it.
    public static unsafe class BlobchegPatchTableBuilder
    {
        static readonly List<ComponentType> s_Registered = new List<ComponentType>();
        static readonly List<string> s_Diagnostics = new List<string>();

        public static IReadOnlyList<ComponentType> RegisteredTypes => s_Registered;

        public static IReadOnlyList<string> Diagnostics => s_Diagnostics; // one bad type must not disable the patch

        public static void Build()
        {
            if (BlobchegPatchTable.IsBuilt)
                return; // once per process: layouts and the type set are fixed after TypeManager init

            var domains = CollectDomains();

            var map = (UnsafeHashMap<int, BlobchegSlotRange>*)UnsafeUtility.Malloc(
                sizeof(UnsafeHashMap<int, BlobchegSlotRange>), 8, Allocator.Persistent);
            *map = new UnsafeHashMap<int, BlobchegSlotRange>(64, Allocator.Persistent);

            var slots = (UnsafeList<BlobchegFieldSlot>*)UnsafeUtility.Malloc(
                sizeof(UnsafeList<BlobchegFieldSlot>), 8, Allocator.Persistent);
            *slots = new UnsafeList<BlobchegFieldSlot>(128, Allocator.Persistent);

            s_Registered.Clear();
            s_Diagnostics.Clear();

            var found = new List<BlobchegFieldSlot>();
            var seen = new HashSet<Type>();

            foreach (var type in ComponentCandidates())
            {
                found.Clear();
                seen.Clear();

                try
                {
                    Walk(type, 0, found, seen, domains, 0);
                }
                catch (Exception e)
                {
                    s_Diagnostics.Add(e.Message);
                    continue;
                }

                if (found.Count == 0)
                    continue;

                // Shared components live outside chunks, so no patch pass reaches them: report, never skip.
                if (typeof(ISharedComponentData).IsAssignableFrom(type))
                {
                    s_Diagnostics.Add(
                        $"Blobcheg: shared component '{type.FullName}' declares a BlobchegReference slot — " +
                        "the patch does not walk shared components, and the slot will stay an offset. " +
                        "Move the reference into an ordinary component or read it through \"offset plus Read\"");
                    continue;
                }

                var typeIndex = TypeManager.GetTypeIndex(type);
                var range = new BlobchegSlotRange { Start = slots->Length, Count = found.Count };

                foreach (var slot in found)
                    slots->Add(slot);

                map->Add(typeIndex.Value, range);
                s_Registered.Add(ComponentType.FromTypeIndex(typeIndex));
            }

            BlobchegPatchTable.Storage = new BlobchegPatchTable.Data
            {
                Map = (IntPtr)map,
                Slots = (IntPtr)slots,
            };
        }

        public static void Destroy() // editor domain reload: persistent memory outlives the managed side
        {
            var data = BlobchegPatchTable.Storage;
            if (data.Map == IntPtr.Zero)
            {
                s_Registered.Clear();
                return;
            }

            var map = (UnsafeHashMap<int, BlobchegSlotRange>*)data.Map;
            map->Dispose();
            UnsafeUtility.Free(map, Allocator.Persistent);

            var slots = (UnsafeList<BlobchegFieldSlot>*)data.Slots;
            slots->Dispose();
            UnsafeUtility.Free(slots, Allocator.Persistent);

            BlobchegPatchTable.Storage = default;
            s_Registered.Clear();
        }

        static Dictionary<Type, ulong> CollectDomains()
        {
            var domains = new Dictionary<Type, ulong>();

            foreach (var assembly in RelevantAssemblies())
            {
                foreach (var type in SafeGetTypes(assembly))
                {
                    if (!type.IsValueType)
                        continue;

                    var attr = type.GetCustomAttribute<BlobchegAttribute>(false);
                    if (attr == null)
                        continue;

                    var key = BlobchegNaming.NameHash(attr.Domain.Name);
                    domains[attr.Domain] = key;

                    BlobchegDomainNames.Remember(key, attr.Domain.Name); // "domain not loaded" must name it
                }
            }

            return domains;
        }

        static IEnumerable<Type> ComponentCandidates()
        {
            foreach (var assembly in RelevantAssemblies())
            {
                foreach (var type in SafeGetTypes(assembly))
                {
                    if (!type.IsValueType || type.IsPrimitive || type.IsEnum || type.ContainsGenericParameters)
                        continue;

                    if (typeof(IComponentData).IsAssignableFrom(type) ||
                        typeof(IBufferElementData).IsAssignableFrom(type) ||
                        typeof(ISharedComponentData).IsAssignableFrom(type)) // shared: only to report their slots
                        yield return type;
                }
            }
        }

        // Only assemblies referencing Blobcheg.Runtime: a full app-domain walk at startup is costly.
        static IEnumerable<Assembly> RelevantAssemblies()
        {
            var self = typeof(BlobchegReferenceData).Assembly;
            var selfName = self.GetName().Name;

            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (assembly == self)
                {
                    yield return assembly;
                    continue;
                }

                if (assembly.IsDynamic)
                    continue;

                var referenced = false;
                foreach (var reference in assembly.GetReferencedAssemblies())
                {
                    if (!string.Equals(reference.Name, selfName, StringComparison.Ordinal))
                        continue;

                    referenced = true;
                    break;
                }

                if (referenced)
                    yield return assembly;
            }
        }

        static IEnumerable<Type> SafeGetTypes(Assembly assembly)
        {
            try
            {
                return assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException e)
            {
                var loaded = new List<Type>();
                foreach (var type in e.Types)
                    if (type != null)
                        loaded.Add(type);

                return loaded;
            }
        }

        // Modelled on EntityRemapUtility.CalculateOffsetsRecurse: matches by field type, not by name.
        static void Walk(Type type, int baseOffset, List<BlobchegFieldSlot> found, HashSet<Type> seen,
            Dictionary<Type, ulong> domains, int depth)
        {
            if (depth > 32)
                throw new InvalidOperationException(
                    $"Blobcheg: struct '{type.FullName}' is nested deeper than 32 — the field walk refused");

            foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                var fieldType = field.FieldType;
                if (!fieldType.IsValueType || fieldType.IsPrimitive || fieldType.IsEnum)
                    continue;

                var offset = baseOffset + UnsafeUtility.GetFieldOffset(field);

                if (fieldType.IsGenericType && fieldType.GetGenericTypeDefinition() == typeof(BlobchegReference<>))
                {
                    var record = fieldType.GetGenericArguments()[0];
                    found.Add(new BlobchegFieldSlot
                    {
                        Offset = offset,
                        DomainKey = DomainKeyOf(record, domains),
                        RecordTypeHash = RecordTypeHashOf(record),
                    });
                    continue;
                }

                if (fieldType == typeof(BlobchegReferenceData))
                    throw new InvalidOperationException(
                        $"Blobcheg: in '{type.FullName}' the field '{field.Name}' is declared as " +
                        "BlobchegReferenceData directly. That is the innards of a slot, not a field: no " +
                        "domain can be derived from it. Declare a BlobchegReference<T>");

                if (fieldType == typeof(BlobchegRawReference))
                    throw new InvalidOperationException(
                        $"Blobcheg: in '{type.FullName}' the field '{field.Name}' is a BlobchegRawReference. " +
                        "A raw record has no type, therefore no domain, and there is nothing to patch it " +
                        "with. Leave such records on the \"offset plus Read\" path");

                if (!seen.Add(fieldType)) // guards one path only; the same struct may repeat in siblings
                    continue;

                Walk(fieldType, offset, found, seen, domains, depth + 1);
                seen.Remove(fieldType);
            }
        }

        // Same hash the base writer stores (BurstRuntime.GetHashCode32<T>), via reflection on a Type.
        static uint RecordTypeHashOf(Type record)
        {
            var method = GetHashCode32Definition().MakeGenericMethod(record);
            return unchecked((uint)(int)method.Invoke(null, null));
        }

        static MethodInfo GetHashCode32Definition()
        {
            foreach (var method in typeof(Unity.Burst.BurstRuntime).GetMethods(BindingFlags.Public | BindingFlags.Static))
                if (method.Name == nameof(Unity.Burst.BurstRuntime.GetHashCode32) &&
                    method.IsGenericMethodDefinition &&
                    method.GetParameters().Length == 0)
                    return method;

            throw new InvalidOperationException(
                "Blobcheg: this version of Burst has no BurstRuntime.GetHashCode32<T>() — there is nothing to check the record type with");
        }

        static ulong DomainKeyOf(Type record, Dictionary<Type, ulong> domains)
        {
            ulong key = 0;
            Type marker = null;

            foreach (var iface in record.GetInterfaces())
            {
                if (!domains.TryGetValue(iface, out var candidate))
                    continue;

                if (marker != null)
                    throw new InvalidOperationException(
                        $"Blobcheg: record '{record.FullName}' belongs to domains '{marker.Name}' and " +
                        $"'{iface.Name}' at once. The patch cannot choose which base to take the address from");

                marker = iface;
                key = candidate;
            }

            if (marker == null)
                throw new InvalidOperationException(
                    $"Blobcheg: record '{record.FullName}' belongs to no domain — there is no marker interface " +
                    "declared through [Blobcheg]. There is nowhere to patch it from");

            return key;
        }
    }
}
