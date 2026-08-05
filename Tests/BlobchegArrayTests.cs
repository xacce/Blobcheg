using System;
using System.IO;
using NUnit.Framework;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;

namespace Blobcheg.Tests
{
    struct TestCurve
    {
        public int Levels;
        public BlobchegArray<float> Values;
    }

    struct TestRow
    {
        public BlobchegArray<int> Cells;
    }

    struct TestTable
    {
        public BlobchegArray<TestRow> Rows;
    }

    /// <summary>
    /// Чтение <see cref="BlobchegArray{T}"/> на байтах, собранных руками: раскладка хвоста здесь
    /// назначается тестом, а не билдером, поэтому видно ровно контракт рантайма — само-относительный
    /// оффсет, пустота без разыменования, отказ на копии записи.
    /// </summary>
    public sealed class BlobchegArrayTests
    {
        string _dir;

        [SetUp]
        public void SetUp()
        {
            _dir = Path.Combine(Path.GetTempPath(), "blobcheg-array-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_dir))
                Directory.Delete(_dir, true);
        }

        (byte[] file, uint offset) Build(byte[] recordBytes, string typeName)
        {
            var writer = BlobchegWriter.Open(_dir, "ArrayDomain");
            var ticket = writer.Append(new BlobchegRecord(typeName, "a", 0, "node", recordBytes));
            writer.Flush();

            return (File.ReadAllBytes(Path.Combine(_dir, "ArrayDomain.bcheg")), writer.OffsetOf(ticket));
        }

        /// <summary>
        /// TestCurve руками: Levels в [0,4), поле массива в [4,12), хвост float'ов с 12.
        /// Оффсет меряется от адреса поля: 12 - 4 = 8.
        /// </summary>
        static byte[] CurveBytes(int levels, params float[] values)
        {
            var stream = new MemoryStream();
            var w = new BinaryWriter(stream);
            w.Write(levels);
            w.Write(values.Length > 0 ? 8 : 0);
            w.Write(values.Length);
            foreach (var value in values)
                w.Write(value);

            w.Flush();
            return stream.ToArray();
        }

        [Test]
        public void Массив_читается_по_элементам()
        {
            var built = Build(CurveBytes(3, 1.5f, 2.5f, 3.5f), typeof(TestCurve).FullName);
            var blob = new BlobchegBlob(BlobchegBuffer.From(built.file, Allocator.Temp), "ArrayDomain");
            try
            {
                ref readonly var curve = ref blob.Read<TestCurve>(built.offset);
                Assert.That(curve.Levels, Is.EqualTo(3));
                Assert.That(curve.Values.Length, Is.EqualTo(3));
                Assert.That(curve.Values.IsEmpty, Is.False);
                Assert.That(curve.Values[0], Is.EqualTo(1.5f));
                Assert.That(curve.Values[1], Is.EqualTo(2.5f));
                Assert.That(curve.Values[2], Is.EqualTo(3.5f), "последний байт последнего элемента — ещё внутри записи");
            }
            finally
            {
                blob.Dispose();
            }
        }

        [Test]
        public void Пустой_массив_читается_без_разыменования()
        {
            var built = Build(CurveBytes(0), typeof(TestCurve).FullName);
            var blob = new BlobchegBlob(BlobchegBuffer.From(built.file, Allocator.Temp), "ArrayDomain");
            try
            {
                ref readonly var curve = ref blob.Read<TestCurve>(built.offset);
                Assert.That(curve.Values.IsEmpty, Is.True);
                Assert.That(curve.Values.Length, Is.Zero);
                unsafe
                {
                    Assert.That((IntPtr)curve.Values.GetUnsafePtr(), Is.EqualTo(IntPtr.Zero),
                        "у пустоты нет указателя — и нет разыменования");
                }
            }
            finally
            {
                blob.Dispose();
            }
        }

        [Test]
        public void Один_элемент_читается()
        {
            var built = Build(CurveBytes(1, 42f), typeof(TestCurve).FullName);
            var blob = new BlobchegBlob(BlobchegBuffer.From(built.file, Allocator.Temp), "ArrayDomain");
            try
            {
                Assert.That(blob.Read<TestCurve>(built.offset).Values[0], Is.EqualTo(42f));
            }
            finally
            {
                blob.Dispose();
            }
        }

        [Test]
        public void Вложенный_массив_читается()
        {
            // TestTable руками: поле Rows в [0,8), строки в [8,24), их хвосты — с 24.
            // Оффсет каждой строки меряется от адреса её собственного поля Cells.
            var stream = new MemoryStream();
            var w = new BinaryWriter(stream);
            w.Write(8);     // Rows._offset: строки лежат с 8, поле на 0
            w.Write(2);     // Rows._length
            w.Write(16);    // row0.Cells._offset: числа с 24, поле на 8
            w.Write(2);     // row0.Cells._length
            w.Write(16);    // row1.Cells._offset: числа с 32, поле на 16
            w.Write(1);     // row1.Cells._length
            w.Write(10);
            w.Write(20);
            w.Write(30);
            w.Flush();

            var built = Build(stream.ToArray(), typeof(TestTable).FullName);
            var blob = new BlobchegBlob(BlobchegBuffer.From(built.file, Allocator.Temp), "ArrayDomain");
            try
            {
                ref readonly var table = ref blob.Read<TestTable>(built.offset);
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
        public void Индекс_за_границей_бросает()
        {
            var built = Build(CurveBytes(2, 1f, 2f), typeof(TestCurve).FullName);
            var blob = new BlobchegBlob(BlobchegBuffer.From(built.file, Allocator.Temp), "ArrayDomain");
            try
            {
                Assert.Throws<IndexOutOfRangeException>(() => _ = blob.Read<TestCurve>(built.offset).Values[2]);
                Assert.Throws<IndexOutOfRangeException>(() => _ = blob.Read<TestCurve>(built.offset).Values[-1]);
            }
            finally
            {
                blob.Dispose();
            }
        }

        /// <summary>
        /// Доказательство, что readonly расставлен правильно, с двух сторон сразу: обычное чтение
        /// через ref readonly НЕ делает защитной копии (иначе покраснели бы тесты выше), а явная
        /// копия в локальную переменную ловится проверкой адреса, называет тип и говорит про копию.
        /// </summary>
        [Test]
        public void Копия_записи_бросает_а_не_отдаёт_мусор()
        {
            var built = Build(CurveBytes(3, 1.5f, 2.5f, 3.5f), typeof(TestCurve).FullName);
            var blob = new BlobchegBlob(BlobchegBuffer.From(built.file, Allocator.Temp), "ArrayDomain");
            try
            {
                var copy = blob.Read<TestCurve>(built.offset);
                var thrown = Assert.Throws<InvalidOperationException>(() => _ = copy.Values[0]);
                StringAssert.Contains("копи", thrown.Message);
                StringAssert.Contains("System.Single", thrown.Message, "ошибка обязана назвать тип элемента");
            }
            finally
            {
                blob.Dispose();
            }
        }

        [Test]
        public void GetUnsafePtr_отдаёт_первый_элемент()
        {
            var built = Build(CurveBytes(3, 1f, 2f, 4f), typeof(TestCurve).FullName);
            var blob = new BlobchegBlob(BlobchegBuffer.From(built.file, Allocator.Temp), "ArrayDomain");
            try
            {
                unsafe
                {
                    ref readonly var curve = ref blob.Read<TestCurve>(built.offset);
                    var ptr = curve.Values.GetUnsafePtr();
                    var sum = 0f;
                    for (var i = 0; i < curve.Values.Length; i++)
                        sum += ptr[i];

                    Assert.That(sum, Is.EqualTo(7f));
                }
            }
            finally
            {
                blob.Dispose();
            }
        }

        [BurstCompile]
        struct SumJob : IJob
        {
            public BlobchegBlob Db;
            public uint Offset;
            public NativeArray<float> Result;

            public void Execute()
            {
                ref readonly var curve = ref Db.Read<TestCurve>(Offset);
                var sum = 0f;
                for (var i = 0; i < curve.Values.Length; i++)
                    sum += curve.Values[i];

                Result[0] = sum;
            }
        }

        [Test]
        public void Массив_читается_из_бёрстовой_джобы()
        {
            var built = Build(CurveBytes(3, 1f, 2f, 4f), typeof(TestCurve).FullName);
            var blob = new BlobchegBlob(BlobchegBuffer.From(built.file, Allocator.Persistent), "ArrayDomain");
            var result = new NativeArray<float>(1, Allocator.Persistent);
            try
            {
                new SumJob { Db = blob, Offset = built.offset, Result = result }.Run();
                Assert.That(result[0], Is.EqualTo(7f));
            }
            finally
            {
                result.Dispose();
                blob.Dispose();
            }
        }
    }
}
