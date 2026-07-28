using System;
using System.IO;
using System.Linq;
using Blobcheg.Authoring;
using NUnit.Framework;
using Unity.Collections;
using UnityEditor;

namespace Blobcheg.AdvancedTests
{
    /// <summary>
    /// Человеческий фактор. Не злоумышленник и не край диапазона, а обычные привычки: сохранить id
    /// в сейв, закешировать оффсет, проверить «не ноль ли», поверить манифесту, поправить пару байт
    /// в собранном файле руками. Эти сценарии стоят продакшена чаще, чем все границы вместе.
    /// </summary>
    public sealed class HumanFactorTests : AdvancedFixture
    {
        /// <summary>
        /// Самая дорогая из возможных поломок: игрок открывает сохранение и получает не тот предмет,
        /// без единой ошибки. Закрыта тем, что id не пересчитывается — он лежит на носителе ноды и
        /// наследуется, а удалённая нода оставляет за собой пустую строку. Строка-дырка стоит
        /// нескольких байт в файле; подтянуть следующую значило бы сдвинуть чужой сохранённый id.
        /// </summary>
        [Test]
        public void Сохранённый_id_после_удаления_соседа_не_указывает_на_другую_ноду()
        {
            var created = new[]
            {
                Node<AdvColdOnlyNodeSo>("N1"),
                Node<AdvColdOnlyNodeSo>("N2"),
                Node<AdvColdOnlyNodeSo>("N3"),
            };

            Rebuild();

            var byId = created.OrderBy(n => IdOf(n, AdvRouter.RouterName).Index).ToArray();
            for (var i = 0; i < byId.Length; i++)
            {
                byId[i].tier = 100 * (i + 1);
                Dirty(byId[i]);
            }

            Rebuild();

            // Так делает потребитель: взял id и положил его в сейв. Дальше он видит только число.
            var saved = IdOf(byId[1], AdvRouter.RouterName);
            Assert.That(saved.Index, Is.EqualTo(1u));

            Kill(byId[0]);
            Rebuild();

            var router = Router();
            var cold = Cold();
            try
            {
                Assert.That(router.Count, Is.EqualTo(3),
                    "удалённая нода оставила дырку: строк по-прежнему три, первая пуста");
                Assert.That(router.HasCold(BlobchegId.In(AdvRouter.RouterName, 0)), Is.False, "и она пуста");

                Assert.That(IdOf(byId[1], AdvRouter.RouterName), Is.EqualTo(saved),
                    "id соседа не съезжает следом за удалённым");
                Assert.That(cold.Read<AdvColdInfo>(router.GetCold(saved)).Tier, Is.EqualTo(200),
                    "сохранённый id обязан вести к своей ноде");
            }
            finally
            {
                router.Dispose();
                cold.Dispose();
            }
        }

        /// <summary>
        /// Кешировать адрес потребитель будет — в компоненте, в статике, в запечённой субсцене.
        /// Поэтому адрес закреплён за записью: прошлый адрес приезжает в раскладку заявкой с
        /// носителя ноды, и появление соседа его не двигает. Двигает только компакт — и он на то и
        /// отдельная команда, что после него перепекается всё, что адрес запомнило.
        /// </summary>
        [Test]
        public void Закешированный_оффсет_переживает_появление_чужой_записи()
        {
            var gun = Node<AdvComboNodeSo>("Combo");
            gun.rpm = 999;
            Dirty(gun);
            Rebuild();

            // Потребитель закешировал адрес у себя: положил в компонент, в статик, в сейв — неважно.
            var cached = OffsetOf(gun, "IAdvCombat");

            // Появилась ЧУЖАЯ нода с типом, имя которого сортируется РАНЬШЕ AdvGun: без заявки на
            // прежний адрес она сдвинула бы все записи следующих типов.
            Node<AdvArmorNodeSo>("Armor");
            Rebuild();

            Assert.That(OffsetOf(gun, "IAdvCombat"), Is.EqualTo(cached),
                "новая нода не имеет права двигать чужой адрес");

            var db = Combat();
            try
            {
                Assert.That(db.Read<AdvGun>(cached).Rpm, Is.EqualTo(999),
                    "и по закешированному адресу лежит та же запись");
            }
            finally
            {
                db.Dispose();
            }
        }

        /// <summary>
        /// Обратная сторона: компакт адреса двигает нарочно. Здесь важно, что он не оставляет
        /// потребителя с протухшим числом молча — носители переписаны, и по старому адресу либо
        /// лежит не та запись, либо не лежит ничего.
        /// </summary>
        [Test]
        public void Компакт_двигает_адрес_и_переписывает_носитель()
        {
            var armor = Node<AdvArmorNodeSo>("Armor");
            var gun = Node<AdvComboNodeSo>("Combo");
            Rebuild();

            Kill(armor);
            Rebuild();

            var withHole = OffsetOf(gun, "IAdvCombat");

            BlobchegBuild.Compact();

            var compacted = OffsetOf(gun, "IAdvCombat");
            Assert.That(compacted, Is.LessThan(withHole), "компакт убрал дырку и подтянул запись");
            Assert.That(compacted, Is.EqualTo((uint)BlobchegFormat.HeaderSize),
                "единственная запись после компакта лежит сразу за header'ом");

            var db = Combat();
            try
            {
                Assert.That(db.Read<AdvGun>(compacted).Rpm, Is.EqualTo(600), "по новому адресу читается");
                Assert.Throws<InvalidOperationException>(() => { _ = db.Read<AdvGun>(withHole).Rpm; },
                    "а по старому — уже нет, и это видно, а не молчит");
            }
            finally
            {
                db.Dispose();
            }
        }

        [Test]
        public void Порядок_id_воспроизводим_между_пересборками()
        {
            var created = new BlobchegNodeSo[]
            {
                Node<AdvColdOnlyNodeSo>("Zulu"),
                Node<AdvColdOnlyNodeSo>("Alpha"),
                Node<AdvComboNodeSo>("Mike"),
            };

            Rebuild();
            var first = created.Select(n => IdOf(n, AdvRouter.RouterName).Value).ToArray();

            Rebuild();
            Rebuild();

            var again = created.Select(n => IdOf(n, AdvRouter.RouterName).Value).ToArray();

            CollectionAssert.AreEqual(first, again,
                "порядок id обязан быть функцией проекта, а не порядка обхода: иначе билд и редактор разъедутся");
        }

        /// <summary>
        /// Привычка «if (id != 0)» — самая распространённая проверка на свете, и здесь она обязана
        /// работать. Работает она потому, что тег ноль зарезервирован: строка ноль существует, но
        /// её id нулём не бывает.
        /// </summary>
        [Test]
        public void Привычная_проверка_на_ноль_работает()
        {
            var created = new[]
            {
                Node<AdvColdOnlyNodeSo>("A"),
                Node<AdvColdOnlyNodeSo>("B"),
            };

            Rebuild();

            var ids = created.Select(n => IdOf(n, AdvRouter.RouterName)).ToArray();
            Assert.That(ids.Count(id => id.Index == 0), Is.EqualTo(1), "строка ноль есть, как и раньше");
            Assert.That(ids.Count(id => id.Value == 0), Is.Zero, "а вот id ноль не выдаётся никому");

            Assert.That(BlobchegId.None.IsValid, Is.False);
            Assert.That(BlobchegId.None.Value, Is.Zero, "«не назначен» и ноль — одно и то же значение");
            Assert.That(new BlobchegId(0).IsValid, Is.False);
        }

        [Test]
        public void Манифест_домена_не_является_доказательством_собранности()
        {
            Node<AdvComboNodeSo>("Combo");
            Rebuild();

            var manifest = AssetDatabase.LoadAssetAtPath<BlobchegDomainSo>(
                BlobchegBuild.ManifestFolder + "/IAdvCombat.asset");

            Assert.That(manifest, Is.Not.Null, "манифест — то, что разработчик видит глазами в проекте");
            Assert.That(manifest.recordCount, Is.GreaterThan(0));

            File.Delete(FileOf("IAdvCombat"));

            Assert.That(manifest.recordCount, Is.GreaterThan(0),
                "манифест по-прежнему бодро рапортует о записях, которых на диске больше нет");

            var load = BlobchegTransport.Default.Read(AdvCombatDb.FileName, Allocator.Persistent);
            try
            {
                Assert.Throws<InvalidOperationException>(() => load.Complete(),
                    "зато подъём обязан упасть явно — иначе рассинхрон манифеста и файла ушёл бы в рантайм");
            }
            finally
            {
                load.Dispose();
            }
        }

        [Test]
        public void Возвращённая_запись_копируется_и_база_не_портится()
        {
            var node = Node<AdvComboNodeSo>("Combo");
            node.rpm = 600;
            Dirty(node);
            Rebuild();

            var offset = OffsetOf(node, "IAdvCombat");
            var db = Combat();
            try
            {
                // Привычка из managed-мира: «взял объект, поправил поле». Здесь это копия.
                var mine = db.Read<AdvGun>(offset);
                mine.Rpm = 1;
                mine.Ammo = -1f;

                Assert.That(db.Read<AdvGun>(offset).Rpm, Is.EqualTo(600),
                    "правка копии не имеет права доехать до базы — её читают все сразу");
                Assert.That(db.Read<AdvGun>(offset).Ammo, Is.EqualTo(30f));
            }
            finally
            {
                db.Dispose();
            }
        }

        [Test]
        public void Правка_собранного_файла_руками_отбивается()
        {
            var node = Node<AdvComboNodeSo>("Combo");
            node.rpm = 600;
            Dirty(node);
            Rebuild();

            // «Это же просто данные» — разработчик правит значение прямо в бинарнике.
            var offset = (int)OffsetOf(node, "IAdvCombat");
            var file = Bytes("IAdvCombat");
            BlobchegBytes.WriteU32(file, offset + 4, 1234);

            var buffer = BlobchegBuffer.From(file, Allocator.Persistent);
            try
            {
                Assert.Throws<InvalidOperationException>(() => { _ = new AdvCombatDb(buffer); },
                    "файл — производное, а не исходник; правка мимо пересборки обязана быть видна сразу");
            }
            finally
            {
                buffer.Dispose();
            }
        }

        [Test]
        public void Правка_значения_видна_в_отчёте_пересборки()
        {
            var node = Node<AdvComboNodeSo>("Combo");
            node.rpm = 1;
            Dirty(node);
            Rebuild();

            node.rpm = 2;
            Dirty(node);
            var report = Rebuild();

            Assert.That(report.Changed, Is.True, "изменилось значение — пересборка обязана это заметить");
            Assert.That(report.ChangedFiles, Is.GreaterThan(0), "и переписать файл");

            var quiet = Rebuild();
            Assert.That(quiet.Changed, Is.False, "а вот после этого — не трогать ничего");
        }

        [Test]
        public void Удаление_последней_ноды_очищает_базу_а_не_оставляет_вчерашнюю()
        {
            var node = Node<AdvComboNodeSo>("Combo");
            Rebuild();

            Assert.That(BlobchegBuild.RefsOf(node).Count(), Is.EqualTo(2), "нода писала в две базы");
            var before = Bytes("IAdvCombat").Length;

            Kill(node);
            Rebuild();

            Assert.That(File.Exists(FileOf("IAdvCombat")), Is.True);

            var db = Combat();
            try
            {
                Assert.That(db.Length, Is.LessThan(before));
                Assert.That(db.Length, Is.EqualTo(BlobchegFormat.HeaderSize),
                    "нод не осталось — база обязана стать пустой, а не остаться вчерашней");
            }
            finally
            {
                db.Dispose();
            }
        }

        [Test]
        public void Поле_на_удалённую_ноду_отвечает_ошибкой_а_не_нулевым_id()
        {
            var node = Node<AdvColdOnlyNodeSo>("Doomed");
            Rebuild();

            var field = new BlobchegIdRef<AdvRouter>(
                BlobchegBuild.IdsOf(node).Single(c => c.RouterName == AdvRouter.RouterName));

            Assert.That(field.IsSet, Is.True);
            Assert.That(field.Id.Index, Is.EqualTo(0u));

            Kill(node);

            Assert.That(field.IsSet, Is.False,
                "ассет уничтожен — поле обязано узнать это Unity'шным сравнением, а не ReferenceEquals");
            Assert.Throws<InvalidOperationException>(() => { _ = field.Id; },
                "повисшая ссылка обязана падать, а не отдавать id ноль");
        }
    }
}
