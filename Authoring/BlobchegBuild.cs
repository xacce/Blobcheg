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
        public int Records;
        public int ChangedFiles;
        public int ChangedRefs;
        public int RemovedRefs;

        public bool Changed => ChangedFiles > 0 || ChangedRefs > 0 || RemovedRefs > 0;

        public override string ToString()
            => $"домены {Domains}, записи {Records}, переписано файлов {ChangedFiles}, " +
               $"обновлено ref'ов {ChangedRefs}, удалено {RemovedRefs}";
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

            // Писатель открывается на КАЖДЫЙ объявленный домен, даже пустой: иначе домен, из
            // которого удалили последнюю ноду, остался бы на диске старым файлом.
            foreach (var domain in BlobchegDomains.All)
                collector.WriterOf(domain);

            foreach (var node in nodes)
            {
                var writer = new BlobchegNodeWriter { Collector = collector, Node = node };
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

            SyncRefs(collector, nodes, ref report);
            SyncManifests(collector, nodes);

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

        public static List<BlobchegNodeSo> FindNodes()
        {
            return AssetDatabase.FindAssets("t:" + nameof(BlobchegNodeSo))
                .OrderBy(guid => guid, StringComparer.Ordinal)
                .Select(AssetDatabase.GUIDToAssetPath)
                .Select(AssetDatabase.LoadAssetAtPath<BlobchegNodeSo>)
                .Where(node => node != null)
                .ToList();
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

        internal static IEnumerable<BlobchegRefSo> RefsOf(BlobchegNodeSo node)
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

        static void SyncManifests(BlobchegCollector collector, List<BlobchegNodeSo> nodes)
        {
            foreach (var pair in collector.Writers)
            {
                var domainName = BlobchegDomains.NameOf(pair.Key);
                var manifest = LoadOrCreateManifest(domainName);

                manifest.domainName = domainName;
                manifest.fileName = BlobchegNaming.FileName(domainName);
                manifest.recordCount = pair.Value.RecordCount;
                manifest.nodes = nodes.Where(n => Array.IndexOf(n.OutTypes, pair.Key) >= 0).ToArray();

                if (manifest.ContentHash == pair.Value.ContentHash && !pair.Value.FileChanged)
                    continue;

                manifest.ContentHash = pair.Value.ContentHash;
                manifest.builtAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                EditorUtility.SetDirty(manifest);
            }
        }

        static BlobchegDomainSo LoadOrCreateManifest(string domainName)
        {
            var path = ManifestFolder + "/" + domainName + ".asset";
            var manifest = AssetDatabase.LoadAssetAtPath<BlobchegDomainSo>(path);
            if (manifest != null)
                return manifest;

            Directory.CreateDirectory(ManifestFolder);
            manifest = ScriptableObject.CreateInstance<BlobchegDomainSo>();
            AssetDatabase.CreateAsset(manifest, path);
            return manifest;
        }
    }
}
