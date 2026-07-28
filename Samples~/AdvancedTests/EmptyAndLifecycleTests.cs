using System;
using System.IO;
using NUnit.Framework;
using Unity.Collections;

namespace Blobcheg.AdvancedTests
{
    /// <summary>
    /// Пусто, ноль и порядок вызовов: база без нод, запись без байтов, файл, которого нет, чтение до
    /// подъёма и после освобождения, и все способы соврать в декларации ноды.
    /// </summary>
    public sealed class EmptyAndLifecycleTests : AdvancedFixture
    {
        [Test]
        public void Домен_без_единой_ноды_ложится_файлом_и_любое_чтение_падает()
        {
            Node<AdvComboNodeSo>("Combo");
            Rebuild();

            Assert.That(File.Exists(FileOf("IAdvOther")), Is.True,
                "домен, из которого ушла последняя нода, обязан лечь пустым файлом, а не остаться вчерашним");

            var db = Other();
            try
            {
                Assert.That(db.IsCreated, Is.True);
                Assert.That(db.Length, Is.EqualTo(BlobchegFormat.HeaderSize), "в пустой базе нет ничего, кроме header'а");
                Assert.Throws<InvalidOperationException>(
                    () => { _ = db.Read<AdvOtherInfo>(BlobchegFormat.HeaderSize).V; },
                    "в пустой базе нет ни одной записи — чтение обязано падать, а не отдавать нули");
            }
            finally
            {
                db.Dispose();
            }
        }

        /// <summary>
        /// Адрес — единственная личность записи в этом формате, поэтому запись нулевой длины
        /// занимает в раскладке байт, а не ноль. Иначе позиция после неё не двигалась бы, следующее
        /// выравнивание возвращало бы тот же адрес, и две разные ноды получали бы один ref-ассет.
        /// </summary>
        [Test]
        public void Две_пустые_сырые_записи_обязаны_иметь_разные_адреса()
        {
            var a = Node<AdvRawNodeSo>("RawEmptyA");
            var b = Node<AdvRawNodeSo>("RawEmptyB");
            a.size = 0;
            b.size = 0;
            Dirty(a);
            Dirty(b);

            Rebuild();

            Assert.That(OffsetOf(a, "IAdvLoose"), Is.Not.EqualTo(OffsetOf(b, "IAdvLoose")),
                "адрес — единственная личность записи; две записи по одному адресу неразличимы");
        }

        [Test]
        public void Запись_нулевой_длины_не_обязана_читаться_как_структура()
        {
            var raw = Node<AdvRawNodeSo>("RawEmpty");
            var loose = Node<AdvLooseNodeSo>("Loose");
            raw.size = 0;
            Dirty(raw);
            Dirty(loose);

            Rebuild();

            var offset = OffsetOf(raw, "IAdvLoose");
            var db = Loose();
            try
            {
                Assert.Throws<InvalidOperationException>(
                    () => { _ = db.Read<AdvLooseBlock>(offset).A; },
                    "в записи ноль байт — 16-байтовая структура из неё взяться не может");
            }
            finally
            {
                db.Dispose();
            }
        }

        [Test]
        public void Пропавший_файл_базы_падает_на_подъёме_явно()
        {
            Node<AdvComboNodeSo>("Combo");
            Rebuild();

            File.Delete(FileOf("IAdvCombat"));

            var load = BlobchegTransport.Default.Read(AdvCombatDb.FileName, Allocator.Persistent);
            try
            {
                Assert.Throws<InvalidOperationException>(() => load.Complete(),
                    "файла нет — это ошибка подъёма, а не пустая база");
            }
            finally
            {
                load.Dispose();
            }
        }

        [Test]
        public void Файл_нулевой_длины_падает_на_подъёме()
        {
            Node<AdvComboNodeSo>("Combo");
            Rebuild();

            File.WriteAllBytes(FileOf("IAdvCombat"), Array.Empty<byte>());

            var load = BlobchegTransport.Default.Read(AdvCombatDb.FileName, Allocator.Persistent);
            try
            {
                Assert.Throws<InvalidOperationException>(() => load.Complete(),
                    "нулевой файл короче header'а — подниматься нечему");
            }
            finally
            {
                load.Dispose();
            }
        }

        [Test]
        public void Acquire_до_готовности_падает()
        {
            Node<AdvComboNodeSo>("Combo");
            Rebuild();

            var load = BlobchegTransport.Default.Read(AdvCombatDb.FileName, Allocator.Persistent);
            try
            {
                Assert.Throws<InvalidOperationException>(() => load.Acquire(),
                    "буфер забирают после готовности, а не вместо ожидания");
            }
            finally
            {
                load.Dispose();
            }
        }

        [Test]
        public void Чтение_из_неподнятой_базы_падает()
        {
            var db = default(AdvCombatDb);

            Assert.That(db.IsCreated, Is.False);
            Assert.Throws<InvalidOperationException>(
                () => { _ = db.Read<AdvGun>(BlobchegFormat.HeaderSize).Rpm; },
                "база не поднята — чтение обязано падать, а не ходить по нулевому адресу");
        }

        [Test]
        public void Чтение_после_Dispose_падает()
        {
            var node = Node<AdvComboNodeSo>("Combo");
            Rebuild();

            var offset = OffsetOf(node, "IAdvCombat");
            var db = Combat();
            Assert.That(db.Read<AdvGun>(offset).Rpm, Is.EqualTo(600), "до освобождения читается");

            db.Dispose();

            Assert.Throws<InvalidOperationException>(
                () => { _ = db.Read<AdvGun>(offset).Rpm; },
                "освобождённая база обязана падать, а не читать освобождённую память");
        }

        [Test]
        public void Повторный_Dispose_ничего_не_ломает()
        {
            Node<AdvComboNodeSo>("Combo");
            Rebuild();

            var db = Combat();
            db.Dispose();

            Assert.DoesNotThrow(() => db.Dispose(), "второй Dispose — идемпотентный no-op, а не двойное освобождение");
            Assert.That(db.IsCreated, Is.False);
        }

        /// <summary>
        /// ПРИНЯТЫЙ ПРЕДЕЛ, а не находка. База — это value-структура с владеющим указателем, и
        /// такой она сделана нарочно: её кладут в <c>IComponentData</c> и копируют каждым
        /// <c>GetSingleton</c>, каждой передачей в джобу, каждым присваиванием. Версия владения
        /// (safety handle, как у NativeArray) требует ячейки, которая переживёт освобождение самой
        /// памяти, — то есть либо утечки, либо реестра, недоступного из Бёрста. Ни то, ни другое в
        /// поле компонента не помещается.
        ///
        /// Поэтому контракт прямой: владелец у базы один — тот, кто её поднял (у Entities это
        /// выпущенная кодогеном бут-система). Остальные экземпляры — виды, живущие ровно столько,
        /// сколько живёт владелец. Тест закрепляет этот контракт, чтобы он не выглядел недосмотром.
        /// </summary>
        [Test]
        public void Копия_базы_это_вид_а_не_владелец()
        {
            Node<AdvComboNodeSo>("Combo");
            Rebuild();

            var db = Combat();
            var copy = db;

            Assert.That(copy.IsCreated, Is.True, "пока владелец жив, вид работает как сам владелец");
            Assert.That(copy.Length, Is.EqualTo(db.Length));

            db.Dispose();

            Assert.That(db.IsCreated, Is.False, "владелец о своей смерти знает");
            Assert.That(copy.IsCreated, Is.True,
                "а вид — нет: у указателя в обычной структуре версии владения не бывает. " +
                "Отсюда правило пакета: Dispose зовёт тот, кто поднял, и ровно один раз");

            // Ни читать через copy, ни звать по ней Dispose здесь НЕЛЬЗЯ: первое — чтение
            // освобождённой памяти, второе — двойное освобождение. Оба уронили бы редактор вместе с
            // отчётом, а показать надо ровно то, что уже показано.
        }

        [Test]
        public void Пересборка_поверх_живого_хендла_не_подменяет_данные()
        {
            var node = Node<AdvComboNodeSo>("Combo");
            node.rpm = 111;
            Dirty(node);
            Rebuild();

            var offset = OffsetOf(node, "IAdvCombat");
            var db = Combat();
            try
            {
                node.rpm = 222;
                Dirty(node);
                Rebuild();

                Assert.That(db.Read<AdvGun>(offset).Rpm, Is.EqualTo(111),
                    "поднятая база — снимок; пересборка файла на диске не имеет права шевелить чужой буфер");
            }
            finally
            {
                db.Dispose();
            }
        }

        [Test]
        public void Нода_без_единого_домена_падает()
        {
            var node = Node<AdvNoOutTypesNodeSo>("NoOut");

            var thrown = Assert.Throws<InvalidOperationException>(() => Rebuild(),
                "нода без OutTypes ничего не значит — это ошибка, а не тихий пропуск");
            StringAssert.Contains("OutTypes", thrown.Message);

            Kill(node);
        }

        [Test]
        public void Нода_с_необъявленным_доменом_падает()
        {
            var node = Node<AdvUndeclaredNodeSo>("Undeclared");

            Assert.Throws<InvalidOperationException>(() => Rebuild(),
                "домен без базы — это не домен; такой файл некому читать");

            Kill(node);
        }

        [Test]
        public void Нода_объявившая_домен_и_ничего_не_написавшая_падает()
        {
            var node = Node<AdvSilentNodeSo>("Silent");

            var thrown = Assert.Throws<InvalidOperationException>(() => Rebuild(),
                "объявила и не написала — расхождение декларации с фактом");
            StringAssert.Contains("OutTypes", thrown.Message);

            Kill(node);
        }

        [Test]
        public void Нода_пишущая_мимо_своих_OutTypes_падает()
        {
            var node = Node<AdvStrayNodeSo>("Stray");

            Assert.Throws<InvalidOperationException>(() => Rebuild(),
                "запись в домен, которого нода не объявляла, — это дыра в раздаче id");

            Kill(node);
        }

        [Test]
        public void Нода_пишущая_в_один_домен_дважды_падает()
        {
            var node = Node<AdvDoubleNodeSo>("Double");

            Assert.Throws<InvalidOperationException>(() => Rebuild(),
                "одна нода — одна запись в базе; иначе у ноды два адреса и id перестаёт быть адресом");

            Kill(node);
        }

        [Test]
        public void Падение_ноды_не_оставляет_полусобранный_файл()
        {
            var good = Node<AdvComboNodeSo>("Good");
            good.rpm = 900;
            Dirty(good);
            Rebuild();

            var before = Bytes("IAdvCombat");

            var bad = Node<AdvThrowNodeSo>("Boom");
            Assert.Throws<InvalidOperationException>(() => Rebuild());

            Assert.That(Bytes("IAdvCombat"), Is.EqualTo(before),
                "сборка либо прошла целиком, либо не тронула файл: полусобранная база выглядит рабочей и врёт");

            Kill(bad);
        }

        [Test]
        public void Пересборка_идемпотентна_на_смешанном_наборе()
        {
            Node<AdvComboNodeSo>("Combo");
            Node<AdvColdOnlyNodeSo>("Cold");
            Node<AdvArmorNodeSo>("Armor");
            Node<AdvLooseNodeSo>("Loose");
            Node<AdvOtherNodeSo>("Other");
            var raw = Node<AdvRawNodeSo>("Raw");
            raw.size = 24;
            Dirty(raw);

            Rebuild();

            var again = Rebuild();
            Assert.That(again.Changed, Is.False,
                $"вторая пересборка не имеет права тронуть ничего, отчёт: {again}");
        }
    }
}
