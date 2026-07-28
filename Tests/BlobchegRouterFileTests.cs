using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using Unity.Collections;

namespace Blobcheg.Tests
{
    /// <summary>
    /// Файл роутера сам по себе: раскладка, popcount-лукап, границы. Ассетов и кодогена здесь нет —
    /// доказывается ровно бинарник и чтение по нему.
    /// </summary>
    public sealed class BlobchegRouterFileTests
    {
        string _dir;

        [SetUp]
        public void SetUp()
        {
            _dir = Path.Combine(Path.GetTempPath(), "blobcheg-router-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_dir))
                Directory.Delete(_dir, true);
        }

        static List<BlobchegRouterCell> Row(params (int Bit, uint Offset)[] cells)
        {
            var list = new List<BlobchegRouterCell>();
            foreach (var cell in cells)
                list.Add(new BlobchegRouterCell(cell.Bit, cell.Offset));

            return list;
        }

        static ulong HashOf(int domainCount)
        {
            var pairs = new List<KeyValuePair<string, string>>();
            for (var i = 0; i < domainCount; i++)
                pairs.Add(new KeyValuePair<string, string>("Domain" + i, "member" + i));

            return BlobchegRouterFormat.LayoutHash(pairs, BlobchegRouterFormat.MaskWidthFor(domainCount));
        }

        byte[] Bytes(string name) => File.ReadAllBytes(Path.Combine(_dir, BlobchegNaming.FileName(name)));

        BlobchegRouterBlob Load(string name, int domainCount, ulong layoutHash)
            => new BlobchegRouterBlob(BlobchegBuffer.From(Bytes(name), Allocator.Persistent), name, domainCount, layoutHash);

        /// <summary>Подъём, который обязан бросить: буфер освобождаем сами — владение к нему не перешло.</summary>
        void RequireThrows(string name, int domainCount, ulong layoutHash, string what)
        {
            var buffer = BlobchegBuffer.From(Bytes(name), Allocator.Persistent);
            try
            {
                var thrown = Assert.Throws<InvalidOperationException>(
                    () => new BlobchegRouterBlob(buffer, name, domainCount, layoutHash));

                StringAssert.Contains(what, thrown.Message);
            }
            finally
            {
                buffer.Dispose();
            }
        }

        [Test]
        public void Строка_отдаёт_оффсеты_по_битам_а_не_по_порядку_в_файле()
        {
            const int domains = 8;
            var hash = HashOf(domains);

            var writer = BlobchegRouterWriter.Open(_dir, "R", domains, hash);
            // Ячейки нарочно приходят вперемешку: в файле они обязаны лечь по возрастанию бита.
            writer.Append("a", Row((5, 500), (0, 100), (3, 300)));
            writer.Append("b", Row());
            writer.Append("c", Row((7, 700)));
            writer.Flush();

            var router = Load("R", domains, hash);
            try
            {
                var a = router.Get(router.IdAt(0));
                Assert.That(a.Offset(0), Is.EqualTo(100u));
                Assert.That(a.Offset(3), Is.EqualTo(300u));
                Assert.That(a.Offset(5), Is.EqualTo(500u));
                Assert.That(a.Mask, Is.EqualTo(0b101001ul));

                var b = router.Get(router.IdAt(1));
                Assert.That(b.Mask, Is.Zero, "нода могла войти в роутер, ничего не написав в его базы");
                Assert.That(b.Has(0), Is.False);
                Assert.Throws<InvalidOperationException>(() => b.Offset(0));
                Assert.That(b.TryOffset(0, out _), Is.False);

                var c = router.Get(router.IdAt(2));
                Assert.That(c.Offset(7), Is.EqualTo(700u));
                Assert.That(c.Has(6), Is.False);

                Assert.That(router.Count, Is.EqualTo(3));
            }
            finally
            {
                router.Dispose();
            }
        }

        [TestCase(8, 7)]
        [TestCase(16, 15)]
        [TestCase(32, 31)]
        [TestCase(64, 63)]
        public void Маска_любой_ширины_читается_включая_старший_бит(int domains, int top)
        {
            var hash = HashOf(domains);
            var writer = BlobchegRouterWriter.Open(_dir, "R", domains, hash);
            writer.Append("a", Row((0, 16), (top, 32)));
            writer.Flush();

            var router = Load("R", domains, hash);
            try
            {
                var row = router.Get(router.IdAt(0));
                Assert.That(row.Offset(0), Is.EqualTo(16u));
                Assert.That(row.Offset(top), Is.EqualTo(32u), "старший бит лежит в маске выбранной ширины");
            }
            finally
            {
                router.Dispose();
            }
        }

        [Test]
        public void Неизвестный_id_бросает_а_TryGet_отвечает_false()
        {
            const int domains = 4;
            var hash = HashOf(domains);

            var writer = BlobchegRouterWriter.Open(_dir, "R", domains, hash);
            writer.Append("a", Row((1, 48)));
            writer.Flush();

            var router = Load("R", domains, hash);
            try
            {
                Assert.Throws<InvalidOperationException>(() => router.Get(router.IdAt(1)));
                Assert.Throws<InvalidOperationException>(() => router.Get(BlobchegId.None));
                Assert.That(router.TryGet(router.IdAt(1), out _), Is.False);
                Assert.That(router.TryGet(BlobchegId.None, out _), Is.False);
                Assert.That(router.TryGet(router.IdAt(0), out _), Is.True);
            }
            finally
            {
                router.Dispose();
            }
        }

        [Test]
        public void Файл_под_другой_набор_баз_не_поднимается()
        {
            const int domains = 4;
            var writer = BlobchegRouterWriter.Open(_dir, "R", domains, HashOf(domains));
            writer.Append("a", Row((0, 48)));
            writer.Flush();

            RequireThrows("R", domains, HashOf(domains) ^ 1, "другой набор баз");
            RequireThrows("R", domains + 1, HashOf(domains), "баз");
        }

        [Test]
        public void База_и_роутер_не_путаются_местами()
        {
            var domain = BlobchegWriter.Open(_dir, "D");
            domain.Append(new BlobchegRecord("T", "k", 0, "n", new byte[16]));
            domain.Flush();

            var router = BlobchegRouterWriter.Open(_dir, "R", 2, HashOf(2));
            router.Append("a", Row((0, 64)));
            router.Flush();

            RequireThrows("D", 2, HashOf(2), "роутер");

            var buffer = BlobchegBuffer.From(Bytes("R"), Allocator.Persistent);
            try
            {
                var asBase = Assert.Throws<InvalidOperationException>(() => new BlobchegBlob(buffer, "R"));
                StringAssert.Contains("базу", asBase.Message);
            }
            finally
            {
                buffer.Dispose();
            }
        }

        [Test]
        public void Файл_чужого_роутера_не_поднимается_под_этим_именем()
        {
            const int domains = 2;
            var hash = HashOf(domains);

            var writer = BlobchegRouterWriter.Open(_dir, "R", domains, hash);
            writer.Append("a", Row((0, 48)));
            writer.Flush();

            // Файлы переставили местами: содержимое целое, целостность сходится, а роутер не тот.
            File.Copy(Path.Combine(_dir, BlobchegNaming.FileName("R")),
                Path.Combine(_dir, BlobchegNaming.FileName("Alien")));

            RequireThrows("Alien", domains, hash, "другого роутера");
        }

        [Test]
        public void Id_чужого_роутера_отбивается_тегом()
        {
            const int domains = 2;
            var hash = HashOf(domains);

            var mine = BlobchegRouterWriter.Open(_dir, "R", domains, hash);
            mine.Append("a", Row((0, 48)));
            mine.Flush();

            var theirs = BlobchegRouterWriter.Open(_dir, "Other", domains, hash);
            theirs.Append("a", Row((0, 48)));
            theirs.Flush();

            var router = Load("R", domains, hash);
            var other = Load("Other", domains, hash);
            try
            {
                var alien = other.IdAt(0);
                Assert.That(alien.Index, Is.EqualTo(router.IdAt(0).Index), "строка та же — тег разный");
                Assert.That(alien, Is.Not.EqualTo(router.IdAt(0)));

                Assert.Throws<InvalidOperationException>(() => router.Get(alien),
                    "id соседнего роутера попадает в диапазон этого — отличает их только тег");
                Assert.That(router.TryGet(alien, out _), Is.False);
            }
            finally
            {
                router.Dispose();
                other.Dispose();
            }
        }

        [Test]
        public void Дефолтный_id_не_резолвится()
        {
            const int domains = 2;
            var hash = HashOf(domains);

            var writer = BlobchegRouterWriter.Open(_dir, "R", domains, hash);
            writer.Append("a", Row((0, 48)));
            writer.Flush();

            Assert.That(default(BlobchegId).IsValid, Is.False, "нулём инициализированное поле — это «не задано»");

            var router = Load("R", domains, hash);
            try
            {
                Assert.Throws<InvalidOperationException>(() => router.Get(default),
                    "иначе забытое поле молча приводило бы к первой ноде роутера");
                Assert.That(router.TryGet(default, out _), Is.False);
            }
            finally
            {
                router.Dispose();
            }
        }

        [Test]
        public void Одинаковое_содержимое_не_переписывает_файл()
        {
            const int domains = 2;
            var hash = HashOf(domains);

            var first = BlobchegRouterWriter.Open(_dir, "R", domains, hash);
            first.Append("a", Row((0, 48)));
            first.Flush();
            Assert.That(first.FileChanged, Is.True, "файла ещё не было");

            var again = BlobchegRouterWriter.Open(_dir, "R", domains, hash);
            again.Append("a", Row((0, 48)));
            again.Flush();
            Assert.That(again.FileChanged, Is.False, "то же содержимое — файл не трогаем, иначе всё перепечётся");
            Assert.That(again.ContentHash, Is.EqualTo(first.ContentHash));
        }

        [Test]
        public void Дважды_указанная_база_и_бит_за_потолком_бросают()
        {
            var writer = BlobchegRouterWriter.Open(_dir, "R", 4, HashOf(4));

            Assert.Throws<InvalidOperationException>(() => writer.Append("a", Row((1, 16), (1, 32))));
            Assert.Throws<ArgumentOutOfRangeException>(() => writer.Append("b", Row((4, 16))));
            Assert.Throws<ArgumentOutOfRangeException>(() => BlobchegRouterFormat.MaskWidthFor(65));
        }

        [Test]
        public void Отладочный_контур_называет_ноду_по_id()
        {
            const int domains = 2;
            var hash = HashOf(domains);

            var writer = BlobchegRouterWriter.Open(_dir, "R", domains, hash);
            writer.Append("Пистолет", Row((0, 48)));
            writer.Append("Броня", Row((1, 64)));
            writer.Flush(true);

            var router = Load("R", domains, hash);
            try
            {
                Assert.That(router.HasDebug, Is.True);
                Assert.That(router.Describe(router.IdAt(0)), Is.EqualTo("Пистолет"));
                Assert.That(router.Describe(router.IdAt(1)), Is.EqualTo("Броня"));
            }
            finally
            {
                router.Dispose();
            }
        }
    }
}
