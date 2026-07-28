using System;
using System.IO;
using System.Linq;
using Blobcheg.Authoring;
using NUnit.Framework;
using UnityEditor;
using UnityEngine.TestTools;

namespace Blobcheg.AdvancedTests
{
    /// <summary>
    /// Употребление не по назначению и прямой абсурд: два фасада над одним файлом, записи, ссылающиеся
    /// друг на друга по кругу, нода-ссылка на саму себя, собранный блоб, подсунутый обратно как
    /// исходник, и запись с сырым указателем внутри.
    ///
    /// Абсурдные сценарии тут не ради смеха: они вскрывают то, что подразумевалось молча.
    /// </summary>
    public sealed class SemanticAndAbsurdTests : AdvancedFixture
    {
        [Test]
        public void Два_фасада_над_одним_доменом_читают_одно_и_то_же()
        {
            var node = Node<AdvLooseNodeSo>("Loose");
            node.a = 111;
            node.b = 222;
            Dirty(node);
            Rebuild();

            Assert.That(AdvLooseTwinDb.FileName, Is.EqualTo(AdvLooseDb.FileName),
                "две базы над одним доменом — это один и тот же файл");

            var offset = OffsetOf(node, "IAdvLoose");
            var first = Loose();
            var second = new AdvLooseTwinDb(BufferOf(AdvLooseTwinDb.FileName));
            try
            {
                Assert.That(first.Read<AdvLooseBlock>(offset).A, Is.EqualTo(111));
                Assert.That(second.Read<AdvLooseBlock>(offset).A, Is.EqualTo(111),
                    "либо второй фасад запрещён, либо он обязан читать ровно то же");
                Assert.That(second.Read<AdvLooseBlock>(offset).B, Is.EqualTo(222));
            }
            finally
            {
                first.Dispose();
                second.Dispose();
            }
        }

        [Test]
        public void Домен_вне_роутера_живёт_без_id()
        {
            var loose = Node<AdvLooseNodeSo>("Loose");
            var combo = Node<AdvComboNodeSo>("Combo");
            Rebuild();

            Assert.That(BlobchegBuild.IdsOf(loose).Count(), Is.Zero,
                "база не вступала ни в один роутер — id у её нод не бывает вовсе");
            Assert.That(BlobchegBuild.RefsOf(loose).Count(), Is.EqualTo(1), "а оффсет есть — он единственный адрес");

            Assert.That(BlobchegBuild.IdsOf(combo).Count(), Is.EqualTo(1), "нода роутера носит ровно один id");
        }

        [Test]
        public void Два_роутера_рядом_не_путают_биты()
        {
            var combo = Node<AdvComboNodeSo>("Combo");
            var other = Node<AdvOtherNodeSo>("Other");
            other.v = 4242;
            Dirty(other);
            Rebuild();

            var mainRouter = Router();
            var otherRouter = OtherRouter();
            var otherDb = Other();
            try
            {
                Assert.That(mainRouter.Count, Is.EqualTo(1), "в главный роутер вошла только своя нода");
                Assert.That(otherRouter.Count, Is.EqualTo(1));

                var mine = mainRouter.Get(IdOf(combo, AdvRouter.RouterName));
                Assert.That(mine.HasCombat, Is.True);
                Assert.That(mine.HasCold, Is.True);

                var alien = otherRouter.Get(IdOf(other, AdvOtherRouter.RouterName));
                Assert.That(alien.HasOther, Is.True);
                Assert.That(otherDb.Read<AdvOtherInfo>(alien.other).V, Is.EqualTo(4242));
            }
            finally
            {
                mainRouter.Dispose();
                otherRouter.Dispose();
                otherDb.Dispose();
            }
        }

        [Test]
        public void Собранный_блоб_подсунутый_как_исходник_не_ломает_пересборку()
        {
            Node<AdvComboNodeSo>("Combo");
            Rebuild();

            // Абсурд: берём выход пайплайна и кладём его на вход, притворившись ассетом.
            // Импорт двоичного мусора под видом .asset — законный повод для ошибки в консоли: она
            // тут ожидаема, и глушится она в самом тесте, потому что фреймворк сбрасывает флаг
            // после SetUp.
            LogAssert.ignoreFailingMessages = true;

            var built = Bytes("IAdvCombat");
            File.WriteAllBytes(Folder + "/Impostor.asset", built);
            AssetDatabase.Refresh();

            Assert.DoesNotThrow(() => Rebuild(),
                "чужой .asset в проекте не имеет права ни ломать пересборку, ни быть принятым за ноду");

            var db = Combat();
            try
            {
                Assert.That(db.IsCreated, Is.True, "и база после этого обязана остаться рабочей");
            }
            finally
            {
                db.Dispose();
            }
        }

        [Test]
        public void Записи_ссылаются_друг_на_друга_по_кругу()
        {
            var a = Node<AdvComboNodeSo>("CycleA");
            var b = Node<AdvComboNodeSo>("CycleB");
            a.link = b;
            b.link = a;
            Dirty(a);
            Dirty(b);

            Rebuild();

            var idA = IdOf(a, AdvRouter.RouterName);
            var idB = IdOf(b, AdvRouter.RouterName);

            var router = Router();
            var cold = Cold();
            try
            {
                // Идём по кругу вручную, с капом: пакет цикл не запрещает и запрещать не обязан,
                // но пройти по нему должно быть можно, и он обязан замкнуться.
                var at = idA;
                for (var hop = 0; hop < 4; hop++)
                {
                    var link = cold.Read<AdvColdInfo>(router.GetCold(at)).LinkId;
                    Assert.That(link, Is.Not.EqualTo(BlobchegId.NoneValue), $"шаг {hop} потерял ссылку");
                    at = new BlobchegId(link);
                }

                Assert.That(at, Is.EqualTo(idA), "чётное число шагов по кругу из двух обязано вернуть на старт");
                Assert.That(cold.Read<AdvColdInfo>(router.GetCold(idA)).LinkId, Is.EqualTo(idB.Value));
                Assert.That(cold.Read<AdvColdInfo>(router.GetCold(idB)).LinkId, Is.EqualTo(idA.Value));
            }
            finally
            {
                router.Dispose();
                cold.Dispose();
            }
        }

        [Test]
        public void Нода_ссылающаяся_сама_на_себя_собирается()
        {
            var node = Node<AdvComboNodeSo>("Ouroboros");
            node.link = node;
            Dirty(node);

            Rebuild();

            var id = IdOf(node, AdvRouter.RouterName);

            var router = Router();
            var cold = Cold();
            try
            {
                ref readonly var record = ref cold.Read<AdvColdInfo>(router.GetCold(id));
                Assert.That(record.SelfId, Is.EqualTo(id.Value));
                Assert.That(record.LinkId, Is.EqualTo(id.Value),
                    "ссылка на саму себя — это тот же id, а не рекурсия и не отказ");
            }
            finally
            {
                router.Dispose();
                cold.Dispose();
            }
        }

        [Test]
        public void Свой_id_в_записи_остаётся_верным_после_перестановки()
        {
            var created = new[]
            {
                Node<AdvColdOnlyNodeSo>("R1"),
                Node<AdvColdOnlyNodeSo>("R2"),
                Node<AdvColdOnlyNodeSo>("R3"),
            };

            Rebuild();

            var byId = created.OrderBy(n => IdOf(n, AdvRouter.RouterName).Value).ToArray();
            Kill(byId[0]);
            Rebuild();

            var router = Router();
            var cold = Cold();
            try
            {
                foreach (var node in new[] { byId[1], byId[2] })
                {
                    var id = IdOf(node, AdvRouter.RouterName);
                    Assert.That(cold.Read<AdvColdInfo>(router.GetCold(id)).SelfId, Is.EqualTo(id.Value),
                        $"нода '{node.name}' положила в запись свой id — после перестановки он обязан сойтись");
                }
            }
            finally
            {
                router.Dispose();
                cold.Dispose();
            }
        }

        /// <summary>
        /// Констрейнт <c>where T : unmanaged</c> отвечает только за «нет managed-ссылок»: структуру
        /// с полем <c>byte*</c> или <c>IntPtr</c> он пропускает, потому что она формально unmanaged.
        /// Отбивает её отдельная проверка пайплайна — и отбивать обязан именно пайплайн: адрес
        /// переживает запись, но не перезапуск процесса, и при чтении отдаёт мусор, неотличимый от
        /// значения.
        /// </summary>
        [Test]
        public void Запись_с_сырым_указателем_отбивается()
        {
            var node = Node<AdvPointerNodeSo>("Pointer");

            var thrown = Assert.Throws<InvalidOperationException>(() => Rebuild(),
                "указатель в файле — это не данные; такую запись обязан отбить пайплайн, а не потребитель");
            StringAssert.Contains("Ptr", thrown.Message,
                "и назвать поле: искать его глазами в толстой структуре — работа не человека");

            Kill(node);
        }

        [Test]
        public void Указатель_в_глубине_записи_тоже_отбивается()
        {
            var node = Node<AdvNestedPointerNodeSo>("Nested");

            var thrown = Assert.Throws<InvalidOperationException>(() => Rebuild(),
                "указатель, спрятанный в поле-структуре, ничем не лучше указателя на виду");
            StringAssert.Contains(nameof(AdvPointerHolder.Handle), thrown.Message);

            Kill(node);
        }

        [Test]
        public void Запись_без_единого_поля_адресуема()
        {
            var one = Node<AdvEmptyRecordNodeSo>("EmptyA");
            var two = Node<AdvEmptyRecordNodeSo>("EmptyB");
            Rebuild();

            var first = OffsetOf(one, "IAdvLoose");
            var second = OffsetOf(two, "IAdvLoose");

            Assert.That(first, Is.Not.EqualTo(second),
                "у записи без полей всё равно есть размер, и два адреса совпасть не могут");

            var db = Loose();
            try
            {
                Assert.DoesNotThrow(() => { Copy(db.Read<AdvEmptyRecord>(first)); });
                Assert.DoesNotThrow(() => { Copy(db.Read<AdvEmptyRecord>(second)); });
            }
            finally
            {
                db.Dispose();
            }
        }
    }
}
