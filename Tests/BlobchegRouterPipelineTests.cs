using System;
using System.IO;
using System.Linq;
using Blobcheg.Authoring;
using NUnit.Framework;
using Unity.Collections;
using UnityEditor;
using UnityEngine;

namespace Blobcheg.Tests
{
    /// <summary>Второй домен теста — нужен, чтобы у роутера было больше одного бита.</summary>
    public interface ITestColdData
    {
    }

    public struct TestColdInfo : ITestColdData
    {
        /// <summary>Свой id, положенный нодой прямо в запись: доказательство, что он известен до записи.</summary>
        public uint SelfId;

        public int Tier;
    }

    [Blobcheg(typeof(ITestColdData), "cold", Router = typeof(TestGameRouter))]
    public partial struct TestColdDb
    {
    }

    /// <summary>
    /// Обычный роутер теста: номера строк раздаёт он сам. Базы называют его явно — рядом живёт
    /// второй, детерминированный (<c>TestFixedRouter</c>), и выбирать за них некому.
    /// </summary>
    [BlobchegRouter]
    public partial struct TestGameRouter
    {
    }

    /// <summary>Нода в обеих базах: строка роутера с двумя битами.</summary>
    public sealed class TestModuleNodeSo : BlobchegNodeSo
    {
        public int tier = 3;

        public override Type[] OutTypes => new[] { typeof(ITestCombatData), typeof(ITestColdData) };

        public override void Write(ref BlobchegNodeWriter writer)
        {
            writer.Add(new TestPistol { AmmoMax = 11f, Rpm = 111 });
            writer.Add(new TestColdInfo { SelfId = writer.Id.Value, Tier = tier });
        }
    }

    /// <summary>Нода только в холодной базе: строка с одним битом, дырка на месте второго.</summary>
    public sealed class TestColdOnlyNodeSo : BlobchegNodeSo
    {
        public int tier = 9;

        public override Type[] OutTypes => new[] { typeof(ITestColdData) };

        public override void Write(ref BlobchegNodeWriter writer)
            => writer.Add(new TestColdInfo { SelfId = writer.Id.Value, Tier = tier });
    }

    /// <summary>Файлы и манифесты, которые пересборка кладёт из-за тестовых доменов.</summary>
    static class BlobchegTestArtifacts
    {
        static readonly string[] Names =
        {
            "ITestCombatData", "ITestColdData", "ITestBootData", "TestGameRouter",
            "ITestGridData", "TestFixedRouter",
        };

        public static void Wipe()
        {
            foreach (var name in Names)
            {
                AssetDatabase.DeleteAsset(BlobchegBuild.ManifestFolder + "/" + name + ".asset");

                var file = Path.Combine(BlobchegBuild.OutputDirectory, BlobchegNaming.FileName(name));
                if (File.Exists(file))
                    File.Delete(file);
            }

            AssetDatabase.Refresh();
        }
    }

    /// <summary>
    /// Сквозной путь роутера: ноды в едиторе → пересборка → файл роутера → носитель id → лукап
    /// оффсетов во всех базах сразу.
    /// </summary>
    public sealed class BlobchegRouterPipelineTests
    {
        string _folder;
        TestModuleNodeSo _module;
        TestColdOnlyNodeSo _cold;

        [SetUp]
        public void SetUp()
        {
            // Папка своя на каждый тест: удаление ассетов отложенное, и переиспользованное имя
            // съедает ассет, созданный в ещё не удалённой папке.
            var name = "BlobchegRouterTemp_" + Guid.NewGuid().ToString("N");
            _folder = "Assets/" + name;
            AssetDatabase.CreateFolder("Assets", name);

            _module = Create<TestModuleNodeSo>("Module");
            _cold = Create<TestColdOnlyNodeSo>("ColdOnly");
            AssetDatabase.SaveAssets();
        }

        [TearDown]
        public void TearDown()
        {
            AssetDatabase.DeleteAsset(_folder);
            BlobchegTestArtifacts.Wipe();
        }

        T Create<T>(string name) where T : ScriptableObject
        {
            var path = _folder + "/" + name + ".asset";
            AssetDatabase.CreateAsset(ScriptableObject.CreateInstance<T>(), path);

            var asset = AssetDatabase.LoadAssetAtPath<T>(path);
            Assert.That(asset, Is.Not.Null, $"ассет '{path}' не создался — дальше проверять нечего");
            return asset;
        }

        static BlobchegId IdOf(BlobchegNodeSo node)
        {
            var carrier = BlobchegBuild.IdsOf(node).Single();
            Assert.That(carrier.RouterName, Is.EqualTo(TestGameRouter.RouterName));
            return new BlobchegIdRef<TestGameRouter>(carrier).Id;
        }

        static TestGameRouter LoadRouter()
        {
            var path = Path.Combine(BlobchegBuild.OutputDirectory, TestGameRouter.FileName);
            Assert.That(File.Exists(path), Is.True, "файл роутера должен лечь в StreamingAssets");
            return new TestGameRouter(BlobchegBuffer.From(File.ReadAllBytes(path), Allocator.Persistent));
        }

        static TestColdDb LoadCold()
            => new TestColdDb(BlobchegBuffer.From(
                File.ReadAllBytes(Path.Combine(BlobchegBuild.OutputDirectory, TestColdDb.FileName)), Allocator.Persistent));

        static TestCombatDb LoadCombat()
            => new TestCombatDb(BlobchegBuffer.From(
                File.ReadAllBytes(Path.Combine(BlobchegBuild.OutputDirectory, TestCombatDb.FileName)), Allocator.Persistent));

        [Test]
        public void Кодоген_и_реестр_сошлись_на_нумерации_бит()
        {
            Assert.That(TestGameRouter.DomainCount, Is.EqualTo(2));
            Assert.That(TestGameRouter.RouterName, Is.EqualTo("TestGameRouter"));
            Assert.That(BlobchegRouters.LayoutHashOf(typeof(TestGameRouter)), Is.EqualTo(TestGameRouter.LayoutHash));
            Assert.DoesNotThrow(() => BlobchegRouters.RequireCodeGenAgrees(typeof(TestGameRouter)));

            var domains = BlobchegRouters.DomainsOf(typeof(TestGameRouter));
            CollectionAssert.AreEqual(new[] { typeof(ITestColdData), typeof(ITestCombatData) }, domains,
                "биты нумеруются доменами по FullName ordinal");
        }

        [Test]
        public void По_одному_id_достаются_записи_обеих_баз()
        {
            _module.tier = 42;
            EditorUtility.SetDirty(_module);

            var report = BlobchegBuild.RebuildAll();
            Assert.That(report.Routers, Is.GreaterThanOrEqualTo(1));

            var id = IdOf(_module);
            var router = LoadRouter();
            var cold = LoadCold();
            var combat = LoadCombat();

            try
            {
                var row = router.Get(id);
                Assert.That(row.HasCold, Is.True);
                Assert.That(row.HasCombat, Is.True);

                ref readonly var record = ref cold.Read<TestColdInfo>(row.cold);
                Assert.That(record.Tier, Is.EqualTo(42));
                Assert.That(record.SelfId, Is.EqualTo(id.Value), "нода положила свой id в запись — он известен до записи");

                ref readonly var pistol = ref combat.Read<TestPistol>(row.combat);
                Assert.That(pistol.Rpm, Is.EqualTo(111));

                Assert.That(router.GetCold(id), Is.EqualTo(row.cold), "короткий путь и строка дают одно и то же");
                Assert.That(router.TryGetCombat(id, out var offset), Is.True);
                Assert.That(offset, Is.EqualTo(row.combat));
            }
            finally
            {
                router.Dispose();
                cold.Dispose();
                combat.Dispose();
            }
        }

        [Test]
        public void Нода_вне_базы_бросает_а_Try_отвечает_false()
        {
            BlobchegBuild.RebuildAll();

            var id = IdOf(_cold);
            var router = LoadRouter();
            try
            {
                var row = router.Get(id);
                Assert.That(row.HasCold, Is.True);
                Assert.That(row.HasCombat, Is.False, "эта нода в боевую базу не писала");

                Assert.Throws<InvalidOperationException>(() => _ = row.combat);
                Assert.Throws<InvalidOperationException>(() => router.GetCombat(id));

                Assert.That(router.TryGetCombat(id, out _), Is.False);
                Assert.That(router.HasCombat(id), Is.False);
                Assert.That(router.HasCold(id), Is.True);
            }
            finally
            {
                router.Dispose();
            }
        }

        [Test]
        public void Неизвестный_id_бросает()
        {
            BlobchegBuild.RebuildAll();

            var router = LoadRouter();
            try
            {
                var beyond = BlobchegId.In(TestGameRouter.RouterName, (uint)router.Count);
                Assert.Throws<InvalidOperationException>(() => router.Get(beyond));
                Assert.Throws<InvalidOperationException>(() => router.Get(BlobchegId.None));
                Assert.That(router.TryGet(beyond, out _), Is.False);

                // Тег заведомо не этого роутера: имя чужого роутера могло бы совпасть тегом.
                var alienTag = (byte)(BlobchegNaming.TagOf(TestGameRouter.RouterName) % 255 + 1);
                var alien = BlobchegId.Make(alienTag, 0);
                Assert.Throws<InvalidOperationException>(() => router.Get(alien),
                    "строка ноль в этом роутере есть, но id выдан не им");
                Assert.That(router.TryGet(alien, out _), Is.False);
            }
            finally
            {
                router.Dispose();
            }
        }

        [Test]
        public void Id_не_двигается_от_правки_значения()
        {
            BlobchegBuild.RebuildAll();
            var before = IdOf(_module);

            _module.tier = 7;
            EditorUtility.SetDirty(_module);
            BlobchegBuild.RebuildAll();

            Assert.That(IdOf(_module), Is.EqualTo(before));

            var cold = LoadCold();
            var router = LoadRouter();
            try
            {
                Assert.That(cold.Read<TestColdInfo>(router.Get(before).cold).Tier, Is.EqualTo(7),
                    "значение при этом обязано поменяться");
            }
            finally
            {
                cold.Dispose();
                router.Dispose();
            }
        }

        [Test]
        public void Новая_нода_не_двигает_ни_чужой_id_ни_чужой_оффсет()
        {
            BlobchegBuild.RebuildAll();

            var id = IdOf(_module);
            var offset = BlobchegBuild.RefsOf(_module)
                .Single(r => r.DomainName == "ITestColdData").offset;

            // GUID у новой ноды случаен, поэтому в раскладке по GUID она садится где угодно — в том
            // числе перед уже существующими.
            Create<TestColdOnlyNodeSo>("Newcomer");
            AssetDatabase.SaveAssets();
            BlobchegBuild.RebuildAll();

            Assert.That(IdOf(_module), Is.EqualTo(id), "id соседа обязан пережить появление новой ноды");
            Assert.That(BlobchegBuild.RefsOf(_module).Single(r => r.DomainName == "ITestColdData").offset,
                Is.EqualTo(offset), "оффсет соседа обязан пережить появление новой ноды");
        }

        [Test]
        public void Удалённая_нода_оставляет_дырку_а_чужой_id_остаётся()
        {
            BlobchegBuild.RebuildAll();

            // Дырку оставляет только тот, у кого id не последний, а раздаются они по GUID —
            // поэтому кого убить, решает замер, а не порядок создания в тесте.
            var first = IdOf(_module).Index < IdOf(_cold).Index;
            var victim = first ? (BlobchegNodeSo)_module : _cold;
            var survivor = first ? (BlobchegNodeSo)_cold : _module;

            var keep = IdOf(survivor);
            var killed = IdOf(victim);
            Assert.That(killed.Index, Is.LessThan(keep.Index));

            var rows = LoadRouterRowCount();

            AssetDatabase.DeleteAsset(AssetDatabase.GetAssetPath(victim));
            BlobchegBuild.RebuildAll();

            Assert.That(IdOf(survivor), Is.EqualTo(keep), "чужой id не съезжает следом за удалённым");
            Assert.That(LoadRouterRowCount(), Is.EqualTo(rows),
                "строка удалённой ноды остаётся дыркой: подтянуть следующую — значит сдвинуть её id");

            var router = LoadRouter();
            try
            {
                Assert.That(router.Get(keep).HasCold, Is.True, "оставшаяся нода по своему id читается как читалась");
                Assert.That(router.Get(killed).HasCold, Is.False, "дырка пуста, а не показывает на соседа");
            }
            finally
            {
                router.Dispose();
            }
        }

        static int LoadRouterRowCount()
        {
            var router = LoadRouter();
            try
            {
                return (int)router.Count;
            }
            finally
            {
                router.Dispose();
            }
        }

        [Test]
        public void Компакт_убирает_дырку_и_раздаёт_адреса_заново()
        {
            BlobchegBuild.RebuildAll();

            var first = IdOf(_module).Index < IdOf(_cold).Index;
            var victim = first ? (BlobchegNodeSo)_module : _cold;
            var survivor = first ? (BlobchegNodeSo)_cold : _module;

            var before = IdOf(survivor);
            AssetDatabase.DeleteAsset(AssetDatabase.GetAssetPath(victim));
            BlobchegBuild.RebuildAll();

            Assert.That(LoadRouterRowCount(), Is.EqualTo(2), "дырка на месте: обычная пересборка её не трогает");
            Assert.That(IdOf(survivor), Is.EqualTo(before));

            BlobchegBuild.Compact();

            Assert.That(LoadRouterRowCount(), Is.EqualTo(1), "компакт обязан убрать пустую строку");
            Assert.That(IdOf(survivor).Index, Is.EqualTo(0u), "и раздать id заново подряд");

            var router = LoadRouter();
            try
            {
                Assert.That(router.Get(IdOf(survivor)).HasCold, Is.True, "нода по новому id читается");
            }
            finally
            {
                router.Dispose();
            }

            Assert.That(BlobchegBuild.RebuildFull().Changed, Is.False,
                "после компакта раскладка обязана сойтись сама с собой");
        }

        [Test]
        public void Пересборка_с_роутером_идемпотентна()
        {
            BlobchegBuild.RebuildAll();

            var again = BlobchegBuild.RebuildAll();
            Assert.That(again.Changed, Is.False,
                "ничего не изменилось — не должен быть тронут ни файл роутера, ни один носитель id");
        }

        [Test]
        public void Поле_id_отбивает_пустоту_и_чужой_роутер()
        {
            BlobchegBuild.RebuildAll();

            var empty = new BlobchegIdRef<TestGameRouter>(null);
            Assert.That(empty.IsSet, Is.False);
            Assert.Throws<InvalidOperationException>(() => _ = empty.Id);

            var alien = ScriptableObject.CreateInstance<BlobchegIdSo>();
            try
            {
                alien.name = "Чужой";
                alien.id = 0;
                var thrown = Assert.Throws<InvalidOperationException>(
                    () => _ = new BlobchegIdRef<TestGameRouter>(alien).Id);

                StringAssert.Contains("TestGameRouter", thrown.Message);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(alien);
            }
        }

        [Test]
        public void Пикер_показывает_только_ноды_своего_роутера()
        {
            BlobchegBuild.RebuildAll();

            var mine = BlobchegIdCatalog.Candidates(TestGameRouter.RouterName);
            CollectionAssert.Contains(mine, BlobchegBuild.IdsOf(_module).Single());
            CollectionAssert.Contains(mine, BlobchegBuild.IdsOf(_cold).Single());

            Assert.That(BlobchegIdCatalog.Candidates("ЧужойРоутер"), Is.Empty);
            Assert.That(BlobchegIdCatalog.RouterNameOf(typeof(TestGameRouter)), Is.EqualTo("TestGameRouter"));
        }

        [Test]
        public void Манифест_роутера_держит_хеш_файла_и_порядок_id()
        {
            var report = BlobchegBuild.RebuildAll();

            var manifest = AssetDatabase.LoadAssetAtPath<BlobchegDomainSo>(
                BlobchegBuild.ManifestFolder + "/TestGameRouter.asset");

            Assert.That(manifest, Is.Not.Null);
            Assert.That(manifest.IsRouter, Is.True,
                $"отчёт: {report}; путь: {AssetDatabase.GetAssetPath(manifest)}; id: {manifest.GetInstanceID()}; " +
                $"domainName: '{manifest.domainName}'; recordCount: {manifest.recordCount}; " +
                $"hash: {manifest.ContentHash:X16}; nodes: {manifest.nodes?.Length}");

            var file = File.ReadAllBytes(Path.Combine(BlobchegBuild.OutputDirectory, TestGameRouter.FileName));
            Assert.That(manifest.ContentHash, Is.EqualTo(BitConverter.ToUInt64(file, 16)));

            // Ноды в манифесте лежат в порядке id — это и есть таблица «id → нода» для глаз.
            for (var i = 0; i < manifest.nodes.Length; i++)
            {
                var carrier = BlobchegBuild.IdsOf(manifest.nodes[i]).Single();
                Assert.That(new BlobchegId(carrier.id).Index, Is.EqualTo((uint)i));
            }
        }
    }
}
