using System;
using System.Collections.Generic;
using System.Reflection;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;

namespace Blobcheg
{
    /// <summary>
    /// Managed-сторона таблицы слотов: обход типов, разрешение домена записи, аллокация нативных
    /// контейнеров. Отдельно от <see cref="BlobchegPatchTable"/> не по вкусу, а по необходимости —
    /// ту таблицу читает Burst-код, и managed-статик в её классе валит компиляцию целиком.
    /// </summary>
    public static unsafe class BlobchegPatchTableBuilder
    {
        static readonly List<ComponentType> s_Registered = new List<ComponentType>();
        static readonly List<string> s_Diagnostics = new List<string>();

        /// <summary>Типы компонентов, в которых есть хотя бы один слот. Их метёт живой путь.</summary>
        public static IReadOnlyList<ComponentType> RegisteredTypes => s_Registered;

        /// <summary>
        /// Что не удалось разобрать при сборке. Отдельный список, а не исключение: сборка обходит
        /// все типы процесса разом, и один неправильно объявленный компонент не имеет права выключить
        /// патч всему проекту. Проваленный тип просто не регистрируется, а его беда называется вслух.
        /// </summary>
        public static IReadOnlyList<string> Diagnostics => s_Diagnostics;

        /// <summary>
        /// Собирает таблицу обходом типов. Один раз на процесс: раскладка структур в рантайме не
        /// меняется, а новых типов компонентов после инициализации TypeManager не появляется.
        /// </summary>
        public static void Build()
        {
            if (BlobchegPatchTable.IsBuilt)
                return;

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

                // Общий компонент лежит в мире одним значением на индекс, а не в чанке рядом с
                // сущностью: ни обход чанков в форке, ни обратный проход перед записью до него не
                // добираются. Молча оставить в нём оффсет — худшее из возможного, поэтому вслух.
                if (typeof(ISharedComponentData).IsAssignableFrom(type))
                {
                    s_Diagnostics.Add(
                        $"Blobcheg: в общем компоненте '{type.FullName}' объявлен слот " +
                        "BlobchegReference — патч общие компоненты не обходит, слот останется оффсетом. " +
                        "Перенеси ссылку в обычный компонент или читай её через «оффсет плюс Read»");
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

        /// <summary>
        /// Снимает таблицу. Нужен на перезагрузке домена в редакторе: managed-сторона там умирает
        /// сама, а persistent-память — нет.
        /// </summary>
        public static void Destroy()
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

        /// <summary>
        /// Маркер-интерфейс домена → ключ его базы. Домены объявлены атрибутом
        /// <see cref="BlobchegAttribute"/>, и другим доменам взяться неоткуда: имя файла, личность в
        /// header'е и ключ реестра считаются из одного и того же имени маркера.
        /// </summary>
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

                    // Имя нужно раньше, чем поднимется база: самое частое сообщение патча — «домен
                    // не поднят», и в нём домен обязан быть назван, а не показан ключом FNV-64.
                    BlobchegDomainNames.Remember(key, attr.Domain.Name);
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

                    // Общие компоненты сюда тоже попадают — не чтобы регистрировать, а чтобы
                    // заметить в них слот и сказать об этом. Молчание было бы хуже.
                    if (typeof(IComponentData).IsAssignableFrom(type) ||
                        typeof(IBufferElementData).IsAssignableFrom(type) ||
                        typeof(ISharedComponentData).IsAssignableFrom(type))
                        yield return type;
                }
            }
        }

        /// <summary>
        /// Только сборки, которые видят <c>Blobcheg.Runtime</c>. Полный обход домена приложения на
        /// старте стоит заметно, а слот нашего типа в сборке, не знающей о пакете, не появится.
        /// </summary>
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

        /// <summary>
        /// Обход полей по образцу <c>EntityRemapUtility.CalculateOffsetsRecurse</c>: спуск во
        /// вложенные value-структуры, матч по типу поля, а не по имени. Останавливаемся на самом
        /// слоте — внутрь <see cref="BlobchegReference{T}"/> лезть нечего.
        /// </summary>
        static void Walk(Type type, int baseOffset, List<BlobchegFieldSlot> found, HashSet<Type> seen,
            Dictionary<Type, ulong> domains, int depth)
        {
            if (depth > 32)
                throw new InvalidOperationException(
                    $"Blobcheg: вложенность структуры '{type.FullName}' глубже 32 — обход полей отказался");

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
                        $"Blobcheg: в '{type.FullName}' поле '{field.Name}' объявлено как " +
                        "BlobchegReferenceData напрямую. Это нутро слота, а не поле: домен из него не " +
                        "выводится. Объяви BlobchegReference<T>");

                if (fieldType == typeof(BlobchegRawReference))
                    throw new InvalidOperationException(
                        $"Blobcheg: в '{type.FullName}' поле '{field.Name}' — BlobchegRawReference. " +
                        "У сырой записи нет типа, значит и домена, и патчить её нечем. Оставь такие " +
                        "записи на путь «оффсет плюс Read»");

                // Циклов у value-структур не бывает, но одна и та же структура может встретиться в
                // соседних полях — повторный спуск в неё по тому же пути и ограничивает seen.
                if (!seen.Add(fieldType))
                    continue;

                Walk(fieldType, offset, found, seen, domains, depth + 1);
                seen.Remove(fieldType);
            }
        }

        /// <summary>
        /// Личность типа записи — тем же счётом, каким её пишет в отладочный контур писатель базы
        /// (<c>BurstRuntime.GetHashCode32&lt;T&gt;</c>). Обобщённый метод через рефлексию, потому что
        /// на этапе сборки таблицы тип записи — это <c>Type</c>, а не параметр.
        /// </summary>
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
                "Blobcheg: в этой версии Burst нет BurstRuntime.GetHashCode32<T>() — сверять тип записи нечем");
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
                        $"Blobcheg: запись '{record.FullName}' входит сразу в домены '{marker.Name}' и " +
                        $"'{iface.Name}'. Патч не может выбрать, из какой базы брать адрес");

                marker = iface;
                key = candidate;
            }

            if (marker == null)
                throw new InvalidOperationException(
                    $"Blobcheg: запись '{record.FullName}' не входит ни в один домен — нет маркер-интерфейса, " +
                    "объявленного через [Blobcheg]. Патчить её неоткуда");

            return key;
        }
    }
}
