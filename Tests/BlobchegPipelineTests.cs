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
    /// Имя члена вступает в роутер: роутер в сборке один, поэтому называть его не нужно.
    /// </summary>
    [Blobcheg(typeof(ITestCombatData), "combat")]
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
