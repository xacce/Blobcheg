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

        public bool Changed => ChangedFiles > 0 || ChangedRefs > 0 || RemovedRefs > 0 || ChangedManifests > 0;

        public override string ToString()
            => $"домены {Domains}, роутеры {Routers}, записи {Records}, переписано файлов {ChangedFiles}, " +
               $"манифестов {ChangedManifests}, обновлено ref'ов {ChangedRefs}, удалено {RemovedRefs}";
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

        public static bool WithDebug =>
#if BLOBCHEG_DEBUG
            true;
#else
            false;
#endif

        public static BlobchegBuildReport RebuildAll()
        {
            var report = new BlobchegBuildReport();
            var collector = new BlobchegCollector(OutputDirectory);
            var nodes = FindNodes();

            // Id раздаются ДО записи: они выводятся из OutTypes, а не из того, что нода написала,
            // поэтому нода может положить свой id прямо в запись за один проход.
            var ids = BlobchegIdTable.Assign(nodes);

            // Писатель открывается на КАЖДЫЙ объявленный домен, даже пустой: иначе домен, из
            // которого удалили последнюю ноду, остался бы на диске старым файлом.
            foreach (var domain in BlobchegDomains.All)
                collector.WriterOf(domain);

            foreach (var node in nodes)
            {
                var writer = new BlobchegNodeWriter { Collector = collector, Node = node, Ids = ids };
                node.Write(ref writer);

                foreach (var domain in BlobchegDomains.DomainsOf(node))
                {
                    if (!collector.Wrote(node, domain))
                        throw new InvalidOperationException(
                            $"Blobcheg: нода '{node.name}' объявила домен '{domain.Name}' в OutTypes, но ничего в него не написала");
                }
            }

            foreach (var pair in collector.Writers)
            {
                pair.Value.Flush(WithDebug);
                report.Domains++;
                report.Records += pair.Value.RecordCount;
                if (pair.Value.FileChanged)
                    report.ChangedFiles++;
            }

            // Роутеры собираются ПОСЛЕ Flush: до него оффсетов, из которых состоят строки, не
            // существует вовсе.
            BuildRouters(collector, ids, ref report);

            // Носители пишутся пачкой: поштучный AddObjectToAsset переимпортирует ноду на каждый
            // сабассет, и на большом проекте вся пересборка — это он и есть. Замер на 500 нодах:
            // 34 мс на носитель без пачки против 9 мс с ней.
            //
            // Манифесты остаются снаружи: они сохраняются адресно, а адресное сохранение внутри
            // пачки не срабатывает — см. комментарий в SyncManifest.
            AssetDatabase.StartAssetEditing();
            try
            {
                SyncRefs(collector, nodes, ref report);
                SyncIds(ids, nodes, ref report);
            }
            finally
            {
                AssetDatabase.StopAssetEditing();
            }

            SyncManifests(collector, nodes, ref report);

            if (report.Changed)
            {
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();
            }

            return report;
        }

        /// <summary>
        /// Гейт пре-билда: пересобрать, потом пересобрать ещё раз и потребовать, чтобы второй заход
        /// не изменил ничего. Первый заход чинит протухший блоб, второй доказывает, что раскладка
        /// детерминирована — иначе в билд поехало бы то, что при следующей сборке будет другим.
        /// </summary>
        public static void RequireUpToDate(string what)
        {
            RebuildAll();

            var again = RebuildAll();
            if (again.Changed)
                throw new InvalidOperationException(
                    $"Blobcheg: {what} — пересборка не сошлась сама с собой ({again}). " +
                    "Раскладка обязана быть детерминированной; ехать с такой базой нельзя");
        }

        /// <summary>
        /// Состав домена ищется по проекту, а не берётся из ручного списка: список — это ещё одно
        /// место, где можно забыть.
        ///
        /// Обход идёт по самой базе ассетов, а НЕ через <c>AssetDatabase.FindAssets("t:...")</c>:
        /// поисковый индекс отстаёт от импорта (в батче свежесозданная нода в нём не находится
        /// вовсе), а пересборка, молча не нашедшая ноду, — это ровно тот случай, когда всё выглядит
        /// рабочим и врёт.
        /// </summary>
        public static List<BlobchegNodeSo> FindNodes()
        {
            var nodes = new List<BlobchegNodeSo>();

            foreach (var path in AssetDatabase.GetAllAssetPaths())
            {
                if (!path.EndsWith(".asset", StringComparison.OrdinalIgnoreCase))
                    continue;

                var type = AssetDatabase.GetMainAssetTypeAtPath(path);
                if (type == null || !typeof(BlobchegNodeSo).IsAssignableFrom(type))
                    continue;

                var node = AssetDatabase.LoadAssetAtPath<BlobchegNodeSo>(path);
                if (node != null)
                    nodes.Add(node);
            }

            return nodes.OrderBy(AssetDatabase.GetAssetPath, StringComparer.Ordinal).ToList();
        }

        static void SyncRefs(BlobchegCollector collector, List<BlobchegNodeSo> nodes, ref BlobchegBuildReport report)
        {
            var wanted = new HashSet<BlobchegRefSo>();

            foreach (var entry in collector.Entries)
            {
                var writer = collector.Writers[entry.Domain];
                var reference = Upsert(entry, writer, ref report);
                wanted.Add(reference);
            }

            // По всем нодам, а не только по писавшим: нода могла перестать писать вовсе, и её
            // ref-ассет обязан уехать вместе с записью.
            foreach (var node in nodes)
            {
                foreach (var stale in RefsOf(node).Where(r => !wanted.Contains(r)).ToList())
                {
                    AssetDatabase.RemoveObjectFromAsset(stale);
                    UnityEngine.Object.DestroyImmediate(stale, true);
                    report.RemovedRefs++;
                }
            }
        }

        static BlobchegRefSo Upsert(BlobchegEntry entry, BlobchegWriter writer, ref BlobchegBuildReport report)
        {
            var domainName = BlobchegDomains.NameOf(entry.Domain);
            var wantedName = entry.Node.name + "_" + domainName;
            var offset = writer.OffsetOf(entry.Ticket);
            var revision = unchecked((long)writer.RevisionOf(entry.Ticket));

            var reference = RefsOf(entry.Node).FirstOrDefault(r => r.domainName == domainName);
            if (reference == null)
            {
                reference = ScriptableObject.CreateInstance<BlobchegRefSo>();
                reference.name = wantedName;
                reference.domainName = domainName;
                AssetDatabase.AddObjectToAsset(reference, entry.Node);
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

            AssetDatabase.SetLabels(reference, LabelsFor(entry.RecordType));
            EditorUtility.SetDirty(reference);
            report.ChangedRefs++;
            return reference;
        }

        static string[] LabelsFor(string recordType)
        {
            if (string.IsNullOrEmpty(recordType))
                return new[] { "BlobchegRaw" };

            var dot = recordType.LastIndexOf('.');
            return new[] { dot < 0 ? recordType : recordType.Substring(dot + 1) };
        }

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
        static void BuildRouters(BlobchegCollector collector, BlobchegIdTable ids, ref BlobchegBuildReport report)
        {
            if (BlobchegRouters.All.Length == 0)
                return;

            var offsets = new Dictionary<(BlobchegNodeSo, Type), uint>();
            foreach (var entry in collector.Entries)
                offsets[(entry.Node, entry.Domain)] = collector.Writers[entry.Domain].OffsetOf(entry.Ticket);

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
                    for (var bit = 0; bit < domains.Length; bit++)
                    {
                        if (offsets.TryGetValue((node, domains[bit]), out var offset))
                            cells.Add(new BlobchegRouterCell(bit, offset));
                    }

                    writer.Append(node.name, cells);
                }

                writer.Flush(WithDebug);

                report.Routers++;
                if (writer.FileChanged)
                    report.ChangedFiles++;

                SyncRouterManifest(name, writer, members, ref report);
            }
        }

        static void SyncRouterManifest(string name, BlobchegRouterWriter writer,
            IReadOnlyList<BlobchegNodeSo> members, ref BlobchegBuildReport report)
        {
            // Порядок нод в манифесте — порядок id. Это и есть таблица «id → нода» для глаз.
            SyncManifest(name, true, members.ToArray(), writer.RowCount,
                writer.ContentHash, writer.FileChanged, ref report);
        }

        /// <summary>Носители id: по одному sub-ассету на пару (нода × роутер).</summary>
        static void SyncIds(BlobchegIdTable ids, List<BlobchegNodeSo> nodes, ref BlobchegBuildReport report)
        {
            var wanted = new HashSet<BlobchegIdSo>();

            foreach (var router in BlobchegRouters.All)
            {
                var name = BlobchegRouters.NameOf(router);
                var members = ids.NodesOf(router);

                foreach (var node in members)
                    wanted.Add(UpsertId(node, name, ids.Of(node, router), ref report));
            }

            foreach (var node in nodes)
            {
                foreach (var stale in IdsOf(node).Where(id => !wanted.Contains(id)).ToList())
                {
                    AssetDatabase.RemoveObjectFromAsset(stale);
                    UnityEngine.Object.DestroyImmediate(stale, true);
                    report.RemovedRefs++;
                }
            }
        }

        static BlobchegIdSo UpsertId(BlobchegNodeSo node, string routerName, BlobchegId id, ref BlobchegBuildReport report)
        {
            var wantedName = node.name + "_" + routerName;

            var carrier = IdsOf(node).FirstOrDefault(existing => existing.RouterName == routerName);
            if (carrier == null)
            {
                carrier = ScriptableObject.CreateInstance<BlobchegIdSo>();
                carrier.name = wantedName;
                carrier.routerName = routerName;
                AssetDatabase.AddObjectToAsset(carrier, node);
            }
            else if (carrier.id == id.Value && carrier.name == wantedName)
            {
                return carrier;
            }

            carrier.name = wantedName;
            carrier.routerName = routerName;
            carrier.id = id.Value;

            AssetDatabase.SetLabels(carrier, new[] { "BlobchegId", routerName });
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
            foreach (var pair in collector.Writers)
            {
                var domainName = BlobchegDomains.NameOf(pair.Key);
                var members = nodes.Where(n => Array.IndexOf(n.OutTypes, pair.Key) >= 0).ToArray();

                SyncManifest(domainName, false, members, pair.Value.RecordCount,
                    pair.Value.ContentHash, pair.Value.FileChanged, ref report);
            }
        }

        /// <summary>
        /// Манифест переписывается, если ХОТЬ ЧТО-ТО в нём разошлось с собранным — не только хеш.
        /// Иначе манифест, созданный в заход, где больше ничего не изменилось, так и остаётся на
        /// диске пустой заготовкой: <c>CreateAsset</c> пишет его до заполнения полей, а
        /// <c>SaveAssets</c> в таком заходе не зовётся вовсе.
        /// </summary>
        static void SyncManifest(string name, bool isRouter, BlobchegNodeSo[] members, int recordCount,
            ulong contentHash, bool fileChanged, ref BlobchegBuildReport report)
        {
            var fileName = BlobchegNaming.FileName(name);
            var manifest = LoadOrCreateManifest(name, out var created);

            var same = !created
                       && !fileChanged
                       && manifest.isRouter == isRouter
                       && manifest.domainName == name
                       && manifest.fileName == fileName
                       && manifest.recordCount == recordCount
                       && manifest.ContentHash == contentHash
                       && SameNodes(manifest.nodes, members);

            if (same)
                return;

            manifest.isRouter = isRouter;
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
