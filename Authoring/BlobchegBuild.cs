using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Blobcheg.Authoring
{
    /// <summary>Что сделала пересборка. Для логов, гейта пре-билда и тестов.</summary>
    public struct BlobchegBuildReport
    {
        public int Domains;
        public int Routers;
        public int Records;
        public int ChangedFiles;
        public int ChangedManifests;
        public int ChangedRefs;
        public int RemovedRefs;

        /// <summary>Нод, которым пересборка проставила пустое имя. Второй заход обязан дать ноль.</summary>
        public int NamedNodes;

        public bool Changed => ChangedFiles > 0 || ChangedRefs > 0 || RemovedRefs > 0
                               || ChangedManifests > 0 || NamedNodes > 0;

        public override string ToString()
            => $"домены {Domains}, роутеры {Routers}, записи {Records}, переписано файлов {ChangedFiles}, " +
               $"манифестов {ChangedManifests}, обновлено ref'ов {ChangedRefs}, удалено {RemovedRefs}, " +
               $"названо нод {NamedNodes}";
    }

    /// <summary>
    /// Пересборка баз. Кнопки Save нет намеренно: она даёт только возможность про себя забыть —
    /// собранный час назад блоб при свежих ассетах выглядит рабочим и врёт. Пересборку зовут хуки
    /// импорта, вход в PlayMode и пре-билд.
    ///
    /// Раскладка детерминирована, поэтому пересборка идемпотентна: не изменилось ничего — не
    /// переписан ни файл, ни один ассет, и ничего не перепекается.
    /// </summary>
    public static class BlobchegBuild
    {
        public const string ManifestFolder = "Assets/Blobcheg";

        public static string OutputDirectory
            => Path.Combine(Application.streamingAssetsPath, BlobchegNaming.DefaultFolder);

        /// <summary>
        /// Пишется ли в файлы отладочный контур. В редакторе — всегда: на нём стоит проверка типа
        /// при чтении, и без него она существует только на бумаге. Снимает его ровно одно место —
        /// гейт пре-билда нерелизного плеера, см. <see cref="BlobchegBuildGate"/>.
        /// </summary>
        public static bool WithDebug => DebugContour;

        internal static bool DebugContour = true;

        /// <summary>Пересборка идёт внутри — импорт своих же носителей кешу не новость.</summary>
        public static bool Building { get; private set; }

        /// <summary>
        /// Обычная пересборка: то, что не менялось, берётся из памяти. Её зовут хуки импорта — то
        /// есть она случается на каждое сохранение ноды, и стоить обязана столько, сколько
        /// изменилось.
        /// </summary>
        public static BlobchegBuildReport RebuildAll() => Rebuild(true, false);

        /// <summary>
        /// Пересборка с нуля: кеш забыт, проект обойдён, Write позван у всех. Ею идут пре-билд и
        /// всё, где «собралось» обязано значить «собралось из ассетов, а не из памяти».
        /// </summary>
        public static BlobchegBuildReport RebuildFull()
        {
            BlobchegCache.Drop();
            return Rebuild(false, false);
        }

        /// <summary>
        /// Компакт: раскладка считается с нуля, дырки от удалённых нод исчезают, адреса и id
        /// выдаются заново подряд. Сам собой он не случается — уезжают ВСЕ адреса, а на них через
        /// DependsOn завязано всё, что их когда-то запомнило.
        ///
        /// Мест ровно два: пре-билд, где следом всё равно перепекается всё, и команда в редакторе,
        /// которую человек зовёт сам.
        /// </summary>
        public static BlobchegBuildReport Compact()
        {
            BlobchegCache.Drop();
            return Rebuild(false, true);
        }

        static BlobchegBuildReport Rebuild(bool incremental, bool compact)
        {
            // Реентранс отбивается здесь, а не на хуке импорта: нода в своём Write может тронуть
            // AssetDatabase чем угодно и войти в пересборку из середины пересборки. Вложенный заход
            // идёт поверх наполовину заполненного коллектора и наполовину розданных id, и «файл
            // собран» после него не значит ничего.
            if (Building)
                throw new InvalidOperationException(
                    "Blobcheg: пересборка вошла сама в себя — скорее всего нода зовёт RebuildAll " +
                    "из Write. Пересборка обязана быть одна: вложенная идёт поверх наполовину " +
                    "розданных адресов и id");

            var report = new BlobchegBuildReport();
            var collector = new BlobchegCollector(OutputDirectory);

            Building = true;
            try
            {
                return Run(collector, incremental, compact, ref report);
            }
            finally
            {
                Building = false;
            }
        }

        static BlobchegBuildReport Run(BlobchegCollector collector, bool incremental, bool compact,
            ref BlobchegBuildReport report)
        {
            IReadOnlyList<BlobchegCache.Entry> entries;
            using (BlobchegProfile.Section("Список нод"))
                entries = BlobchegCache.Fill();

            var nodes = new List<BlobchegNodeSo>(entries.Count);
            foreach (var entry in entries)
            {
                nodes.Add(entry.Node);

                // Правка в инспекторе импорта не даёт: ассет грязный в памяти и на диске ещё
                // старый. Пересборка обязана видеть то, что видит человек на экране.
                if (!incremental || EditorUtility.IsDirty(entry.Node))
                    entry.Dirty = true;
            }

            // Имя нужно ноде раньше, чем она пишет: запись может положить в себя хеш своего имени.
            // Пачки StartAssetEditing здесь нет намеренно — она стоит позже и заведена под
            // AddObjectToAsset, а SetDirty на самой ноде переимпорта не вызывает.
            using (BlobchegProfile.Section("Имена нод"))
            {
                foreach (var entry in entries)
                {
                    if (!entry.Node.EnsureName())
                        continue;

                    entry.Dirty = true;
                    EditorUtility.SetDirty(entry.Node);
                    report.NamedNodes++;
                }
            }

            // Носители читаются один раз на всю пересборку: они и журнал уже выданных адресов, и
            // то, что в конце придётся сверить и переписать.
            BlobchegCarriers carriers;
            using (BlobchegProfile.Section("Чтение носителей"))
                carriers = BlobchegCarriers.Read(entries);

            // Id раздаются ДО записи: они выводятся из OutTypes, а не из того, что нода написала,
            // поэтому нода может положить свой id прямо в запись за один проход.
            BlobchegIdTable ids;
            using (BlobchegProfile.Section("Assign id'ов"))
                ids = BlobchegIdTable.Assign(nodes, compact ? null : carriers);

            // Писатель открывается на КАЖДЫЙ объявленный домен, даже пустой: иначе домен, из
            // которого удалили последнюю ноду, остался бы на диске старым файлом.
            foreach (var domain in BlobchegDomains.All)
                collector.WriterOf(domain);

            using (BlobchegProfile.Section("node.Write — изменившиеся ноды"))
            {
                foreach (var entry in entries)
                    WriteNode(entry, collector, ids);
            }

            using (BlobchegProfile.Section("Записи писателям"))
                collector.Handover();

            // Адреса прошлой пересборки уходят писателю ДО Flush: раскладка обязана оставить
            // нетронутые записи на их местах, иначе каждая новая нода двигает чужие адреса, а на
            // них через DependsOn завязаны запечённые субсцены.
            using (BlobchegProfile.Section("Заявки на прежние адреса"))
            {
                foreach (var entry in collector.Entries)
                {
                    // Компакт — это отказ от прежних адресов: заявок нет вовсе.
                    if (compact)
                        break;

                    var reference = carriers.Ref(entry.Node, BlobchegDomains.NameOf(entry.Domain));
                    if (reference != null)
                        collector.Writers[entry.Domain].Claim(entry.Ticket, reference.offset);
                }
            }

            using (BlobchegProfile.Section("Flush баз"))
            {
                foreach (var pair in collector.Writers)
                {
                    pair.Value.Flush(WithDebug);
                    report.Domains++;
                    report.Records += pair.Value.RecordCount;
                    if (pair.Value.FileChanged)
                    {
                        report.ChangedFiles++;
                        BlobchegFileVersions.Bump(BlobchegNaming.FileName(BlobchegDomains.NameOf(pair.Key)));
                    }
                }
            }

            // Роутеры собираются ПОСЛЕ Flush: до него оффсетов, из которых состоят строки, не
            // существует вовсе.
            var offsets = new Dictionary<(BlobchegNodeSo, Type), uint>();
            foreach (var entry in collector.Entries)
                offsets[(entry.Node, entry.Domain)] = collector.Writers[entry.Domain].OffsetOf(entry.Ticket);

            using (BlobchegProfile.Section("BuildRouters"))
                BuildRouters(offsets, ids, ref report);

            // Производные файлы — таблица хешей и всё, что заведут после неё. Ядро о них не знает:
            // оно отдаёт готовую раскладку и принимает обратно счётчики отчёта.
            using (BlobchegProfile.Section("Пост-проходы"))
                RunPasses(new BlobchegBuildLayout(ids, offsets), ref report);

            // Носители пишутся пачкой: поштучный AddObjectToAsset переимпортирует ноду на каждый
            // сабассет, и на большом проекте вся пересборка — это он и есть. Замер на 500 нодах:
            // 34 мс на носитель без пачки против 9 мс с ней.
            //
            // Манифесты остаются снаружи: они сохраняются адресно, а адресное сохранение внутри
            // пачки не срабатывает — см. комментарий в SyncManifest.
            AssetDatabase.StartAssetEditing();
            try
            {
                using (BlobchegProfile.Section("SyncRefs"))
                    SyncRefs(collector, carriers, nodes, ref report);

                using (BlobchegProfile.Section("SyncIds"))
                    SyncIds(ids, carriers, nodes, ref report);
            }
            finally
            {
                using (BlobchegProfile.Section("StopAssetEditing — переимпорт пачки"))
                    AssetDatabase.StopAssetEditing();
            }

            using (BlobchegProfile.Section("SyncManifests"))
                SyncManifests(collector, nodes, ref report);

            if (report.Changed)
            {
                using (BlobchegProfile.Section("SaveAssets"))
                    AssetDatabase.SaveAssets();

                using (BlobchegProfile.Section("Refresh"))
                    AssetDatabase.Refresh();
            }

            // Кеш обновляется в самом конце и только тем, что пересборка действительно записала.
            foreach (var entry in entries)
            {
                entry.Refs = carriers.RefListOf(entry.Node);
                entry.Ids = carriers.IdListOf(entry.Node);
                entry.Dirty = false;
            }

            return report;
        }

        /// <summary>
        /// Нода, которую никто не трогал, отдаёт прошлые байты: <c>Write</c> у неё не зовут вовсе.
        /// Байты те же самые, что коллектор получил в прошлый раз, поэтому раскладка от этого не
        /// зависит — зависит только цена.
        /// </summary>
        static void WriteNode(BlobchegCache.Entry entry, BlobchegCollector collector, BlobchegIdTable ids)
        {
            var node = entry.Node;
            var now = IdsNow(node, ids);

            if (!entry.Dirty && entry.Records != null && Same(entry.IdsAtWrite, now))
            {
                using (BlobchegProfile.Section("  нода из кеша"))
                {
                    foreach (var written in entry.Records)
                        collector.Add(node, written.Domain, written.RecordType, written.TypeHash, written.Bytes);
                }

                return;
            }

            using var _ = BlobchegProfile.Section("  нода посчитана заново");

            var start = collector.Entries.Count;

            var writer = new BlobchegNodeWriter { Collector = collector, Node = node, Ids = ids };
            node.Write(ref writer);

            foreach (var domain in BlobchegDomains.DomainsOf(node))
            {
                if (!collector.Wrote(node, domain))
                    throw new InvalidOperationException(
                        $"Blobcheg: нода '{node.name}' объявила домен '{domain.Name}' в OutTypes, но ничего в него не написала");
            }

            var records = new List<BlobchegCache.Written>();
            for (var i = start; i < collector.Entries.Count; i++)
            {
                var written = collector.Entries[i];
                records.Add(new BlobchegCache.Written
                {
                    Domain = written.Domain,
                    RecordType = written.RecordType,
                    TypeHash = written.TypeHash,
                    Bytes = written.Bytes,
                });
            }

            entry.Records = records;
            entry.IdsAtWrite = now;
        }

        /// <summary>Id ноды во всех роутерах сразу. Нода вне роутера — <see cref="BlobchegId.NoneValue"/>.</summary>
        static uint[] IdsNow(BlobchegNodeSo node, BlobchegIdTable ids)
        {
            var routers = BlobchegRouters.All;
            var now = new uint[routers.Length];

            for (var i = 0; i < routers.Length; i++)
                now[i] = ids.TryOf(node, routers[i], out var id) ? id.Value : BlobchegId.NoneValue;

            return now;
        }

        static bool Same(uint[] were, uint[] are)
        {
            if (were == null || are == null || were.Length != are.Length)
                return false;

            for (var i = 0; i < are.Length; i++)
            {
                if (were[i] != are[i])
                    return false;
            }

            return true;
        }

        /// <summary>
        /// Гейт пре-билда: пересобрать, потом пересобрать ещё раз и потребовать, чтобы второй заход
        /// не изменил ничего. Первый заход чинит протухший блоб, второй доказывает, что раскладка
        /// детерминирована — иначе в билд поехало бы то, что при следующей сборке будет другим.
        ///
        /// Оба захода — полные: в билд обязано ехать то, что собралось из ассетов, а не из памяти
        /// редактора. Заодно это единственная проверка кеша, которая вообще возможна: если он
        /// разошёлся с ассетами, второй заход это увидит.
        /// </summary>
        public static void RequireUpToDate(string what)
        {
            RebuildFull();

            var again = RebuildFull();
            if (again.Changed)
                throw new InvalidOperationException(
                    $"Blobcheg: {what} — пересборка не сошлась сама с собой ({again}). " +
                    "Раскладка обязана быть детерминированной; ехать с такой базой нельзя");
        }

        /// <summary>
        /// Состав домена ищется по проекту, а не берётся из ручного списка: список — это ещё одно
        /// место, где можно забыть.
        ///
        /// Обходов два, и оба обязательны, потому что отстают они на разных событиях: поисковый
        /// индекс <c>FindAssets("t:...")</c> отстаёт от импорта (в батче свежесозданная нода в нём
        /// не находится вовсе), а полный обход <c>GetAllAssetPaths</c> — от переезда ассета.
        /// Личность ноды здесь GUID, а не путь: под двумя путями это один и тот же ассет.
        ///
        /// Есть состояние, в котором ноду не находит НИКАКОЙ обход: сразу после переименования её
        /// путь уже новый, GUID известен, а тип и объект по нему ещё не поднимаются, и ни
        /// <c>ImportAsset(ForceSynchronousImport)</c>, ни <c>Refresh</c> этого не меняют (замерено).
        /// Собирать в таком состоянии нельзя: запись ноды ушла бы из файла, а id соседей поехали бы,
        /// и всё это молча. Поэтому обход помнит GUID'ы, которые уже видел, и на потере отказывается
        /// — см. <see cref="Lost"/>.
        /// </summary>
        public static List<BlobchegNodeSo> FindNodes()
            => FindNodesByGuid().Values.OrderBy(AssetDatabase.GetAssetPath, StringComparer.Ordinal).ToList();

        /// <summary>То же самое, но с GUID'ами: их кладёт себе кеш, чтобы не спрашивать базу заново.</summary>
        internal static Dictionary<string, BlobchegNodeSo> FindNodesByGuid()
        {
            var found = Walk();

            var lost = Lost(found);
            if (lost != null)
            {
                // База ассетов ещё не переварила переименование: файл на диске лежит, GUID известен,
                // а ни тип, ни объект по нему ещё не поднимаются. Даём ей досчитать и идём заново.
                AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
                found = Walk();
                lost = Lost(found);
            }

            if (lost != null)
                throw new InvalidOperationException(
                    $"Blobcheg: нода '{lost}' пропала из обхода, но её файл лежит на диске — база " +
                    "ассетов ещё не переварила переименование. Пересборка в этом состоянии выкинула " +
                    "бы её запись из файла и сдвинула id соседей, причём молча, поэтому она " +
                    "отказывается. Повтори, когда редактор доимпортирует");

            Seen.UnionWith(found.Keys);
            return found;
        }

        /// <summary>
        /// GUID'ы нод, о которых пайплайн знает в этой сессии: их кладут сюда и обход, и кеш —
        /// нода, созданная между полными обходами, известна только кешу. Набор переживает
        /// <c>BlobchegCache.Drop</c> и не переживает перезагрузку домена — ровно то окно, в котором
        /// нода и теряется: ассет, переименованный до перезагрузки, после неё импортирован полностью.
        /// </summary>
        static readonly HashSet<string> Seen = new HashSet<string>(StringComparer.Ordinal);

        internal static void Remember(string guid)
        {
            if (!string.IsNullOrEmpty(guid))
                Seen.Add(guid);
        }

        static Dictionary<string, BlobchegNodeSo> Walk()
        {
            var byGuid = new Dictionary<string, BlobchegNodeSo>(StringComparer.Ordinal);

            foreach (var path in AssetDatabase.GetAllAssetPaths())
                Consider(path, null, byGuid);

            foreach (var guid in AssetDatabase.FindAssets("t:" + nameof(BlobchegNodeSo)))
            {
                // Сначала GUID, потом путь: на 10 000 нод второй заход почти целиком попадает в
                // уже найденное, и платить за него native-вызовами незачем.
                if (byGuid.ContainsKey(guid))
                    continue;

                Consider(AssetDatabase.GUIDToAssetPath(guid), guid, byGuid);
            }

            return byGuid;
        }

        /// <summary>
        /// Нода, которую обход знал и больше не видит, хотя её файл на месте. Путь такой ноды — или
        /// <c>null</c>, если потерь нет. Заодно чистит из <see cref="Seen"/> тех, кого действительно
        /// не стало.
        ///
        /// Отличить потерю от нормального ухода можно по трём признакам сразу: файл лежит на диске,
        /// GUID ещё указывает на путь, а тип по этому пути не спрашивается. Удалённая нода теряет
        /// файл, переставшая быть нодой — отдаёт свой новый тип, и обе проходят мимо.
        /// </summary>
        static string Lost(Dictionary<string, BlobchegNodeSo> found)
        {
            List<string> gone = null;

            foreach (var guid in Seen)
            {
                if (found.ContainsKey(guid))
                    continue;

                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (!string.IsNullOrEmpty(path)
                    && AssetDatabase.GetMainAssetTypeAtPath(path) == null
                    && File.Exists(Path.Combine(Application.dataPath, "..", path)))
                    return path;

                (gone ?? (gone = new List<string>())).Add(guid);
            }

            if (gone != null)
            {
                foreach (var guid in gone)
                    Seen.Remove(guid);
            }

            return null;
        }

        static void Consider(string path, string knownGuid, Dictionary<string, BlobchegNodeSo> byGuid)
        {
            if (string.IsNullOrEmpty(path) || !path.EndsWith(".asset", StringComparison.OrdinalIgnoreCase))
                return;

            var type = AssetDatabase.GetMainAssetTypeAtPath(path);
            if (type == null || !typeof(BlobchegNodeSo).IsAssignableFrom(type))
                return;

            var guid = knownGuid ?? AssetDatabase.AssetPathToGUID(path);
            if (string.IsNullOrEmpty(guid) || byGuid.ContainsKey(guid))
                return;

            var node = AssetDatabase.LoadAssetAtPath<BlobchegNodeSo>(path);

            if (node == null)
            {
                // Путь из обхода отстал от переименования: спрашиваем базу, где ассет лежит сейчас.
                var now = AssetDatabase.GUIDToAssetPath(guid);
                if (!string.Equals(now, path, StringComparison.Ordinal))
                    node = AssetDatabase.LoadAssetAtPath<BlobchegNodeSo>(now);
            }

            if (node == null)
                throw new InvalidOperationException(
                    $"Blobcheg: '{path}' объявлен нодой ({type.Name}), но не грузится. Пропустить его молча " +
                    "нельзя: его запись ушла бы из базы, а id соседей поехали бы");

            byGuid.Add(guid, node);
        }

        static void SyncRefs(BlobchegCollector collector, BlobchegCarriers carriers,
            List<BlobchegNodeSo> nodes, ref BlobchegBuildReport report)
        {
            var wanted = new HashSet<BlobchegRefSo>();

            foreach (var entry in collector.Entries)
            {
                var writer = collector.Writers[entry.Domain];
                var reference = Upsert(entry, writer, carriers, ref report);
                wanted.Add(reference);
            }

            // По всем нодам, а не только по писавшим: нода могла перестать писать вовсе, и её
            // ref-ассет обязан уехать вместе с записью.
            foreach (var node in nodes)
            {
                var staleRefs = carriers.RefsOf(node).Where(r => !wanted.Contains(r)).ToList();

                foreach (var stale in staleRefs)
                {
                    carriers.Forget(node, stale);
                    AssetDatabase.RemoveObjectFromAsset(stale);
                    UnityEngine.Object.DestroyImmediate(stale, true);
                    report.RemovedRefs++;
                }
            }
        }

        static BlobchegRefSo Upsert(BlobchegEntry entry, BlobchegWriter writer, BlobchegCarriers carriers,
            ref BlobchegBuildReport report)
        {
            var domainName = BlobchegDomains.NameOf(entry.Domain);
            var wantedName = entry.Node.name + "_" + domainName;
            var offset = writer.OffsetOf(entry.Ticket);
            var revision = unchecked((long)writer.RevisionOf(entry.Ticket));

            var reference = carriers.Ref(entry.Node, domainName);

            if (reference == null)
            {
                reference = ScriptableObject.CreateInstance<BlobchegRefSo>();
                reference.name = wantedName;
                reference.domainName = domainName;
                using (BlobchegProfile.Section("  refs: AddObjectToAsset"))
                    AssetDatabase.AddObjectToAsset(reference, entry.Node);

                carriers.Add(entry.Node, reference);
            }
            else if (reference.offset == offset
                     && reference.revision == revision
                     && string.Equals(reference.recordType, entry.RecordType, StringComparison.Ordinal)
                     && reference.name == wantedName)
            {
                return reference;
            }

            reference.name = wantedName;
            reference.domainName = domainName;
            reference.recordType = entry.RecordType;
            reference.offset = offset;
            reference.revision = revision;

            using (BlobchegProfile.Section("  refs: SetDirty"))
                EditorUtility.SetDirty(reference);

            report.ChangedRefs++;
            return reference;
        }

        // Лейблов на носителях больше нет. Их не читал никто — ни пикер (он ходит по нодам и
        // смотрит recordType), ни бейк, — а стоил каждый AssetDatabase.SetLabels 4,7 мс: на 500
        // нодах это 7,1 с из 14,4 с холодной сборки. Замер: docs/blobcheg-editor-scale.md.

        /// <summary>Ref-ассеты ноды — по одному на домен, в который она пишет.</summary>
        public static IEnumerable<BlobchegRefSo> RefsOf(BlobchegNodeSo node)
        {
            var path = AssetDatabase.GetAssetPath(node);
            if (string.IsNullOrEmpty(path))
                yield break;

            foreach (var asset in AssetDatabase.LoadAllAssetsAtPath(path))
            {
                if (asset is BlobchegRefSo reference)
                    yield return reference;
            }
        }

        /// <summary>
        /// Файл роутера: строка на ноду в порядке id, в строке — оффсеты во всех базах роутера, где
        /// нода есть. Пустая строка допустима: нода могла войти в роутер одной базой из десяти.
        /// </summary>
        static void BuildRouters(Dictionary<(BlobchegNodeSo, Type), uint> offsets, BlobchegIdTable ids,
            ref BlobchegBuildReport report)
        {
            if (BlobchegRouters.All.Length == 0)
                return;

            foreach (var router in BlobchegRouters.All)
            {
                // Сверка с кодогеном до записи файла: собрать файл под одну нумерацию бит, а читать
                // кодом под другую — ровно то расхождение, ради которого заведён LayoutHash.
                BlobchegRouters.RequireCodeGenAgrees(router);

                var domains = BlobchegRouters.DomainsOf(router);
                var name = BlobchegRouters.NameOf(router);
                var writer = BlobchegRouterWriter.Open(
                    OutputDirectory, name, domains.Length, BlobchegRouters.LayoutHashOf(router));

                var members = ids.NodesOf(router);
                var cells = new List<BlobchegRouterCell>();

                foreach (var node in members)
                {
                    cells.Clear();

                    // Дырка от удалённой ноды: строка есть, но пустая. Убрать её — значит сдвинуть
                    // id всех, кто стоит следом, а id уже уехал в чужие сейвы и субсцены.
                    if (node != null)
                    {
                        for (var bit = 0; bit < domains.Length; bit++)
                        {
                            if (offsets.TryGetValue((node, domains[bit]), out var offset))
                                cells.Add(new BlobchegRouterCell(bit, offset));
                        }
                    }

                    writer.Append(node == null ? string.Empty : node.name, cells);
                }

                writer.Flush(WithDebug);

                report.Routers++;
                if (writer.FileChanged)
                {
                    report.ChangedFiles++;
                    BlobchegFileVersions.Bump(BlobchegNaming.FileName(name));
                }

                SyncRouterManifest(name, writer, members, ref report);
            }
        }

        static void SyncRouterManifest(string name, BlobchegRouterWriter writer,
            IReadOnlyList<BlobchegNodeSo> members, ref BlobchegBuildReport report)
        {
            // Порядок нод в манифесте — порядок id. Это и есть таблица «id → нода» для глаз.
            SyncManifest(name, BlobchegFileKind.Router, members.ToArray(), writer.RowCount,
                writer.ContentHash, writer.FileChanged, ref report);
        }

        /// <summary>
        /// Чужие проходы по готовой раскладке. Порядок — по полному имени типа: пересборка обязана
        /// быть детерминированной, а порядок, в котором типы отдаёт <c>TypeCache</c>, ей не обещан.
        /// </summary>
        static void RunPasses(BlobchegBuildLayout layout, ref BlobchegBuildReport report)
        {
            var passes = TypeCache.GetTypesDerivedFrom<IBlobchegBuildPass>()
                .Where(type => !type.IsAbstract && !type.IsInterface)
                .OrderBy(type => type.FullName, StringComparer.Ordinal)
                .ToList();

            foreach (var type in passes)
            {
                var pass = (IBlobchegBuildPass)Activator.CreateInstance(type);

                using (BlobchegProfile.Section("  " + type.Name))
                    pass.Run(layout, ref report);
            }
        }

        /// <summary>Носители id: по одному sub-ассету на пару (нода × роутер).</summary>
        static void SyncIds(BlobchegIdTable ids, BlobchegCarriers carriers,
            List<BlobchegNodeSo> nodes, ref BlobchegBuildReport report)
        {
            var wanted = new HashSet<BlobchegIdSo>();

            foreach (var router in BlobchegRouters.All)
            {
                var name = BlobchegRouters.NameOf(router);
                var members = ids.NodesOf(router);

                foreach (var node in members)
                {
                    if (node != null)
                        wanted.Add(UpsertId(node, name, ids.Of(node, router), carriers, ref report));
                }
            }

            foreach (var node in nodes)
            {
                var staleIds = carriers.IdsOf(node).Where(id => !wanted.Contains(id)).ToList();

                foreach (var stale in staleIds)
                {
                    carriers.Forget(node, stale);
                    AssetDatabase.RemoveObjectFromAsset(stale);
                    UnityEngine.Object.DestroyImmediate(stale, true);
                    report.RemovedRefs++;
                }
            }
        }

        static BlobchegIdSo UpsertId(BlobchegNodeSo node, string routerName, BlobchegId id,
            BlobchegCarriers carriers, ref BlobchegBuildReport report)
        {
            var wantedName = node.name + "_" + routerName;

            var carrier = carriers.Id(node, routerName);

            if (carrier == null)
            {
                carrier = ScriptableObject.CreateInstance<BlobchegIdSo>();
                carrier.name = wantedName;
                carrier.routerName = routerName;
                using (BlobchegProfile.Section("  ids: AddObjectToAsset"))
                    AssetDatabase.AddObjectToAsset(carrier, node);

                carriers.Add(node, carrier);
            }
            else if (carrier.id == id.Value && carrier.name == wantedName)
            {
                return carrier;
            }

            carrier.name = wantedName;
            carrier.routerName = routerName;
            carrier.id = id.Value;

            using (BlobchegProfile.Section("  ids: SetDirty"))
                EditorUtility.SetDirty(carrier);

            report.ChangedRefs++;
            return carrier;
        }

        /// <summary>Носители id ноды — по одному на роутер, в который она входит.</summary>
        public static IEnumerable<BlobchegIdSo> IdsOf(BlobchegNodeSo node)
        {
            var path = AssetDatabase.GetAssetPath(node);
            if (string.IsNullOrEmpty(path))
                yield break;

            foreach (var asset in AssetDatabase.LoadAllAssetsAtPath(path))
            {
                if (asset is BlobchegIdSo carrier)
                    yield return carrier;
            }
        }

        static void SyncManifests(BlobchegCollector collector, List<BlobchegNodeSo> nodes, ref BlobchegBuildReport report)
        {
            // Один проход по нодам на все домены сразу: OutTypes у ноды — свойство, и обычная нода
            // собирает массив заново на каждый спрос. Спрашивать его по разу на домен — это тот же
            // проход, умноженный на число доменов.
            var members = new Dictionary<Type, List<BlobchegNodeSo>>();
            foreach (var pair in collector.Writers)
                members.Add(pair.Key, new List<BlobchegNodeSo>());

            foreach (var node in nodes)
            {
                var declared = node.OutTypes;
                if (declared == null)
                    continue;

                foreach (var domain in declared)
                {
                    if (members.TryGetValue(domain, out var list))
                        list.Add(node);
                }
            }

            foreach (var pair in collector.Writers)
            {
                var domainName = BlobchegDomains.NameOf(pair.Key);

                SyncManifest(domainName, BlobchegFileKind.Database, members[pair.Key].ToArray(),
                    pair.Value.RecordCount, pair.Value.ContentHash, pair.Value.FileChanged, ref report);
            }
        }

        /// <summary>
        /// Манифест переписывается, если ХОТЬ ЧТО-ТО в нём разошлось с собранным — не только хеш.
        /// Иначе манифест, созданный в заход, где больше ничего не изменилось, так и остаётся на
        /// диске пустой заготовкой: <c>CreateAsset</c> пишет его до заполнения полей, а
        /// <c>SaveAssets</c> в таком заходе не зовётся вовсе.
        /// </summary>
        internal static void SyncManifest(string name, BlobchegFileKind kind, BlobchegNodeSo[] members,
            int recordCount, ulong contentHash, bool fileChanged, ref BlobchegBuildReport report)
        {
            var fileName = BlobchegNaming.FileName(name);
            var manifest = LoadOrCreateManifest(name, out var created);

            var same = !created
                       && !fileChanged
                       && manifest.kind == kind
                       && manifest.domainName == name
                       && manifest.fileName == fileName
                       && manifest.recordCount == recordCount
                       && manifest.ContentHash == contentHash
                       && SameNodes(manifest.nodes, members);

            if (same)
                return;

            manifest.kind = kind;
            manifest.domainName = name;
            manifest.fileName = fileName;
            manifest.recordCount = recordCount;
            manifest.nodes = members;
            manifest.ContentHash = contentHash;
            manifest.builtAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

            // Пишется адресно, а не общим SaveAssets в конце: свежесозданный CreateAsset'ом манифест
            // база успевает перечитать с диска (пустой заготовкой, какой он был до заполнения полей),
            // и заполнение теряется молча.
            EditorUtility.SetDirty(manifest);
            AssetDatabase.SaveAssetIfDirty(manifest);
            report.ChangedManifests++;
        }

        static bool SameNodes(BlobchegNodeSo[] were, BlobchegNodeSo[] are)
        {
            if (were == null || were.Length != are.Length)
                return false;

            // Сравнение Unity'шным ==, а не ReferenceEquals: реимпорт ассета меняет managed-обёртку,
            // оставляя тот же объект — на ReferenceEquals манифест «менялся» бы каждую пересборку.
            for (var i = 0; i < are.Length; i++)
            {
                if (were[i] != are[i])
                    return false;
            }

            return true;
        }

        static BlobchegDomainSo LoadOrCreateManifest(string domainName, out bool created)
        {
            var path = ManifestFolder + "/" + domainName + ".asset";
            var manifest = AssetDatabase.LoadAssetAtPath<BlobchegDomainSo>(path);
            created = manifest == null;
            if (manifest != null)
                return manifest;

            Directory.CreateDirectory(ManifestFolder);
            AssetDatabase.ImportAsset(ManifestFolder);

            manifest = ScriptableObject.CreateInstance<BlobchegDomainSo>();
            AssetDatabase.CreateAsset(manifest, path);

            // Дальше работаем с тем объектом, который держит база, а не с тем, который ей отдали:
            // после CreateAsset это не обязательно один и тот же экземпляр.
            return AssetDatabase.LoadAssetAtPath<BlobchegDomainSo>(path) ?? manifest;
        }
    }
}
