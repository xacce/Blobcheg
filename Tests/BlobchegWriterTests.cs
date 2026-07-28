using System;
using System.IO;
using System.Text;
using NUnit.Framework;

namespace Blobcheg.Tests
{
    /// <summary>
    /// Раскладка и писатель. Главное свойство, которое тут доказывается: порядок обхода на файл не
    /// влияет, а правка значения не двигает оффсеты.
    /// </summary>
    public sealed class BlobchegWriterTests
    {
        string _dir;

        [SetUp]
        public void SetUp()
        {
            _dir = Path.Combine(Path.GetTempPath(), "blobcheg-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_dir))
                Directory.Delete(_dir, true);
        }

        static byte[] Payload(byte fill, int size = 8)
        {
            var bytes = new byte[size];
            for (var i = 0; i < size; i++)
                bytes[i] = fill;

            return bytes;
        }

        static BlobchegRecord Rec(string type, string key, byte fill, int size = 8)
            => new BlobchegRecord(type, key, 0, "node-" + key, Payload(fill, size));

        [Test]
        public void Записи_группируются_по_типу_и_выровнены_на_16()
        {
            var writer = BlobchegWriter.Open(_dir, "Domain");
            var shield = writer.Append(Rec("Shield", "b", 2));
            var gunB = writer.Append(Rec("Gun", "b", 1));
            var gunA = writer.Append(Rec("Gun", "a", 3));
            writer.Flush();

            Assert.That(writer.OffsetOf(gunA), Is.EqualTo(BlobchegFormat.HeaderSize), "первым идёт тип Gun, внутри — ключ 'a'");
            Assert.That(writer.OffsetOf(gunB), Is.GreaterThan(writer.OffsetOf(gunA)));
            Assert.That(writer.OffsetOf(shield), Is.GreaterThan(writer.OffsetOf(gunB)), "Shield по FullName идёт после Gun");

            foreach (var offset in new[] { writer.OffsetOf(gunA), writer.OffsetOf(gunB), writer.OffsetOf(shield) })
                Assert.That(offset % BlobchegFormat.RecordAlign, Is.Zero, "старт записи выровнен на 16");
        }

        [Test]
        public void Порядок_обхода_на_файл_не_влияет()
        {
            var straight = BlobchegWriter.Open(_dir, "Straight");
            straight.Append(Rec("Gun", "a", 1));
            straight.Append(Rec("Gun", "b", 2));
            straight.Append(Rec("Shield", "a", 3));
            straight.Flush();

            var reversed = BlobchegWriter.Open(_dir, "Reversed");
            reversed.Append(Rec("Shield", "a", 3));
            reversed.Append(Rec("Gun", "b", 2));
            reversed.Append(Rec("Gun", "a", 1));
            reversed.Flush();

            Assert.That(reversed.ContentHash, Is.EqualTo(straight.ContentHash));
            CollectionAssert.AreEqual(
                Body(Path.Combine(_dir, "Straight.bcheg")),
                Body(Path.Combine(_dir, "Reversed.bcheg")));
        }

        [Test]
        public void Сырые_записи_ложатся_в_хвост()
        {
            var writer = BlobchegWriter.Open(_dir, "Domain");
            var raw = writer.Append(new BlobchegRecord(null, "a", 0, "raw", Payload(9, 5)));
            var typed = writer.Append(Rec("Zzz", "a", 1));
            writer.Flush();

            Assert.That(writer.OffsetOf(raw), Is.GreaterThan(writer.OffsetOf(typed)),
                "сырые блоки переменной длины не должны таскать за собой типизированные");
        }

        [Test]
        public void Правка_значения_не_двигает_оффсеты()
        {
            var before = BlobchegWriter.Open(_dir, "Domain");
            var a = before.Append(Rec("Gun", "a", 1));
            var b = before.Append(Rec("Gun", "b", 2));
            before.Flush();

            var after = BlobchegWriter.Open(_dir, "Domain");
            var a2 = after.Append(Rec("Gun", "a", 77));
            var b2 = after.Append(Rec("Gun", "b", 2));
            after.Flush();

            Assert.That(after.OffsetOf(a2), Is.EqualTo(before.OffsetOf(a)));
            Assert.That(after.OffsetOf(b2), Is.EqualTo(before.OffsetOf(b)));
            Assert.That(after.RevisionOf(a2), Is.Not.EqualTo(before.RevisionOf(a)), "ревизия обязана заметить правку");
            Assert.That(after.RevisionOf(b2), Is.EqualTo(before.RevisionOf(b)), "нетронутая нода — та же ревизия");
        }

        [Test]
        public void Неизменное_содержимое_файл_не_переписывает()
        {
            var first = BlobchegWriter.Open(_dir, "Domain");
            first.Append(Rec("Gun", "a", 1));
            first.Flush();
            Assert.That(first.FileChanged, Is.True);

            var second = BlobchegWriter.Open(_dir, "Domain");
            second.Append(Rec("Gun", "a", 1));
            second.Flush();
            Assert.That(second.FileChanged, Is.False, "то же содержимое — файл не трогаем, иначе перепечётся всё");
        }

        [Test]
        public void Заявленный_адрес_остаётся_за_записью_а_новая_садится_в_хвост()
        {
            var before = BlobchegWriter.Open(_dir, "Domain");
            var only = before.Append(Rec("Gun", "b", 1));
            before.Flush();
            var kept = before.OffsetOf(only);

            var after = BlobchegWriter.Open(_dir, "Domain");
            var newcomer = after.Append(Rec("Gun", "a", 5));
            var old = after.Append(Rec("Gun", "b", 1));
            after.Claim(old, kept);
            after.Flush();

            Assert.That(after.OffsetOf(old), Is.EqualTo(kept), "прежний адрес обязан остаться за прежней записью");
            Assert.That(after.OffsetOf(newcomer), Is.GreaterThan(kept),
                "новая запись садится в хвост, хотя по ключу шла бы первой — иначе она сдвинет чужой адрес");
        }

        [Test]
        public void Удалённая_запись_оставляет_дырку_а_соседи_не_едут()
        {
            var before = BlobchegWriter.Open(_dir, "Domain");
            var a = before.Append(Rec("Gun", "a", 1));
            var b = before.Append(Rec("Gun", "b", 2));
            var c = before.Append(Rec("Gun", "c", 3));
            before.Flush();
            var keptA = before.OffsetOf(a);
            var keptC = before.OffsetOf(c);
            Assert.That(before.OffsetOf(b), Is.GreaterThan(keptA));

            var after = BlobchegWriter.Open(_dir, "Domain");
            var a2 = after.Append(Rec("Gun", "a", 1));
            var c2 = after.Append(Rec("Gun", "c", 3));
            after.Claim(a2, keptA);
            after.Claim(c2, keptC);
            after.Flush();

            Assert.That(after.OffsetOf(a2), Is.EqualTo(keptA));
            Assert.That(after.OffsetOf(c2), Is.EqualTo(keptC), "дырка от удалённой записи не подтягивает следующую");
        }

        [Test]
        public void Наехавшая_заявка_уступает_соседке_и_уезжает_в_хвост()
        {
            var before = BlobchegWriter.Open(_dir, "Domain");
            var a = before.Append(new BlobchegRecord(null, "a", 0, "raw-a", Payload(1, 8)));
            var b = before.Append(new BlobchegRecord(null, "b", 0, "raw-b", Payload(2, 8)));
            before.Flush();
            var keptA = before.OffsetOf(a);
            var keptB = before.OffsetOf(b);

            // Сырая запись выросла и накрыла место соседки: место остаётся за той, что раньше.
            var after = BlobchegWriter.Open(_dir, "Domain");
            var a2 = after.Append(new BlobchegRecord(null, "a", 0, "raw-a", Payload(1, 40)));
            var b2 = after.Append(new BlobchegRecord(null, "b", 0, "raw-b", Payload(2, 8)));
            after.Claim(a2, keptA);
            after.Claim(b2, keptB);
            after.Flush();

            Assert.That(after.OffsetOf(a2), Is.EqualTo(keptA));
            Assert.That(after.OffsetOf(b2), Is.GreaterThanOrEqualTo(keptA + 40),
                "наложения в файле быть не может: потерявшая место запись уезжает в хвост");
        }

        [Test]
        public void Мусорная_заявка_раскладку_не_ломает()
        {
            var plain = BlobchegWriter.Open(_dir, "Plain");
            var expected = plain.Append(Rec("Gun", "a", 1));
            plain.Flush();

            var claimed = BlobchegWriter.Open(_dir, "Claimed");
            var ticket = claimed.Append(Rec("Gun", "a", 1));
            claimed.Claim(ticket, 7);
            claimed.Flush();

            Assert.That(claimed.OffsetOf(ticket), Is.EqualTo(plain.OffsetOf(expected)),
                "адрес не по выравниванию — не адрес; запись получает место как новая");
        }

        [Test]
        public void Claim_после_Flush_бросает()
        {
            var writer = BlobchegWriter.Open(_dir, "Domain");
            var ticket = writer.Append(Rec("Gun", "a", 1));
            writer.Flush();
            Assert.Throws<InvalidOperationException>(() => writer.Claim(ticket, BlobchegFormat.HeaderSize));
        }

        [Test]
        public void Две_записи_одной_ноды_в_домен_бросают()
        {
            var writer = BlobchegWriter.Open(_dir, "Domain");
            writer.Append(Rec("Gun", "a", 1));
            Assert.Throws<InvalidOperationException>(() => writer.Append(Rec("Gun", "a", 2)));
        }

        [Test]
        public void Оффсет_до_Flush_бросает()
        {
            var writer = BlobchegWriter.Open(_dir, "Domain");
            var ticket = writer.Append(Rec("Gun", "a", 1));
            Assert.Throws<InvalidOperationException>(() => writer.OffsetOf(ticket));
            Assert.Throws<InvalidOperationException>(() => writer.RevisionOf(ticket));
        }

        [Test]
        public void Append_после_Flush_бросает()
        {
            var writer = BlobchegWriter.Open(_dir, "Domain");
            writer.Append(Rec("Gun", "a", 1));
            writer.Flush();
            Assert.Throws<InvalidOperationException>(() => writer.Append(Rec("Gun", "b", 2)));
        }

        [Test]
        public void Пустой_домен_даёт_файл_из_одного_хедера()
        {
            var writer = BlobchegWriter.Open(_dir, "Empty");
            writer.Flush();

            var file = File.ReadAllBytes(Path.Combine(_dir, "Empty.bcheg"));
            Assert.That(file.Length, Is.EqualTo(BlobchegFormat.HeaderSize));
        }

        [Test]
        public void Имя_файла_собирается_из_имени_домена()
        {
            Assert.That(BlobchegNaming.FileName("IHotPathCombatData"), Is.EqualTo("IHotPathCombatData.bcheg"));
            Assert.Throws<ArgumentException>(() => BlobchegNaming.FileName(""));
        }

        static byte[] Body(string path)
        {
            var file = File.ReadAllBytes(path);
            var body = new byte[file.Length - BlobchegFormat.HeaderSize];
            Buffer.BlockCopy(file, BlobchegFormat.HeaderSize, body, 0, body.Length);
            return body;
        }

        [Test]
        public void Debug_секция_несёт_имена_типа_и_ноды()
        {
            var writer = BlobchegWriter.Open(_dir, "Domain");
            writer.Append(new BlobchegRecord("Ns.Gun", "a", 0xDEAD, "СуперПушка", Payload(1)));
            writer.Flush(withDebug: true);

            var file = File.ReadAllBytes(Path.Combine(_dir, "Domain.bcheg"));
            var debugOffset = BitConverter.ToUInt32(file, 12);
            Assert.That(debugOffset, Is.Not.Zero);
            Assert.That(BitConverter.ToUInt32(file, (int)debugOffset), Is.EqualTo(BlobchegDebugSection.Magic));

            var count = BitConverter.ToUInt32(file, (int)debugOffset + 4);
            Assert.That(count, Is.EqualTo(1));

            var typeHash = BitConverter.ToUInt32(file, (int)debugOffset + BlobchegDebugSection.PrologSize + 8);
            Assert.That(typeHash, Is.EqualTo(0xDEAD));

            var nameOffset = BitConverter.ToUInt32(file, (int)debugOffset + BlobchegDebugSection.PrologSize + 12);
            var typeLength = BitConverter.ToUInt16(file, (int)nameOffset);
            Assert.That(Encoding.UTF8.GetString(file, (int)nameOffset + 2, typeLength), Is.EqualTo("Ns.Gun"));
        }

        [Test]
        public void Без_дефайна_секции_в_файле_нет()
        {
            var writer = BlobchegWriter.Open(_dir, "Domain");
            writer.Append(Rec("Gun", "a", 1));
            writer.Flush();

            var file = File.ReadAllBytes(Path.Combine(_dir, "Domain.bcheg"));
            Assert.That(BitConverter.ToUInt32(file, 12), Is.Zero, "debugOffset");
            Assert.That(BitConverter.ToUInt16(file, 6), Is.Zero, "flags");
        }
    }
}
