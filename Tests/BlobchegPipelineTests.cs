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
    /// <summary>Домен теста. Маркер-интерфейс и есть база: один домен — один файл.</summary>
    public interface ITestCombatData
    {
    }

    public struct TestPistol : ITestCombatData
    {
        public float AmmoMax;
        public int Rpm;
    }

    public struct TestArmor : ITestCombatData
    {
        public float Hp;
    }

    /// <summary>
    /// Объявление базы. Всё тело дописывает генератор — если он не отработал, тест не соберётся.
    /// Имя члена вступает в роутер, а сам роутер назван: в сборке тестов их два.
    /// </summary>
    [Blobcheg(typeof(ITestCombatData), "combat", Router = typeof(TestGameRouter))]
    public partial struct TestCombatDb
    {
    }

    public sealed class TestPistolNodeSo : BlobchegNodeSo
    {
        public float ammoMax = 30f;
        public int rpm = 600;

        public override Type[] OutTypes => new[] { typeof(ITestCombatData) };

        public override void Write(ref BlobchegNodeWriter writer)
            => writer.Add(new TestPistol { AmmoMax = ammoMax, Rpm = rpm });
    }

    public sealed class TestArmorNodeSo : BlobchegNodeSo
    {
        public float hp = 100f;

        public override Type[] OutTypes => new[] { typeof(ITestCombatData) };

        public override void Write(ref BlobchegNodeWriter writer)
            => writer.Add(new TestArmor { Hp = hp });
    }

    public struct TestLootTable : ITestCombatData
    {
        public int Rolls;
        public BlobchegArray<float> Weights;
    }

    public sealed class TestLootNodeSo : BlobchegNodeSo
    {
        public int rolls = 2;
        public float[] weights = { 0.5f, 0.3f, 0.2f };

        public override Type[] OutTypes => new[] { typeof(ITestCombatData) };

        public override void Write(ref BlobchegNodeWriter writer)
        {
            var b = writer.Begin<TestLootTable>();
            b.Root.Rolls = rolls;

            var w = b.Allocate(ref b.Root.Weights, weights.Length);
            for (var i = 0; i < w.Length; i++)
                w[i] = weights[i];

            b.End();
        }
    }

    /// <summary>Нода-ошибка: запись с массивом структ-литералом. Обязана быть отбита пересборкой.</summary>
    public sealed class TestLootLiteralNodeSo : BlobchegNodeSo
    {
        public override Type[] OutTypes => new[] { typeof(ITestCombatData) };

        public override void Write(ref BlobchegNodeWriter writer)
            => writer.Add(new TestLootTable { Rolls = 1 });
    }

    /// <summary>Нода-ошибка: открыла билдер и не позвала End.</summary>
    public sealed class TestLootUnclosedNodeSo : BlobchegNodeSo
    {
        public override Type[] OutTypes => new[] { typeof(ITestCombatData) };

        public override void Write(ref BlobchegNodeWriter writer)
        {
            var b = writer.Begin<TestLootTable>();
            b.Root.Rolls = 1;
        }
    }

    /// <summary>
    /// Сквозной путь: нода в едиторе → пересборка → файл → ref-ассет → чтение по оффсету.
    /// Кнопки Save в этом пути нет, поэтому пересборка зовётся напрямую — так же, как её зовут хуки.
    /// </summary>
    public sealed class BlobchegPipelineTests
    {
        // Папка своя на каждый тест: удаление ассетов отложенное, и переиспользованное имя
        // съедает ассет, созданный в ещё не удалённой папке. Ловится это не там, где сломано.
        string _folder;

        TestPistolNodeSo _pistol;
        TestArmorNodeSo _armor;

        [SetUp]
        public void SetUp()
        {
            var name = "BlobchegTestsTemp_" + Guid.NewGuid().ToString("N");
            _folder = "Assets/" + name;
            AssetDatabase.CreateFolder("Assets", name);

            _pistol = Create<TestPistolNodeSo>("Pistol");
            _armor = Create<TestArmorNodeSo>("Armor");
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

        [Test]
        public void Ноды_находятся_поиском_по_проекту()
        {
            var found = BlobchegBuild.FindNodes();
            CollectionAssert.Contains(found, _pistol);
            CollectionAssert.Contains(found, _armor);
        }

        static BlobchegRefSo RefOf(BlobchegNodeSo node)
            => BlobchegBuild.RefsOf(node).Single();

        [Test]
        public void Пересборка_кладёт_файл_ref_ассеты_и_читается_по_оффсету()
        {
            _pistol.ammoMax = 42f;
            _pistol.rpm = 900;
            EditorUtility.SetDirty(_pistol);

            var report = BlobchegBuild.RebuildAll();
            Assert.That(report.Records, Is.GreaterThanOrEqualTo(2));

            var file = Path.Combine(BlobchegBuild.OutputDirectory, TestCombatDb.FileName);
            Assert.That(File.Exists(file), Is.True, "файл базы должен лечь в StreamingAssets");

            var pistolRef = RefOf(_pistol);
            Assert.That(pistolRef.RecordType, Is.EqualTo(typeof(TestPistol).FullName));
            Assert.That(pistolRef.DomainName, Is.EqualTo("ITestCombatData"));

            var db = new TestCombatDb(BlobchegBuffer.From(File.ReadAllBytes(file), Allocator.Temp));
            try
            {
                ref readonly var pistol = ref db.Read<TestPistol>(pistolRef.offset);
                Assert.That(pistol.AmmoMax, Is.EqualTo(42f));
                Assert.That(pistol.Rpm, Is.EqualTo(900));

                ref readonly var armor = ref db.Read<TestArmor>(RefOf(_armor).offset);
                Assert.That(armor.Hp, Is.EqualTo(100f));
            }
            finally
            {
                db.Dispose();
            }
        }

        [Test]
        public void Пересборка_идемпотентна()
        {
            BlobchegBuild.RebuildAll();

            var again = BlobchegBuild.RebuildAll();
            Assert.That(again.Changed, Is.False,
                "ничего не изменилось — не должен быть тронут ни файл, ни один ассет, иначе всё перепечётся. " +
                $"Отчёт: {again}");
        }

        [Test]
        public void Правка_значения_не_двигает_оффсет()
        {
            BlobchegBuild.RebuildAll();
            var before = RefOf(_pistol).offset;

            _pistol.ammoMax = 7f;
            EditorUtility.SetDirty(_pistol);
            BlobchegBuild.RebuildAll();

            Assert.That(RefOf(_pistol).offset, Is.EqualTo(before));

            var file = Path.Combine(BlobchegBuild.OutputDirectory, TestCombatDb.FileName);
            var db = new TestCombatDb(BlobchegBuffer.From(File.ReadAllBytes(file), Allocator.Temp));
            try
            {
                Assert.That(db.Read<TestPistol>(before).AmmoMax, Is.EqualTo(7f), "значение при этом обязано поменяться");
            }
            finally
            {
                db.Dispose();
            }
        }

        static byte[] DomainFile()
            => File.ReadAllBytes(Path.Combine(BlobchegBuild.OutputDirectory, TestCombatDb.FileName));

        /// <summary>
        /// Главное свойство кеша: собранное из памяти обязано совпасть с собранным из ассетов.
        /// Полный заход, не изменивший ничего, — и есть доказательство совпадения.
        /// </summary>
        [Test]
        public void Инкрементальная_пересборка_совпадает_с_полной()
        {
            BlobchegBuild.RebuildAll();

            _pistol.ammoMax = 3f;
            EditorUtility.SetDirty(_pistol);
            BlobchegBuild.RebuildAll();

            var incremental = DomainFile();
            var full = BlobchegBuild.RebuildFull();

            Assert.That(full.Changed, Is.False,
                $"полный заход после инкрементального обязан не найти расхождений. Отчёт: {full}");
            CollectionAssert.AreEqual(incremental, DomainFile());
        }

        [Test]
        public void Новая_нода_попадает_в_инкрементальную_пересборку()
        {
            BlobchegBuild.RebuildAll();

            var extra = Create<TestArmorNodeSo>("Extra");
            AssetDatabase.SaveAssets();
            BlobchegBuild.RebuildAll();

            Assert.That(BlobchegBuild.RefsOf(extra).Any(), Is.True, "нода, созданная после сборки, обязана в неё попасть");

            var incremental = DomainFile();
            Assert.That(BlobchegBuild.RebuildFull().Changed, Is.False);
            CollectionAssert.AreEqual(incremental, DomainFile());
        }

        [Test]
        public void Удалённая_нода_уходит_из_инкрементальной_пересборки()
        {
            var extra = Create<TestArmorNodeSo>("Extra");
            AssetDatabase.SaveAssets();
            BlobchegBuild.RebuildAll();

            var records = BlobchegBuild.RebuildAll().Records;

            AssetDatabase.DeleteAsset(AssetDatabase.GetAssetPath(extra));
            var after = BlobchegBuild.RebuildAll();

            Assert.That(after.Records, Is.EqualTo(records - 1), "запись удалённой ноды обязана уйти из базы");

            var incremental = DomainFile();
            Assert.That(BlobchegBuild.RebuildFull().Changed, Is.False);
            CollectionAssert.AreEqual(incremental, DomainFile());
        }

        [Test]
        public void Типизированное_поле_не_принимает_чужую_запись()
        {
            BlobchegBuild.RebuildAll();

            var field = new BlobchegRef<TestPistol>(RefOf(_armor));
            var thrown = Assert.Throws<InvalidOperationException>(() => _ = field.Offset);
            StringAssert.Contains("TestArmor", thrown.Message);

            Assert.That(new BlobchegRef<TestPistol>(RefOf(_pistol)).Offset, Is.EqualTo(RefOf(_pistol).offset));
        }

        [Test]
        public void Пикер_показывает_только_записи_своего_типа()
        {
            BlobchegBuild.RebuildAll();

            var pistols = BlobchegRefCatalog.Candidates(typeof(TestPistol));
            CollectionAssert.Contains(pistols, RefOf(_pistol));
            CollectionAssert.DoesNotContain(pistols, RefOf(_armor));

            var armors = BlobchegRefCatalog.Candidates(typeof(TestArmor));
            CollectionAssert.Contains(armors, RefOf(_armor));
            CollectionAssert.DoesNotContain(armors, RefOf(_pistol));

            var raw = BlobchegRefCatalog.Candidates(null);
            CollectionAssert.IsSupersetOf(raw, new[] { RefOf(_pistol), RefOf(_armor) });
        }

        [Test]
        public void Каталог_отбивает_чужую_запись()
        {
            BlobchegBuild.RebuildAll();

            Assert.That(BlobchegRefCatalog.Matches(RefOf(_armor), typeof(TestPistol)), Is.False);
            Assert.That(BlobchegRefCatalog.Matches(RefOf(_pistol), typeof(TestPistol)), Is.True);
            Assert.That(BlobchegRefCatalog.Matches(null, typeof(TestPistol)), Is.False);
        }

        [Test]
        public void Пустое_поле_бросает_а_не_отдаёт_ноль()
        {
            var empty = new BlobchegRef<TestPistol>(null);
            Assert.That(empty.IsSet, Is.False);
            Assert.Throws<InvalidOperationException>(() => _ = empty.Offset);
        }

        [Test]
        public void Нода_с_массивом_пишется_и_читается_через_пересборку()
        {
            var loot = Create<TestLootNodeSo>("Loot");
            AssetDatabase.SaveAssets();
            BlobchegBuild.RebuildAll();

            var file = Path.Combine(BlobchegBuild.OutputDirectory, TestCombatDb.FileName);
            var db = new TestCombatDb(BlobchegBuffer.From(File.ReadAllBytes(file), Allocator.Temp));
            try
            {
                ref readonly var table = ref db.Read<TestLootTable>(RefOf(loot).offset);
                Assert.That(table.Rolls, Is.EqualTo(2));
                Assert.That(table.Weights.Length, Is.EqualTo(3));
                Assert.That(table.Weights[0], Is.EqualTo(0.5f));
                Assert.That(table.Weights[1], Is.EqualTo(0.3f));
                Assert.That(table.Weights[2], Is.EqualTo(0.2f));
            }
            finally
            {
                db.Dispose();
            }
        }

        [Test]
        public void Правка_длины_массива_не_двигает_чужие_адреса()
        {
            var loot = Create<TestLootNodeSo>("Loot");
            AssetDatabase.SaveAssets();
            BlobchegBuild.RebuildAll();
            var pistolBefore = RefOf(_pistol).offset;
            var armorBefore = RefOf(_armor).offset;

            loot.weights = new[] { 0.3f, 0.25f, 0.2f, 0.15f, 0.06f, 0.04f };
            EditorUtility.SetDirty(loot);
            BlobchegBuild.RebuildAll();

            Assert.That(RefOf(_pistol).offset, Is.EqualTo(pistolBefore), "выросший массив двигает только свою запись");
            Assert.That(RefOf(_armor).offset, Is.EqualTo(armorBefore));

            var file = Path.Combine(BlobchegBuild.OutputDirectory, TestCombatDb.FileName);
            var db = new TestCombatDb(BlobchegBuffer.From(File.ReadAllBytes(file), Allocator.Temp));
            try
            {
                Assert.That(db.Read<TestLootTable>(RefOf(loot).offset).Weights.Length, Is.EqualTo(6));
            }
            finally
            {
                db.Dispose();
            }
        }

        [Test]
        public void Литерал_с_массивом_отбивается()
        {
            Create<TestLootLiteralNodeSo>("LootLiteral");
            AssetDatabase.SaveAssets();

            var thrown = Assert.Throws<InvalidOperationException>(() => BlobchegBuild.RebuildAll());
            StringAssert.Contains("Begin", thrown.Message, "ошибка обязана назвать правильную форму записи");
        }

        [Test]
        public void Begin_без_End_падает_с_именем_ноды()
        {
            Create<TestLootUnclosedNodeSo>("LootUnclosed");
            AssetDatabase.SaveAssets();

            var thrown = Assert.Throws<InvalidOperationException>(() => BlobchegBuild.RebuildAll());
            StringAssert.Contains("LootUnclosed", thrown.Message);
            StringAssert.Contains("End", thrown.Message);
        }

        [Test]
        public void Манифест_домена_держит_тот_же_хеш_что_файл()
        {
            BlobchegBuild.RebuildAll();

            var manifest = AssetDatabase.LoadAssetAtPath<BlobchegDomainSo>(
                BlobchegBuild.ManifestFolder + "/ITestCombatData.asset");
            Assert.That(manifest, Is.Not.Null);

            var file = File.ReadAllBytes(Path.Combine(BlobchegBuild.OutputDirectory, TestCombatDb.FileName));
            var inHeader = BitConverter.ToUInt64(file, 16);
            Assert.That(manifest.ContentHash, Is.EqualTo(inHeader));
        }
    }
}
