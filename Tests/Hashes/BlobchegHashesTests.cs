using System;
using System.IO;
using System.Linq;
using Blobcheg.Authoring;
using NUnit.Framework;
using Unity.Collections;
using UnityEditor;
using UnityEngine;

namespace Blobcheg.HashTests
{
    /// <summary>
    /// Сквозной путь таблицы: имя ноды → ключ → файл → лукап. И главное свойство, ради которого всё
    /// заведено: адреса уезжают, хеш остаётся.
    /// </summary>
    public sealed class BlobchegHashesTests
    {
        static readonly string[] Artifacts =
        {
            "ITestHashHot", "ITestHashCold", "TestHashRouter", "TestHashRouterHashes",
        };

        string _folder;
        TestHashNodeSo _gun;
        TestHashNodeSo _twin;
        TestHashColdOnlyNodeSo _cold;

        [SetUp]
        public void SetUp()
        {
            var name = "BlobchegHashTemp_" + Guid.NewGuid().ToString("N");
            _folder = "Assets/" + name;
            AssetDatabase.CreateFolder("Assets", name);

            _gun = Create<TestHashNodeSo>("Gun");
            _twin = Create<TestHashNodeSo>("Twin");
            _cold = Create<TestHashColdOnlyNodeSo>("ColdOnly");

            _gun.twin = _twin;
            EditorUtility.SetDirty(_gun);
            AssetDatabase.SaveAssets();
        }

        [TearDown]
        public void TearDown()
        {
            AssetDatabase.DeleteAsset(_folder);

            foreach (var artifact in Artifacts)
            {
                AssetDatabase.DeleteAsset(BlobchegBuild.ManifestFolder + "/" + artifact + ".asset");

                var file = Path.Combine(BlobchegBuild.OutputDirectory, BlobchegNaming.FileName(artifact));
                if (File.Exists(file))
                    File.Delete(file);
            }

            AssetDatabase.Refresh();
        }

        T Create<T>(string name) where T : ScriptableObject
        {
            var path = _folder + "/" + name + ".asset";
            AssetDatabase.CreateAsset(ScriptableObject.CreateInstance<T>(), path);

            var asset = AssetDatabase.LoadAssetAtPath<T>(path);
            Assert.That(asset, Is.Not.Null, $"ассет '{path}' не создался — дальше проверять нечего");
            return asset;
        }

        static void Rename(BlobchegNodeSo node, string name)
        {
            var serialized = new SerializedObject(node);
            serialized.FindProperty("blobchegName").stringValue = name;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(node);
        }

        static string PathOf(string identity)
            => Path.Combine(BlobchegBuild.OutputDirectory, BlobchegNaming.FileName(identity));

        static BlobchegBuffer Read(string identity)
        {
            var path = PathOf(identity);
            Assert.That(File.Exists(path), Is.True, $"файл '{path}' должен лечь в StreamingAssets");
            return BlobchegBuffer.From(File.ReadAllBytes(path), Allocator.Persistent);
        }

        static TestHashTable LoadTable() => new TestHashTable(Read(TestHashTable.FileIdentity));

        static TestHashRouter LoadRouter() => new TestHashRouter(Read(TestHashRouter.RouterName));

        static TestHashHotDb LoadHot() => new TestHashHotDb(Read(TestHashHotDb.DomainName));

        static TestHashColdDb LoadCold() => new TestHashColdDb(Read(TestHashColdDb.DomainName));

        static BlobchegId IdOf(BlobchegNodeSo node)
        {
            var carrier = BlobchegBuild.IdsOf(node).Single(c => c.RouterName == TestHashRouter.RouterName);
            return new BlobchegId(carrier.id);
        }

        static uint OffsetOf(BlobchegNodeSo node, string domainName)
            => BlobchegBuild.RefsOf(node).Single(r => r.DomainName == domainName).offset;

        [Test]
        public void Ключ_считается_от_имени_роутера_и_имени_ноды()
        {
            var direct = BlobchegHashKey.Of("TestHashRouter", "ak74");
            Assert.That(BlobchegHashKey.Of<TestHashRouter>("ak74"), Is.EqualTo(direct),
                "имя роутера берётся у параметра типа, а не пишется руками");

            Assert.That(BlobchegHashKey.Of("TestHashRouter", "ak74m"), Is.Not.EqualTo(direct));
            Assert.That(BlobchegHashKey.Of("ДругойРоутер", "ak74"), Is.Not.EqualTo(direct),
                "роутер — часть ключа: без него одно имя в двух роутерах дало бы один хеш");

            Assert.That(direct, Is.Not.EqualTo(0ul), "ноль занят под «не назначен»");

            Assert.Throws<ArgumentException>(() => BlobchegHashKey.Of("TestHashRouter", ""));
            Assert.Throws<ArgumentException>(() => BlobchegHashKey.Of("", "ak74"));
        }

        [Test]
        public void Пустое_имя_заполняется_один_раз_именем_ассета()
        {
            Assert.That(_gun.BlobchegName, Is.Null.Or.Empty, "до пересборки имени нет");

            var first = BlobchegBuild.RebuildAll();
            Assert.That(first.NamedNodes, Is.GreaterThanOrEqualTo(3), "имена проставились в первый заход");
            Assert.That(_gun.BlobchegName, Is.EqualTo("Gun"));
            Assert.That(_cold.BlobchegName, Is.EqualTo("ColdOnly"));

            var again = BlobchegBuild.RebuildAll();
            Assert.That(again.NamedNodes, Is.EqualTo(0), "второй заход имена не трогает");
            Assert.That(again.Changed, Is.False, "пересборка с таблицей обязана быть идемпотентной");
        }

        [Test]
        public void Хеш_разворачивается_в_id_и_обратно()
        {
            BlobchegBuild.RebuildAll();

            var table = LoadTable();
            try
            {
                Assert.That(table.Count, Is.EqualTo(3));
                Assert.That(table.Tag, Is.EqualTo(BlobchegNaming.TagOf(TestHashRouter.RouterName)));

                foreach (var node in new BlobchegNodeSo[] { _gun, _twin, _cold })
                {
                    var id = IdOf(node);
                    var hash = BlobchegHashKey.Of<TestHashRouter>(node.BlobchegName);

                    Assert.That(table.TryGetId(hash, out var found), Is.True, $"нода '{node.name}' не нашлась по хешу");
                    Assert.That(found, Is.EqualTo(id));
                    Assert.That(table.GetId(hash), Is.EqualTo(id));
                    Assert.That(table.HashOf(id), Is.EqualTo(hash), "обратный путь обязан давать тот же хеш");
                }
            }
            finally
            {
                table.Dispose();
            }
        }

        [Test]
        public void Неизвестный_хеш_и_ноль_не_находятся()
        {
            BlobchegBuild.RebuildAll();

            var table = LoadTable();
            try
            {
                Assert.That(table.TryGetId(BlobchegHashKey.Of<TestHashRouter>("такой ноды нет"), out _), Is.False);
                Assert.Throws<InvalidOperationException>(
                    () => table.GetId(BlobchegHashKey.Of<TestHashRouter>("такой ноды нет")));

                Assert.That(table.TryGetId(0, out _), Is.False,
                    "ноль — это пустой слот, а не первая строка");

                var alienTag = (byte)(table.Tag % 255 + 1);
                Assert.Throws<InvalidOperationException>(() => table.HashOf(BlobchegId.Make(alienTag, 0)),
                    "id чужого роутера здесь не значит ничего");
            }
            finally
            {
                table.Dispose();
            }
        }

        [Test]
        public void Дырка_от_удалённой_ноды_отдаёт_ноль()
        {
            BlobchegBuild.RebuildAll();

            // Дырку оставляет только тот, чей номер не последний; раздаются они по GUID, поэтому
            // кого убить, решает замер.
            var victim = new BlobchegNodeSo[] { _gun, _twin, _cold }.OrderBy(n => IdOf(n).Index).First();
            var killed = IdOf(victim);

            AssetDatabase.DeleteAsset(AssetDatabase.GetAssetPath(victim));
            BlobchegBuild.RebuildAll();

            var table = LoadTable();
            try
            {
                Assert.That(table.HashOf(killed), Is.EqualTo(0ul), "строка есть, но пустая");
                Assert.That(table.TryGetId(BlobchegHashKey.Of<TestHashRouter>("Gun"), out _),
                    Is.EqualTo(victim != (BlobchegNodeSo)_gun), "удалённая нода по хешу больше не находится");
            }
            finally
            {
                table.Dispose();
            }
        }

        [Test]
        public void Хеш_по_адресу_записи_сходится_с_хешем_по_id()
        {
            BlobchegBuild.RebuildAll();

            var table = LoadTable();
            try
            {
                var hash = BlobchegHashKey.Of<TestHashRouter>(_gun.BlobchegName);

                Assert.That(table.HashOfHot(OffsetOf(_gun, "ITestHashHot")), Is.EqualTo(hash));
                Assert.That(table.HashOfCold(OffsetOf(_gun, "ITestHashCold")), Is.EqualTo(hash));

                // Нода только в холодной базе: в горячей дорожке её адреса нет вовсе.
                Assert.That(table.TryHashOfCold(OffsetOf(_cold, "ITestHashCold"), out var coldHash), Is.True);
                Assert.That(coldHash, Is.EqualTo(BlobchegHashKey.Of<TestHashRouter>(_cold.BlobchegName)));

                Assert.That(table.TryHashOfHot(7, out _), Is.False, "чужой адрес — не ответ, а false");
                Assert.Throws<InvalidOperationException>(() => table.HashOfHot(7));
            }
            finally
            {
                table.Dispose();
            }
        }

        [Test]
        public void Запись_несёт_свой_хеш_и_хеш_соседа()
        {
            BlobchegBuild.RebuildAll();

            var table = LoadTable();
            var hot = LoadHot();
            var router = LoadRouter();
            try
            {
                ref readonly var record = ref hot.Read<TestHashHotRecord>(router.Get(IdOf(_gun)).hot);

                Assert.That(record.Self, Is.EqualTo(BlobchegHashKey.Of<TestHashRouter>(_gun.BlobchegName)));
                Assert.That(table.GetId(record.Self), Is.EqualTo(IdOf(_gun)));
                Assert.That(table.GetId(record.Twin), Is.EqualTo(IdOf(_twin)),
                    "хеш соседа в записи разворачивается в его строку");
            }
            finally
            {
                table.Dispose();
                hot.Dispose();
                router.Dispose();
            }
        }

        [Test]
        public void Компакт_двигает_адреса_а_хеш_остаётся()
        {
            BlobchegBuild.RebuildAll();

            var victim = new BlobchegNodeSo[] { _gun, _twin, _cold }.OrderBy(n => IdOf(n).Index).First();
            var survivor = victim == (BlobchegNodeSo)_gun ? _twin : _gun;

            var hash = BlobchegHashKey.Of<TestHashRouter>(survivor.BlobchegName);
            var wasId = IdOf(survivor);

            AssetDatabase.DeleteAsset(AssetDatabase.GetAssetPath(victim));
            BlobchegBuild.RebuildAll();
            BlobchegBuild.Compact();

            var nowId = IdOf(survivor);
            Assert.That(nowId, Is.Not.EqualTo(wasId), "компакт обязан сдвинуть номер строки — иначе тест ничего не ловит");
            Assert.That(BlobchegHashKey.Of<TestHashRouter>(survivor.BlobchegName), Is.EqualTo(hash),
                "хеш от компакта не зависит: он считается от имени");

            var table = LoadTable();
            var hot = LoadHot();
            var router = LoadRouter();
            try
            {
                Assert.That(table.TryGetId(hash, out var found), Is.True, "старый хеш обязан найтись после компакта");
                Assert.That(found, Is.EqualTo(nowId), "и вести на НОВЫЙ номер строки");

                ref readonly var record = ref hot.Read<TestHashHotRecord>(router.Get(found).hot);
                Assert.That(record.Self, Is.EqualTo(hash));
            }
            finally
            {
                table.Dispose();
                hot.Dispose();
                router.Dispose();
            }
        }

        /// <summary>
        /// Переименовать ассет прямо здесь нельзя: пересборка после <c>RenameAsset</c> в батче
        /// отказывается работать, пока редактор не доимпортирует, и это её собственное правило.
        /// Проверяется то же самое с другой стороны — имя ноды разведено с именем ассета, и хеш
        /// считается от имени ноды.
        /// </summary>
        [Test]
        public void Хеш_считается_от_имени_ноды_а_не_от_имени_ассета()
        {
            BlobchegBuild.RebuildAll();

            var byAssetName = BlobchegHashKey.Of<TestHashRouter>("Gun");

            Rename(_gun, "ak74m");
            AssetDatabase.SaveAssets();
            BlobchegBuild.RebuildAll();

            Assert.That(_gun.name, Is.EqualTo("Gun"), "имя ассета не тронуто");

            var table = LoadTable();
            var hot = LoadHot();
            var router = LoadRouter();
            try
            {
                Assert.That(table.TryGetId(byAssetName, out _), Is.False,
                    "имя ассета к хешу отношения не имеет, а прежнее имя ноды больше не находится: " +
                    "списка прежних имён нет");

                var now = BlobchegHashKey.Of<TestHashRouter>("ak74m");
                Assert.That(table.TryGetId(now, out var found), Is.True);
                Assert.That(found, Is.EqualTo(IdOf(_gun)));

                ref readonly var record = ref hot.Read<TestHashHotRecord>(router.Get(found).hot);
                Assert.That(record.Self, Is.EqualTo(now), "запись пересобралась с новым хешем");
            }
            finally
            {
                table.Dispose();
                hot.Dispose();
                router.Dispose();
            }
        }

        [Test]
        public void Два_одинаковых_имени_валят_пересборку()
        {
            BlobchegBuild.RebuildAll();

            Rename(_twin, _gun.BlobchegName);
            AssetDatabase.SaveAssets();

            var thrown = Assert.Throws<InvalidOperationException>(() => BlobchegBuild.RebuildAll());
            StringAssert.Contains(_gun.BlobchegName, thrown.Message);
            StringAssert.Contains("TestHashRouter", thrown.Message);

            // Вернуть проект в рабочее состояние, иначе следующая пересборка в TearDown упадёт тоже.
            Rename(_twin, "Twin");
            AssetDatabase.SaveAssets();
            Assert.DoesNotThrow(() => BlobchegBuild.RebuildAll());
        }

        [Test]
        public void Чужой_файл_и_чужая_раскладка_не_поднимаются()
        {
            BlobchegBuild.RebuildAll();

            var alienName = Read(TestHashTable.FileIdentity);
            try
            {
                Assert.Throws<InvalidOperationException>(() => new BlobchegHashesBlob(
                    alienName, "ЧужаяТаблица", TestHashRouter.RouterName,
                    TestHashTable.DomainCount, TestHashTable.LayoutHash), "личность файла обязана сойтись");
            }
            finally
            {
                alienName.Dispose();
            }

            var alienLayout = Read(TestHashTable.FileIdentity);
            try
            {
                Assert.Throws<InvalidOperationException>(() => new BlobchegHashesBlob(
                    alienLayout, TestHashTable.FileIdentity, TestHashRouter.RouterName,
                    TestHashTable.DomainCount, TestHashTable.LayoutHash + 1), "раскладка бит обязана сойтись");
            }
            finally
            {
                alienLayout.Dispose();
            }

            var router = Read(TestHashRouter.RouterName);
            try
            {
                Assert.Throws<InvalidOperationException>(() => new BlobchegHashesBlob(
                    router, TestHashTable.FileIdentity, TestHashRouter.RouterName,
                    TestHashTable.DomainCount, TestHashTable.LayoutHash),
                    "файл роутера не поднимается как таблица");
            }
            finally
            {
                router.Dispose();
            }
        }

        [Test]
        public void Манифест_таблицы_держит_хеш_файла_и_порядок_строк()
        {
            BlobchegBuild.RebuildAll();

            var manifest = AssetDatabase.LoadAssetAtPath<BlobchegDomainSo>(
                BlobchegBuild.ManifestFolder + "/" + TestHashTable.FileIdentity + ".asset");

            Assert.That(manifest, Is.Not.Null);
            Assert.That(manifest.kind, Is.EqualTo(BlobchegFileKind.Hashes));
            Assert.That(manifest.fileName, Is.EqualTo(TestHashTable.FileName));

            var file = File.ReadAllBytes(PathOf(TestHashTable.FileIdentity));
            Assert.That(manifest.ContentHash, Is.EqualTo(BitConverter.ToUInt64(file, 16)));

            for (var i = 0; i < manifest.nodes.Length; i++)
            {
                if (manifest.nodes[i] == null)
                    continue;

                Assert.That(IdOf(manifest.nodes[i]).Index, Is.EqualTo((uint)i),
                    "ноды в манифесте лежат в порядке строк");
            }
        }

        [Test]
        public void Хеш_несуществующей_ноды_бросает()
            => Assert.Throws<ArgumentNullException>(() => ((BlobchegNodeSo)null).HashIn<TestHashRouter>());
    }
}
