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
        // BUG: сохранённый id после удаления соседней ноды молча начинает указывать на ДРУГУЮ ноду.
        // Ожидалось: id, положенный в сейв (или в компонент, или на провод), обязан либо указывать
        // на ту же ноду, либо стать явно недействительным. Молчаливая подмена — самая дорогая из
        // возможных: игрок открывает сохранение и получает не тот предмет, без единой ошибки.
        // Корень: id — позиция строки в списке нод роутера, отсортированном по GUID
        // (BlobchegIdTable.Assign), и удаление ноды сдвигает ВСЕ последующие позиции. Носители
        // BlobchegIdSo при пересборке перевыставляются, поэтому путь через ассет остаётся верным, а
        // любой уже сохранённый uint — нет. В файле роутера нет поколения (в прологе Count,
        // DomainCount, LayoutHash и оффсеты массивов, и больше ничего), поэтому отличить «id из
        // прошлой раскладки» от «id из этой» нечем даже теоретически.
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

            var byId = created.OrderBy(n => IdOf(n, AdvRouter.RouterName).Value).ToArray();
            for (var i = 0; i < byId.Length; i++)
            {
                byId[i].tier = 100 * (i + 1);
                Dirty(byId[i]);
            }

            Rebuild();

            // Так делает потребитель: взял id и положил его в сейв. Дальше он видит только число.
            var saved = IdOf(byId[1], AdvRouter.RouterName);
            Assert.That(saved.Value, Is.EqualTo(1u));

            Kill(byId[0]);
            Rebuild();

            var router = Router();
            var cold = Cold();
            try
            {
                Assert.That(router.Count, Is.EqualTo(2));
                Assert.That(cold.Read<AdvColdInfo>(router.GetCold(saved)).Tier, Is.EqualTo(200),
                    "сохранённый id обязан вести к своей ноде либо честно умереть, а не привести к соседней");
            }
            finally
            {
                router.Dispose();
                cold.Dispose();
            }
        }

        // BUG: закешированный оффсет после появления записи ДРУГОГО типа молча указывает на чужую
        // запись.
        // Ожидалось: протухший адрес обязан быть отбит при чтении.
        // Корень: тот же, что у «Оффсет_из_чужой_базы_не_читается_в_этой» — адрес в этом формате это
        // голое число без личности и без поколения. Раскладка BlobchegWriter.BuildOrder группирует
        // записи по FullName типа, поэтому появление ноды с типом, чьё имя сортируется раньше,
        // сдвигает ВСЕ записи следующих типов. Ревизия у записи есть (BlobchegRefSo.revision), но
        // она внутренняя и участвует только в решении «переписывать ли ассет», а до рантайма не
        // доезжает вовсе.
        [Test]
        public void Закешированный_оффсет_после_появления_чужой_записи_не_врёт()
        {
            var gun = Node<AdvComboNodeSo>("Combo");
            gun.rpm = 999;
            Dirty(gun);
            Rebuild();

            // Потребитель закешировал адрес у себя: положил в компонент, в статик, в сейв — неважно.
            var cached = OffsetOf(gun, "IAdvCombat");

            // Появилась ЧУЖАЯ нода с типом, имя которого сортируется раньше AdvGun.
            Node<AdvArmorNodeSo>("Armor");
            Rebuild();

            Assert.That(OffsetOf(gun, "IAdvCombat"), Is.Not.EqualTo(cached),
                "сама раскладка при этом обязана была поехать — иначе тест ничего не проверяет");

            var db = Combat();
            try
            {
                Assert.That(db.Read<AdvGun>(cached).Rpm, Is.EqualTo(999),
                    "старый адрес указывает на чужую запись, и об этом никто не сказал");
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

        [Test]
        public void Проверка_id_на_ноль_не_является_проверкой()
        {
            var created = new[]
            {
                Node<AdvColdOnlyNodeSo>("A"),
                Node<AdvColdOnlyNodeSo>("B"),
            };

            Rebuild();

            var zero = created.Select(n => IdOf(n, AdvRouter.RouterName)).Count(id => id.Value == 0);
            Assert.That(zero, Is.EqualTo(1),
                "строка ноль — обычная валидная нода, поэтому привычное 'if (id != 0)' не проверяет ничего; " +
                "признак незаполненности один — BlobchegId.None");

            Assert.That(BlobchegId.None.IsValid, Is.False);
            Assert.That(new BlobchegId(0).IsValid, Is.True);
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
            Assert.That(field.Id.Value, Is.EqualTo(0u));

            Kill(node);

            Assert.That(field.IsSet, Is.False,
                "ассет уничтожен — поле обязано узнать это Unity'шным сравнением, а не ReferenceEquals");
            Assert.Throws<InvalidOperationException>(() => { _ = field.Id; },
                "повисшая ссылка обязана падать, а не отдавать id ноль");
        }
    }
}
