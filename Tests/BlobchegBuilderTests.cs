using System;
using System.IO;
using Blobcheg.Authoring;
using NUnit.Framework;
using Unity.Collections;

namespace Blobcheg.Tests
{
    /// <summary>
    /// Билдер записи сам по себе: раскладка хвоста, само-относительные оффсеты, лимиты. Полный
    /// круг — собранные билдером байты поднимаются базой и читаются через <see cref="BlobchegArray{T}"/>.
    /// </summary>
    public sealed class BlobchegBuilderTests
    {
        string _dir;

        [SetUp]
        public void SetUp()
        {
            _dir = Path.Combine(Path.GetTempPath(), "blobcheg-builder-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_dir))
                Directory.Delete(_dir, true);
        }

        static BlobchegBuilder<T> Open<T>(Action<byte[]> sink) where T : unmanaged
            => new BlobchegBuilder<T>("node", sink);

        [Test]
        public void Оффсет_считается_от_адреса_поля()
        {
            byte[] built = null;
            var b = Open<TestCurve>(bytes => built = bytes);
            b.Root.Levels = 7;
            var values = b.Allocate(ref b.Root.Values, 3);
            values[0] = 1.5f;
            values[1] = 2.5f;
            values[2] = 3.5f;
            b.End();

            // Голова 12 байт, хвост float'ов с 12; поле массива лежит на 4 → оффсет 8.
            Assert.That(built.Length, Is.EqualTo(24));
            Assert.That(BitConverter.ToInt32(built, 0), Is.EqualTo(7));
            Assert.That(BitConverter.ToInt32(built, 4), Is.EqualTo(8));
            Assert.That(BitConverter.ToInt32(built, 8), Is.EqualTo(3));
            Assert.That(BitConverter.ToSingle(built, 12), Is.EqualTo(1.5f));
            Assert.That(BitConverter.ToSingle(built, 20), Is.EqualTo(3.5f));
        }

        [Test]
        public void Пустой_массив_оставляет_поле_нулём_и_не_растит_запись()
        {
            byte[] built = null;
            var b = Open<TestCurve>(bytes => built = bytes);
            b.Allocate(ref b.Root.Values, 0);
            b.End();

            Assert.That(built.Length, Is.EqualTo(12), "чанка под пустоту нет");
            Assert.That(BitConverter.ToInt32(built, 4), Is.Zero);
            Assert.That(BitConverter.ToInt32(built, 8), Is.Zero);
        }

        [Test]
        public void Собранная_билдером_запись_читается_базой()
        {
            byte[] built = null;
            var b = Open<TestTable>(bytes => built = bytes);
            var rows = b.Allocate(ref b.Root.Rows, 2);
            var first = b.Allocate(ref rows[0].Cells, 2);
            first[0] = 10;
            first[1] = 20;
            var second = b.Allocate(ref rows[1].Cells, 1);
            second[0] = 30;
            b.End();

            var writer = BlobchegWriter.Open(_dir, "BuilderDomain");
            var ticket = writer.Append(new BlobchegRecord(typeof(TestTable).FullName, "a", 0, "node", built));
            writer.Flush();

            var file = File.ReadAllBytes(Path.Combine(_dir, "BuilderDomain.bcheg"));
            var blob = new BlobchegBlob(BlobchegBuffer.From(file, Allocator.Temp), "BuilderDomain");
            try
            {
                ref readonly var table = ref blob.Read<TestTable>(writer.OffsetOf(ticket));
                Assert.That(table.Rows.Length, Is.EqualTo(2));
                Assert.That(table.Rows[0].Cells[0], Is.EqualTo(10));
                Assert.That(table.Rows[0].Cells[1], Is.EqualTo(20));
                Assert.That(table.Rows[1].Cells[0], Is.EqualTo(30));
            }
            finally
            {
                blob.Dispose();
            }
        }

        [Test]
        public void Окно_массива_живёт_через_соседний_Allocate()
        {
            byte[] built = null;
            var b = Open<TestTable>(bytes => built = bytes);
            var rows = b.Allocate(ref b.Root.Rows, 1);
            b.Allocate(ref rows[0].Cells, 1)[0] = 5;

            // Чанки не переезжают: окно rows обязано остаться живым после Allocate соседа.
            Assert.That(rows.Length, Is.EqualTo(1));
            b.End();
            Assert.That(built, Is.Not.Null);
        }

        [Test]
        public void Отрицательная_длина_бросает()
        {
            var b = Open<TestCurve>(_ => { });
            Assert.Throws<ArgumentOutOfRangeException>(() => b.Allocate(ref b.Root.Values, -1));
            b.Abandon();
        }

        [Test]
        public void Чужое_поле_бросает()
        {
            var b = Open<TestCurve>(_ => { });
            var foreign = new TestCurve();
            var thrown = Assert.Throws<InvalidOperationException>(() => b.Allocate(ref foreign.Values, 1));
            StringAssert.Contains("не из этой записи", thrown.Message);
            b.Abandon();
        }

        [Test]
        public void Повторный_Allocate_в_то_же_поле_бросает()
        {
            var b = Open<TestCurve>(_ => { });
            b.Allocate(ref b.Root.Values, 2);
            var thrown = Assert.Throws<InvalidOperationException>(() => b.Allocate(ref b.Root.Values, 3));
            StringAssert.Contains("Values", thrown.Message, "ошибка обязана назвать поле");
            b.Abandon();
        }

        [Test]
        public void Работа_после_End_бросает()
        {
            byte[] built = null;
            var b = Open<TestCurve>(bytes => built = bytes);
            b.End();

            var foreign = new TestCurve();
            Assert.Throws<InvalidOperationException>(() => _ = b.Root.Levels);
            Assert.Throws<InvalidOperationException>(() => b.Allocate(ref foreign.Values, 1));
            Assert.Throws<InvalidOperationException>(() => b.End());
            Assert.That(built, Is.Not.Null, "первый End при этом собрал запись");
        }

        [Test]
        public void Индекс_за_границей_окна_бросает()
        {
            var b = Open<TestCurve>(_ => { });
            var values = b.Allocate(ref b.Root.Values, 2);

            // ref struct в лямбду не захватывается — ловим руками.
            var caught = false;
            try
            {
                values[2] = 1f;
            }
            catch (IndexOutOfRangeException)
            {
                caught = true;
            }

            Assert.That(caught, Is.True);
            b.Abandon();
        }
    }
}
