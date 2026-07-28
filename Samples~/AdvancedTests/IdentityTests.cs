using System;
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
        // BUG: id, выданный ДРУГИМ роутером, резолвится в этом молча и отдаёт чужую строку.
        // Ожидалось: чужой id обязан быть отбит — либо исключением, либо честным «строки нет».
        // Корень: BlobchegId — это голый uint-индекс, а файл роутера не несёт никакой своей
        // личности: в прологе лежат Count, DomainCount, LayoutHash и оффсеты массивов, но нет ни
        // идентификатора роутера, ни поколения. BlobchegRouterBlob.Get проверяет ТОЛЬКО диапазон
        // (id.Value >= _count), поэтому любой индекс в диапазоне считается своим. Проверка родства
        // существует ровно на уровне ассетов (BlobchegIdRef сверяет routerName), и как только id
        // превращается в uint — в компоненте, в сейве, на проводе — родства больше нет.
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
                Assert.Throws<InvalidOperationException>(() => router.Get(alien),
                    "этот id выдан роутером AdvOtherRouter — в AdvRouter он не значит ничего");
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

        // BUG: оффсет из ЧУЖОЙ базы читается в этой молча и отдаёт чужие байты как свою запись.
        // Ожидалось: адрес, выданный другой базой, обязан быть отбит явно.
        // Корень: в header'е файла (BlobchegHeader) нет ничего, что говорило бы, ЧЕЙ это файл —
        // только Magic, Version, Flags, длина и хеш содержимого. Единственная проверка чтения,
        // BlobchegBlob.CheckRead, сверяет оффсет с длиной СВОЕГО буфера и с выравниванием; про то,
        // что число приехало из соседней базы, узнать нечем. Типизированное поле BlobchegRef<T>
        // ловит это на уровне ассета, но ровно до того момента, как оффсет станет uint в компоненте.
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
                Assert.Throws<InvalidOperationException>(() => { _ = db.Read<AdvGun>(alienOffset).Rpm; },
                    "этот адрес выдан базой IAdvLoose — в боевой базе по нему лежит что угодно");
            }
            finally
            {
                db.Dispose();
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

        // BUG: default(BlobchegId) — это валидная строка 0, а не «id не назначен».
        // Ожидалось: значение по умолчанию не должно быть валидным адресом; поле, которое забыли
        // заполнить, обязано падать, а не молча указывать на первую ноду в базе.
        // Корень: сентинелом выбран BlobchegId.NoneValue = uint.MaxValue, поэтому нулевая
        // инициализация — а это ЛЮБОЙ default: поле IComponentData, элемент NativeArray, не
        // выставленное поле структуры — даёт Value == 0, который проходит проверку
        // BlobchegRouterBlob.Get (0 < Count) и IsValid. Сентинел 0 с id, начинающимися с единицы,
        // закрыл бы это by construction; выбранный обратный порядок оставляет ловушку открытой.
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
                Assert.That(router.TryGet(default, out _), Is.False,
                    "нулевой id не имеет права отдавать первую ноду базы");
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

        // BUG: пересборка, запущенная сразу после переименования ноды, НЕ ВИДИТ эту ноду вовсе —
        // обход возвращает только соседку, ни под старым путём, ни под новым переименованной нет.
        // Ожидалось: пересборка обязана видеть все ноды проекта; ассет, только что переименованный,
        // никуда не девался.
        // Последствия: заход в таком состоянии выкидывает запись ноды из файла, сдвигает id всех
        // нод, стоящих после неё, и оставляет ref-ассет указывающим на адрес, которого больше нет.
        // Молча — отчёт при этом бодро рапортует об успешной пересборке.
        // Корень: BlobchegBuild.FindNodes обходит AssetDatabase.GetAllAssetPaths(). Комментарий в
        // нём объясняет, что FindAssets("t:...") не годится — поисковый индекс отстаёт от импорта;
        // но GetAllAssetPaths отстаёт ТОЖЕ, просто на другом событии: переименование в том же
        // заходе редактора в него ещё не попало. ImportAsset(..., ForceSynchronousImport) на новый
        // путь этого не чинит. В обычной работе дыра замаскирована тем, что хук зовёт пересборку
        // через EditorApplication.delayCall, то есть уже после того, как база ассетов улеглась;
        // открытой она остаётся для синхронных путей — гейта пре-билда BlobchegBuild.RequireUpToDate
        // и любого скрипта, который переименовывает ассет и тут же пересобирает.
        [Test]
        public void Переименование_ноды_не_двигает_id()
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

            // Импорт после переименования отложенный, и без принудительной синхронизации ассет по
            // новому пути ещё не загружен: это грабли редактора, а не поведение пакета.
            AssetDatabase.ImportAsset(renamedPath, ImportAssetOptions.ForceSynchronousImport);
            Rebuild();

            Assert.That(AssetDatabase.AssetPathToGUID(renamedPath), Is.EqualTo(guid),
                "GUID переименованного ассета обязан остаться тем же — на нём и держится id");

            // Ищем ноду тем же обходом, каким её ищет сама пересборка, и ПО ПУТИ, а не по имени:
            // имя managed-объекта после переименования обновляется отложенно.
            var seen = BlobchegBuild.FindNodes();
            var renamed = seen.FirstOrDefault(n => AssetDatabase.GetAssetPath(n) == renamedPath);
            Assert.That(renamed, Is.Not.Null,
                "пересборка обязана видеть переименованную ноду; обход вернул: " +
                string.Join(", ", seen.Select(n => AssetDatabase.GetAssetPath(n) + " [" + n.name + "]")));

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

            var ids = nodes.Select(n => IdOf(n, AdvRouter.RouterName).Value).OrderBy(v => v).ToArray();
            CollectionAssert.AreEqual(new uint[] { 0, 1, 2, 3 }, ids,
                "id — плотный индекс строки; дырки в нём сделали бы array[id] невозможным");

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
