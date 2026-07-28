using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Unity.Collections;

namespace Blobcheg.AdvancedTests
{
    /// <summary>
    /// Границы адреса и границы типа: за концом файла, внутрь header'а, мимо выравнивания, ровно на
    /// последней записи, ровно на 64 базах роутера — и что происходит, когда запись читают типом
    /// того же размера, но не тем.
    /// </summary>
    public sealed class BoundaryAndTypeTests : AdvancedFixture
    {
        [Test]
        public void Оффсет_за_концом_файла_падает()
        {
            var node = Node<AdvComboNodeSo>("Combo");
            Rebuild();

            var db = Combat();
            try
            {
                var past = (uint)BlobchegFormat.AlignUp(db.Length);

                Assert.Throws<InvalidOperationException>(() => { _ = db.Read<AdvGun>(past).Rpm; },
                    "адрес ровно на конце файла — это уже не запись");

                Assert.Throws<InvalidOperationException>(() => { _ = db.Read<AdvGun>(past + 16u).Rpm; });
                Assert.Throws<InvalidOperationException>(() => { _ = db.Read<AdvGun>(0xFFFFFFF0u).Rpm; },
                    "адрес у потолка uint не имеет права свернуться в валидный");

                Assert.That(db.Read<AdvGun>(OffsetOf(node, "IAdvCombat")).Rpm, Is.EqualTo(600),
                    "и при этом настоящий адрес обязан читаться");
            }
            finally
            {
                db.Dispose();
            }
        }

        [Test]
        public void Оффсет_мимо_выравнивания_падает()
        {
            var node = Node<AdvComboNodeSo>("Combo");
            Rebuild();

            var offset = OffsetOf(node, "IAdvCombat");
            var db = Combat();
            try
            {
                Assert.Throws<InvalidOperationException>(() => { _ = db.Read<AdvGun>(offset + 1u).Rpm; },
                    "начало записи всегда кратно 16 — всё остальное не начало записи");
                Assert.Throws<InvalidOperationException>(() => { _ = db.Read<AdvGun>(offset + 15u).Rpm; });
            }
            finally
            {
                db.Dispose();
            }
        }

        [Test]
        public void Оффсет_внутрь_header_падает()
        {
            Node<AdvComboNodeSo>("Combo");
            Rebuild();

            var db = Combat();
            try
            {
                Assert.Throws<InvalidOperationException>(() => { _ = db.Read<AdvGun>(0).Rpm; },
                    "нулевой адрес — это header, а не запись; он же значение неинициализированного поля");
                Assert.Throws<InvalidOperationException>(() => { _ = db.Read<AdvGun>(16).Rpm; });
            }
            finally
            {
                db.Dispose();
            }
        }

        [Test]
        public void Тип_крупнее_записи_не_лезет_в_буфер()
        {
            var node = Node<AdvComboNodeSo>("Combo");
            Rebuild();

            var offset = OffsetOf(node, "IAdvCombat");
            var db = Combat();
            try
            {
                Assert.Throws<InvalidOperationException>(() => { _ = db.Read<AdvFat>(offset).C0.A; },
                    "512-байтовая структура из 8-байтовой записи взяться не может");
            }
            finally
            {
                db.Dispose();
            }
        }

        [Test]
        public void Последняя_запись_читается_целиком()
        {
            var fat = Node<AdvFatNodeSo>("Fat");
            fat.first = 3.25;
            fat.last = -7.75;
            Dirty(fat);
            Rebuild();

            var offset = OffsetOf(fat, "IAdvCombat");
            var db = Combat();
            try
            {
                Assert.That(offset + 512u, Is.LessThanOrEqualTo((uint)db.Length),
                    "запись обязана целиком помещаться в файл");

                ref readonly var record = ref db.Read<AdvFat>(offset);
                Assert.That(record.C0.A, Is.EqualTo(3.25), "первые 8 байт последней записи");
                Assert.That(record.C7.H, Is.EqualTo(-7.75), "и последние её 8 байт тоже — до самого конца файла");
            }
            finally
            {
                db.Dispose();
            }
        }

        [Test]
        public void Однобайтовая_запись_не_сдвигает_соседей()
        {
            var tiny = Node<AdvRawNodeSo>("Tiny");
            var big = Node<AdvRawNodeSo>("Big");
            tiny.size = 1;
            tiny.seed = 0x11;
            big.size = 40;
            big.seed = 0x20;
            Dirty(tiny);
            Dirty(big);

            Rebuild();

            var tinyAt = OffsetOf(tiny, "IAdvLoose");
            var bigAt = OffsetOf(big, "IAdvLoose");

            Assert.That(tinyAt, Is.Not.EqualTo(bigAt));
            Assert.That(Math.Abs((long)tinyAt - bigAt), Is.GreaterThanOrEqualTo(16),
                "между началами двух записей всегда есть выравнивание");

            var file = Bytes("IAdvLoose");
            Assert.That(file[(int)tinyAt], Is.EqualTo((byte)0x11), "однобайтовая запись лежит там, где обещал её адрес");
            Assert.That(file[(int)bigAt], Is.EqualTo((byte)0x20));
            Assert.That(file[(int)bigAt + 39], Is.EqualTo((byte)(0x20 + 39)), "и сосед не обрезан");
        }

        [Test]
        public void Роутер_без_единой_базы_отбивается()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => BlobchegRouterFormat.MaskWidthFor(0),
                "роутеру без баз нечего маршрутизировать");

            Assert.Throws<ArgumentOutOfRangeException>(
                () => BlobchegRouterWriter.Open(Scratch, "AdvEmptyRouter", 0, 0),
                "писатель роутера обязан отбить это на входе, а не собрать файл, который никто не поднимет");
        }

        [Test]
        public void Больше_шестидесяти_четырёх_баз_отбивается()
        {
            Assert.Throws<ArgumentOutOfRangeException>(
                () => BlobchegRouterFormat.MaskWidthFor(BlobchegRouterFormat.MaxDomains + 1),
                "маска шире 64 бит не бывает — это обязана быть ошибка, а не потерянная база");

            Assert.Throws<ArgumentOutOfRangeException>(
                () => BlobchegRouterWriter.Open(Scratch, "AdvWideRouter", BlobchegRouterFormat.MaxDomains + 1, 0));
        }

        [Test]
        public void Роутер_ровно_на_шестидесяти_четырёх_базах_живёт()
        {
            const int count = BlobchegRouterFormat.MaxDomains;

            var pairs = Enumerable.Range(0, count)
                .Select(i => new KeyValuePair<string, string>("Domain" + i.ToString("D2"), "member" + i))
                .ToList();

            var width = BlobchegRouterFormat.MaskWidthFor(count);
            var layout = BlobchegRouterFormat.LayoutHash(pairs, width);

            var writer = BlobchegRouterWriter.Open(Scratch, "Adv64Router", count, layout);
            writer.Append("edges", new[]
            {
                new BlobchegRouterCell(0, 0x100),
                new BlobchegRouterCell(count - 1, 0x200),
            });
            writer.Append("empty", Array.Empty<BlobchegRouterCell>());
            writer.Flush();

            var blob = new BlobchegRouterBlob(
                BlobchegBuffer.From(File.ReadAllBytes(writer.FilePath), Allocator.Persistent),
                "Adv64Router", count, layout);

            try
            {
                var edges = blob.Get(new BlobchegId(0));
                Assert.That(edges.Has(0), Is.True);
                Assert.That(edges.Has(count - 1), Is.True, "старший бит маски — тот, на котором ломается popcount");
                Assert.That(edges.Offset(0), Is.EqualTo(0x100u));
                Assert.That(edges.Offset(count - 1), Is.EqualTo(0x200u));
                Assert.That(edges.Has(1), Is.False);

                var empty = blob.Get(new BlobchegId(1));
                Assert.That(empty.Mask, Is.EqualTo(0ul), "строка без единой базы допустима");
                Assert.Throws<InvalidOperationException>(() => empty.Offset(0),
                    "но оффсета у неё нет, и сентинела вместо него быть не может");
                Assert.That(empty.TryOffset(0, out _), Is.False);
            }
            finally
            {
                blob.Dispose();
            }
        }

        [Test]
        public void Id_за_последней_строкой_падает()
        {
            Node<AdvComboNodeSo>("Combo");
            Node<AdvColdOnlyNodeSo>("Cold");
            Rebuild();

            var router = Router();
            try
            {
                Assert.That(router.Count, Is.EqualTo(2));

                Assert.Throws<InvalidOperationException>(() => router.Get(new BlobchegId((uint)router.Count)),
                    "строки с таким номером нет — это ошибка, а не пустая строка");
                Assert.Throws<InvalidOperationException>(() => router.Get(new BlobchegId(uint.MaxValue - 1)));
                Assert.Throws<InvalidOperationException>(() => router.Get(BlobchegId.None));

                Assert.That(router.TryGet(new BlobchegId((uint)router.Count), out _), Is.False);
                Assert.That(router.TryGet(BlobchegId.None, out _), Is.False);
            }
            finally
            {
                router.Dispose();
            }
        }

        // BUG: запись читается типом-близнецом того же размера молча, и наружу едут чужие байты.
        // Ожидалось: чтение записи не тем типом обязано быть отбито явно.
        // Корень: BlobchegBlob.CheckType — единственная проверка типа — висит на
        // [Conditional("BLOBCHEG_DEBUG")], а сама debug-секция пишется только при том же дефайне.
        // В обычном редакторе и в билде дефайна нет вообще, поэтому проверка не вызывается ни разу:
        // и пишущая, и читающая стороны молчат. Констрейнт домена в сгенерированном Read<T> ловит
        // только ЧУЖОЙ домен — близнец внутри своего домена проходит компилятор насквозь.
        [Test]
        public void Близнец_того_же_размера_обязан_быть_отбит()
        {
            var gun = Node<AdvComboNodeSo>("Combo");
            gun.ammo = 12.5f;
            gun.rpm = 777;
            Dirty(gun);
            Rebuild();

            var offset = OffsetOf(gun, "IAdvCombat");
            var db = Combat();
            try
            {
                Assert.Throws<InvalidOperationException>(() => { _ = db.Read<AdvGunTwin>(offset).Rpm; },
                    "по этому адресу лежит AdvGun; отдавать его как AdvGunTwin нельзя даже при равном размере");
            }
            finally
            {
                db.Dispose();
            }
        }

        [Test]
        public void Смесь_bool_enum_и_выравнивания_переживает_круг()
        {
            var node = Node<AdvMixedNodeSo>("Mixed");
            node.flag = true;
            node.tier = AdvTier.High;
            node.weight = -1234.5678;
            node.small = -31000;
            Dirty(node);
            Rebuild();

            var db = Combat();
            try
            {
                ref readonly var record = ref db.Read<AdvMixed>(OffsetOf(node, "IAdvCombat"));

                Assert.That(record.Flag, Is.True, "bool переживает круг как есть, без превращения в 0/1 наугад");
                Assert.That(record.Tier, Is.EqualTo(AdvTier.High));
                Assert.That(record.Weight, Is.EqualTo(-1234.5678).Within(0.0));
                Assert.That(record.Small, Is.EqualTo((short)-31000));
            }
            finally
            {
                db.Dispose();
            }
        }

        [Test]
        public void Сырой_путь_и_типизированный_кладут_одни_и_те_же_байты()
        {
            var typed = Node<AdvLooseNodeSo>("Typed");
            var raw = Node<AdvRawNodeSo>("Raw");
            typed.a = 0x0102030405060708L;
            typed.b = 0x1112131415161718L;
            raw.size = 16;
            raw.seed = 0;
            Dirty(typed);
            Dirty(raw);

            Rebuild();

            var file = Bytes("IAdvLoose");
            var typedAt = OffsetOf(typed, "IAdvLoose");
            var rawAt = OffsetOf(raw, "IAdvLoose");

            Assert.That(BitConverter.ToInt64(file, (int)typedAt), Is.EqualTo(typed.a),
                "типизированная запись — это ровно байты структуры, little-endian, без обёрток");

            var db = Loose();
            try
            {
                Assert.That(db.Read<AdvLooseBlock>(typedAt).B, Is.EqualTo(typed.b));

                // Сырая запись того же размера читается тем же способом: типа у неё нет, но байты те же.
                Assert.That(file[(int)rawAt], Is.EqualTo((byte)0));
                Assert.That(file[(int)rawAt + 15], Is.EqualTo((byte)15));
            }
            finally
            {
                db.Dispose();
            }
        }
    }
}
