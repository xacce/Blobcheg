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
    /// Reading a <see cref="BlobchegArray{T}"/> over bytes assembled by hand: the layout of the tail is
    /// assigned here by the test and not by the builder, so exactly the runtime contract is visible —
    /// the self-relative offset, emptiness without dereferencing, the refusal on a copy of the record.
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
        /// TestCurve by hand: Levels in [0,4), the array field in [4,12), the float tail from 12.
        /// The offset is measured from the field address: 12 - 4 = 8.
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
        public void The_array_is_read_element_by_element()
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
                Assert.That(curve.Values[2], Is.EqualTo(3.5f), "the last byte of the last element is still inside the record");
            }
            finally
            {
                blob.Dispose();
            }
        }

        [Test]
        public void An_empty_array_is_read_without_dereferencing()
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
                        "emptiness has no pointer — and no dereferencing");
                }
            }
            finally
            {
                blob.Dispose();
            }
        }

        [Test]
        public void A_single_element_is_read()
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
        public void A_nested_array_is_read()
        {
            // TestTable by hand: the Rows field in [0,8), the rows in [8,24), their tails from 24.
            // The offset of every row is measured from the address of its own Cells field.
            var stream = new MemoryStream();
            var w = new BinaryWriter(stream);
            w.Write(8);     // Rows._offset: the rows lie from 8, the field is at 0
            w.Write(2);     // Rows._length
            w.Write(16);    // row0.Cells._offset: the numbers from 24, the field is at 8
            w.Write(2);     // row0.Cells._length
            w.Write(16);    // row1.Cells._offset: the numbers from 32, the field is at 16
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
        public void An_index_past_the_bounds_throws()
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
        /// Proof that readonly is placed correctly, from both sides at once: an ordinary read through a
        /// ref readonly does NOT make a defensive copy (otherwise the tests above would have gone red),
        /// while an explicit copy into a local variable is caught by the address check, names the type
        /// and says it is about a copy.
        /// </summary>
        [Test]
        public void A_copy_of_the_record_throws_instead_of_handing_out_garbage()
        {
            var built = Build(CurveBytes(3, 1.5f, 2.5f, 3.5f), typeof(TestCurve).FullName);
            var blob = new BlobchegBlob(BlobchegBuffer.From(built.file, Allocator.Temp), "ArrayDomain");
            try
            {
                var copy = blob.Read<TestCurve>(built.offset);
                var thrown = Assert.Throws<InvalidOperationException>(() => _ = copy.Values[0]);
                StringAssert.Contains("copy", thrown.Message);
                StringAssert.Contains("System.Single", thrown.Message, "the error is obliged to name the element type");
            }
            finally
            {
                blob.Dispose();
            }
        }

        [Test]
        public void GetUnsafePtr_hands_out_the_first_element()
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
        public void The_array_is_read_from_a_bursted_job()
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
