using System;
using System.Collections.Generic;
using System.Linq;
using Blobcheg.Authoring;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Blobcheg.AdvancedTests
{
    /// <summary>
    /// Личность записи. В пакете адресов ровно два — оффсет и <see cref="BlobchegId"/>, — и оба
    /// голые числа. Здесь проверяется, что случится, если число дать не тому, кому оно принадлежит.
    /// </summary>
    public sealed class IdentityTests : AdvancedFixture
    {
        /// <summary>
        /// Родство id держится на теге — старшем байте значения. Без него голое число попадало бы в
        /// диапазон соседнего роутера и отдавало чужую строку молча: проверка родства существовала
        /// бы ровно на уровне ассетов, а в компоненте, в сейве и на проводе от id остаётся uint.
        /// </summary>
        [Test]
        public void Id_чужого_роутера_не_резолвится_в_этом()
        {
            Node<AdvComboNodeSo>("Combo");
            var other = Node<AdvOtherNodeSo>("Other");
            Rebuild();

            var alien = IdOf(other, AdvOtherRouter.RouterName);

            var router = Router();
            try
            {
                Assert.That(alien.Index, Is.EqualTo(0u), "строка та же самая — отличает их только тег");
                Assert.That(router.Count, Is.GreaterThan((int)alien.Index),
                    "и она в этом роутере есть, иначе тест поймал бы обычный выход за диапазон");

                Assert.Throws<InvalidOperationException>(() => router.Get(alien),
                    "этот id выдан роутером AdvOtherRouter — в AdvRouter он не значит ничего");
                Assert.That(router.TryGet(alien, out _), Is.False);
            }
            finally
            {
                router.Dispose();
            }
        }

        [Test]
        public void Id_несёт_тег_своего_роутера()
        {
            var combo = Node<AdvComboNodeSo>("Combo");
            var other = Node<AdvOtherNodeSo>("Other");
            Rebuild();

            var mine = IdOf(combo, AdvRouter.RouterName);
            var alien = IdOf(other, AdvOtherRouter.RouterName);

            Assert.That(mine.Tag, Is.Not.Zero, "тег ноль зарезервирован под «id не назначен»");
            Assert.That(mine.Tag, Is.Not.EqualTo(alien.Tag), "два роутера — два разных тега");
            Assert.That(mine.Tag, Is.EqualTo(BlobchegNaming.TagOf(AdvRouter.RouterName)),
                "тег выводится из имени роутера, поэтому едитор и файл приходят к нему независимо");

            var router = Router();
            try
            {
                Assert.That(router.Tag, Is.EqualTo(mine.Tag));
                Assert.That(router.IdAt(mine.Index), Is.EqualTo(mine), "обход роутера даёт те же id");
            }
            finally
            {
                router.Dispose();
            }
        }

        [Test]
        public void Носитель_чужого_роутера_отбивается_полем()
        {
            Node<AdvComboNodeSo>("Combo");
            var other = Node<AdvOtherNodeSo>("Other");
            Rebuild();

            var alienCarrier = BlobchegBuild.IdsOf(other).Single(c => c.RouterName == AdvOtherRouter.RouterName);

            var thrown = Assert.Throws<InvalidOperationException>(
                () => { _ = new BlobchegIdRef<AdvRouter>(alienCarrier).Id; },
                "ассет чужого роутера в типизированном поле обязан быть отбит");
            StringAssert.Contains(AdvRouter.RouterName, thrown.Message);

            var empty = new BlobchegIdRef<AdvRouter>(null);
            Assert.That(empty.IsSet, Is.False);
            Assert.Throws<InvalidOperationException>(() => { _ = empty.Id; },
                "пустое поле — это ошибка, а не id ноль");
        }

        [Test]
        public void Ref_чужого_домена_отбивается_типизированным_полем()
        {
            var loose = Node<AdvLooseNodeSo>("Loose");
            Node<AdvComboNodeSo>("Combo");
            Rebuild();

            var looseRef = RefOf(loose, "IAdvLoose");

            var thrown = Assert.Throws<InvalidOperationException>(
                () => { _ = new BlobchegRef<AdvGun>(looseRef).Offset; },
                "в поле BlobchegRef<AdvGun> лежит запись другого типа — это ошибка");
            StringAssert.Contains(nameof(AdvGun), thrown.Message);

            Assert.That(new BlobchegRef<AdvLooseBlock>(looseRef).Offset, Is.EqualTo(looseRef.offset),
                "а свой тип обязан проходить");
        }

        /// <summary>
        /// Оффсет личности не несёт и нести не может: это позиция в файле, а не имя. Ловит чужой
        /// адрес отладочный контур — по нему видно, начинается ли в этом месте запись и та ли она.
        /// В редакторе и в development-билде контур есть всегда, в релизном плеере его нет, и там
        /// это снова вопрос доверия — ровно как и всё остальное содержимое записи.
        /// </summary>
        [Test]
        public void Оффсет_из_чужой_базы_не_читается_в_этой()
        {
            var loose = Node<AdvLooseNodeSo>("Loose");
            Node<AdvComboNodeSo>("Combo");
            Node<AdvArmorNodeSo>("Armor");
            Rebuild();

            var alienOffset = OffsetOf(loose, "IAdvLoose");

            var db = Combat();
            try
            {
                Assert.That(db.HasDebug, Is.True, "в редакторе отладочный контур обязан быть — на нём стоит проверка");
                Assert.That(alienOffset + 8u, Is.LessThanOrEqualTo((uint)db.Length),
                    "адрес обязан помещаться в боевую базу, иначе тест поймал бы обычный выход за границу");

                Assert.Throws<InvalidOperationException>(() => { _ = db.Read<AdvGun>(alienOffset).Rpm; },
                    "этот адрес выдан базой IAdvLoose — в боевой базе по нему лежит что угодно");
            }
            finally
            {
                db.Dispose();
            }
        }

        /// <summary>
        /// А вот у самого ФАЙЛА личность есть — хеш имени домена в header'е. Без неё два
        /// переставленных местами .bcheg поднимаются оба: целостность у каждого своя и сходится.
        /// </summary>
        [Test]
        public void Файл_чужой_базы_не_поднимается_под_этим_именем()
        {
            Node<AdvLooseNodeSo>("Loose");
            Node<AdvComboNodeSo>("Combo");
            Rebuild();

            // Подменяем файл боевой базы файлом холодной — ровно то, что делает неудачный мёрж
            // или ручное копирование «а, тут же просто данные».
            Overwrite("IAdvCombat", Bytes("IAdvCold"));

            var buffer = BufferOf(AdvCombatDb.FileName);
            try
            {
                var thrown = Assert.Throws<InvalidOperationException>(() => { _ = new AdvCombatDb(buffer); },
                    "файл целый и хеш сходится — но это файл другого домена");
                StringAssert.Contains("другого домена", thrown.Message);
            }
            finally
            {
                buffer.Dispose();
            }
        }

        [Test]
        public void Нода_вне_базы_отвечает_отсутствием_записи()
        {
            Node<AdvComboNodeSo>("Combo");
            var cold = Node<AdvColdOnlyNodeSo>("Cold");
            Rebuild();

            var id = IdOf(cold, AdvRouter.RouterName);

            var router = Router();
            try
            {
                var row = router.Get(id);
                Assert.That(row.HasCold, Is.True);
                Assert.That(row.HasCombat, Is.False);

                Assert.Throws<InvalidOperationException>(() => { _ = row.combat; },
                    "сентинела «записи нет» быть не может: молчаливый ноль поехал бы в Read");
                Assert.That(router.TryGetCombat(id, out _), Is.False);
                Assert.That(router.HasCombat(id), Is.False);
            }
            finally
            {
                router.Dispose();
            }
        }

        /// <summary>
        /// Нулевая инициализация — это ЛЮБОЙ default: поле IComponentData, элемент NativeArray, не
        /// выставленное поле структуры. Сентинелом выбран ноль именно поэтому: забытое поле обязано
        /// падать, а не молча приводить к первой ноде роутера.
        /// </summary>
        [Test]
        public void Дефолтный_BlobchegId_не_валиден()
        {
            Node<AdvComboNodeSo>("Combo");
            Rebuild();

            Assert.That(default(BlobchegId).IsValid, Is.False,
                "не заполненное поле — это «не задано», а не строка ноль");

            var router = Router();
            try
            {
                Assert.That(router.Count, Is.EqualTo(1), "строка ноль в роутере при этом есть");
                Assert.That(router.TryGet(default, out _), Is.False,
                    "нулевой id не имеет права отдавать первую ноду базы");
                Assert.Throws<InvalidOperationException>(() => router.Get(default));
            }
            finally
            {
                router.Dispose();
            }
        }

        [Test]
        public void Правка_значения_не_двигает_ни_id_ни_оффсет()
        {
            var node = Node<AdvComboNodeSo>("Combo");
            node.tier = 1;
            Dirty(node);
            Rebuild();

            var idBefore = IdOf(node, AdvRouter.RouterName);
            var offsetBefore = OffsetOf(node, "IAdvCold");

            node.tier = 12345;
            Dirty(node);
            Rebuild();

            Assert.That(IdOf(node, AdvRouter.RouterName), Is.EqualTo(idBefore), "id — позиция, а не хеш содержимого");
            Assert.That(OffsetOf(node, "IAdvCold"), Is.EqualTo(offsetBefore), "размер не менялся — адрес тоже");

            var cold = Cold();
            try
            {
                Assert.That(cold.Read<AdvColdInfo>(offsetBefore).Tier, Is.EqualTo(12345),
                    "а вот значение обязано было поменяться");
            }
            finally
            {
                cold.Dispose();
            }
        }

        /// <summary>
        /// Переименовали и тут же пересобрали — так ходит любой скрипт и гейт пре-билда. В этом
        /// заходе редактора база ассетов переименование ещё не переварила: ассет не поднимается ни
        /// под старым путём, ни под новым, а поисковый индекс о нём не знает — проверено замером,
        /// <c>ImportAsset(ForceSynchronousImport)</c> и <c>Refresh</c> этого не чинят.
        ///
        /// Значит исходов ровно два, и оба обязаны быть честными: либо обход ноду видит, либо
        /// пересборка ОТКАЗЫВАЕТСЯ. Чего быть не должно — третьего: молча собранной базы без её
        /// записи и со съехавшими id соседей. Ассет на диске лежит, GUID его известен, поэтому
        /// отличить потерю от удаления пакету есть по чему.
        /// </summary>
        [Test]
        public void Переименование_ноды_не_теряет_её_молча()
        {
            var a = Node<AdvColdOnlyNodeSo>("Alpha");
            Node<AdvColdOnlyNodeSo>("Beta");
            Rebuild();

            var before = IdOf(a, AdvRouter.RouterName);

            // GUID берётся ДО переименования: managed-обёртка переименованного ассета реимпорт не
            // переживает, и держаться за неё — ошибка теста, а не находка про пакет.
            var guid = AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(a));
            Assert.That(AssetDatabase.RenameAsset(AssetDatabase.GetAssetPath(a), "Zulu"), Is.Empty,
                "переименование обязано пройти, иначе дальше проверять нечего");
            AssetDatabase.SaveAssets();

            var renamedPath = Folder + "/Zulu.asset";
            AssetDatabase.ImportAsset(renamedPath, ImportAssetOptions.ForceSynchronousImport);

            Assert.That(AssetDatabase.AssetPathToGUID(renamedPath), Is.EqualTo(guid),
                "GUID переименованного ассета обязан остаться тем же — на нём и держится id");

            List<BlobchegNodeSo> seen = null;
            InvalidOperationException refused = null;
            try
            {
                seen = BlobchegBuild.FindNodes();
            }
            catch (InvalidOperationException e)
            {
                refused = e;
            }

            if (refused != null)
            {
                StringAssert.Contains("Zulu", refused.Message, "отказ обязан называть ноду, из-за которой он случился");
                Assert.Throws<InvalidOperationException>(() => Rebuild(),
                    "и сама пересборка в этом состоянии обязана отказаться так же, а не собрать базу без ноды");

                // Убираем ассет сами: пока он лежит непереваренным, обход будет отказывать и дальше.
                AssetDatabase.DeleteAsset(renamedPath);
                AssetDatabase.Refresh();
                return;
            }

            // Ищем ноду ПО ПУТИ, а не по имени: имя managed-объекта после переименования
            // обновляется отложенно.
            var renamed = seen.FirstOrDefault(n => AssetDatabase.GetAssetPath(n) == renamedPath);
            Assert.That(renamed, Is.Not.Null,
                "обход не отказался — значит обязан был увидеть ноду; вернул: " +
                string.Join(", ", seen.Select(n => AssetDatabase.GetAssetPath(n))));

            Rebuild();
            Assert.That(IdOf(renamed, AdvRouter.RouterName), Is.EqualTo(before),
                "id считается по GUID ассета — имя на него влиять не имеет права");
        }

        [Test]
        public void Ноды_с_одинаковым_именем_различимы()
        {
            var one = Node<AdvColdOnlyNodeSo>("Same");
            var two = NodeIn<AdvColdOnlyNodeSo>("Nested", "Same");
            one.tier = 101;
            two.tier = 202;
            Dirty(one);
            Dirty(two);

            Rebuild();

            var idOne = IdOf(one, AdvRouter.RouterName);
            var idTwo = IdOf(two, AdvRouter.RouterName);
            Assert.That(idOne, Is.Not.EqualTo(idTwo), "имя не является личностью ноды — GUID является");

            var router = Router();
            var cold = Cold();
            try
            {
                Assert.That(cold.Read<AdvColdInfo>(router.GetCold(idOne)).Tier, Is.EqualTo(101));
                Assert.That(cold.Read<AdvColdInfo>(router.GetCold(idTwo)).Tier, Is.EqualTo(202));
            }
            finally
            {
                router.Dispose();
                cold.Dispose();
            }
        }

        [Test]
        public void Нода_в_двух_роутерах_требует_явного_IdIn()
        {
            var node = Node<AdvBothRoutersNodeSo>("Both");
            node.askSingleId = true;
            Dirty(node);

            Assert.Throws<InvalidOperationException>(() => Rebuild(),
                "у ноды два роутера — «свой единственный id» у неё спросить нельзя, это должна быть ошибка");

            node.askSingleId = false;
            Dirty(node);
            Rebuild();

            var mine = IdOf(node, AdvRouter.RouterName);
            var alien = IdOf(node, AdvOtherRouter.RouterName);

            Assert.That(node.LastMain, Is.EqualTo(mine.Value), "IdIn отдал тот же id, что уехал в носитель");
            Assert.That(node.LastOther, Is.EqualTo(alien.Value));
            Assert.That(BlobchegBuild.IdsOf(node).Count(), Is.EqualTo(2), "по носителю на роутер");
        }

        [Test]
        public void Id_плотный_и_непрерывный()
        {
            var nodes = new BlobchegNodeSo[]
            {
                Node<AdvComboNodeSo>("A"),
                Node<AdvColdOnlyNodeSo>("B"),
                Node<AdvColdOnlyNodeSo>("C"),
                Node<AdvArmorNodeSo>("D"),
            };

            Rebuild();

            var ids = nodes.Select(n => IdOf(n, AdvRouter.RouterName).Index).OrderBy(v => v).ToArray();
            CollectionAssert.AreEqual(new uint[] { 0, 1, 2, 3 }, ids,
                "строка — плотный индекс; дырки в нём сделали бы array[index] невозможным");

            var router = Router();
            try
            {
                Assert.That(router.Count, Is.EqualTo(nodes.Length));
            }
            finally
            {
                router.Dispose();
            }
        }

        [Test]
        public void Роутер_и_ref_ассет_дают_один_адрес()
        {
            var node = Node<AdvComboNodeSo>("Combo");
            Node<AdvArmorNodeSo>("Armor");
            Rebuild();

            var viaRef = OffsetOf(node, "IAdvCombat");
            var id = IdOf(node, AdvRouter.RouterName);

            var router = Router();
            try
            {
                Assert.That(router.GetCombat(id), Is.EqualTo(viaRef),
                    "два адреса пакета обязаны сходиться на одной записи, иначе их два разных");
                Assert.That(router.Get(id).combat, Is.EqualTo(viaRef));
            }
            finally
            {
                router.Dispose();
            }
        }
    }
}
