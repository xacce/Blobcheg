using System;
using System.IO;
using NUnit.Framework;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;

namespace Blobcheg.Tests
{
    struct TestGun
    {
        public float AmmoMax;
        public int Rpm;
    }

    struct TestShield
    {
        public float Hp;
    }

    /// <summary>A twin of <see cref="TestGun"/>: the same size, a different type. The reinterpretation trap.</summary>
    struct TestGunTwin
    {
        public float AmmoMax;
        public int Rpm;
    }

    /// <summary>
    /// Loading a base: the integrity, reading at an offset, the debug contour. Everything that sits
    /// behind <c>ENABLE_UNITY_COLLECTIONS_CHECKS</c> is checked here — in the editor the define is set.
    /// </summary>
    public sealed class BlobchegBlobTests
    {
        string _dir;

        [SetUp]
        public void SetUp()
        {
            _dir = Path.Combine(Path.GetTempPath(), "blobcheg-blob-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_dir))
                Directory.Delete(_dir, true);
        }

        unsafe byte[] Bytes<T>(in T value) where T : unmanaged
        {
            var bytes = new byte[Unity.Collections.LowLevel.Unsafe.UnsafeUtility.SizeOf<T>()];
            var copy = value;
            fixed (byte* destination = bytes)
                Unity.Collections.LowLevel.Unsafe.UnsafeUtility.CopyStructureToPtr(ref copy, destination);

            return bytes;
        }

        (byte[] file, uint gun, uint shield) Build(bool withDebug = false)
        {
            var writer = BlobchegWriter.Open(_dir, "Domain");
            var gun = writer.Append(new BlobchegRecord(typeof(TestGun).FullName, "a",
                unchecked((uint)BurstRuntime.GetHashCode32<TestGun>()), "gun", Bytes(new TestGun { AmmoMax = 30f, Rpm = 600 })));
            var shield = writer.Append(new BlobchegRecord(typeof(TestShield).FullName, "a",
                unchecked((uint)BurstRuntime.GetHashCode32<TestShield>()), "shield", Bytes(new TestShield { Hp = 12.5f })));
            writer.Flush(withDebug);

            return (File.ReadAllBytes(Path.Combine(_dir, "Domain.bcheg")),
                writer.OffsetOf(gun), writer.OffsetOf(shield));
        }

        [Test]
        public void It_reads_what_was_written_at_the_offset()
        {
            var built = Build();
            var blob = new BlobchegBlob(BlobchegBuffer.From(built.file, Allocator.Temp), "Domain");
            try
            {
                Assert.That(blob.Read<TestGun>(built.gun).AmmoMax, Is.EqualTo(30f));
                Assert.That(blob.Read<TestGun>(built.gun).Rpm, Is.EqualTo(600));
                Assert.That(blob.Read<TestShield>(built.shield).Hp, Is.EqualTo(12.5f));
            }
            finally
            {
                blob.Dispose();
            }
        }

        [Test]
        public void A_swapped_byte_is_caught_on_load()
        {
            var built = Build();
            built.file[built.gun] ^= 0xFF;

            var buffer = BlobchegBuffer.From(built.file, Allocator.Temp);
            var thrown = Assert.Throws<InvalidOperationException>(() => new BlobchegBlob(buffer, "Domain"));
            StringAssert.Contains("integrity", thrown.Message);
            buffer.Dispose();
        }

        [Test]
        public void A_truncated_file_is_caught_on_load_and_the_failure_is_transient()
        {
            var built = Build();
            var cut = new byte[built.file.Length - BlobchegFormat.RecordAlign];
            Buffer.BlockCopy(built.file, 0, cut, 0, cut.Length);

            var buffer = BlobchegBuffer.From(cut, Allocator.Temp);

            // A file caught mid-rewrite looks the same: the reader learned the length from the new
            // header while the bytes came from the old one. The cause is in time and not in the bytes —
            // hence the separate type by which the editor tells a notification from a breakage.
            Assert.Throws<BlobchegTransientException>(() => new BlobchegBlob(buffer, "Domain"));
            buffer.Dispose();
        }

        [Test]
        public void Corrupted_bytes_do_not_count_as_transient()
        {
            var built = Build();
            built.file[built.gun] ^= 0xFF;

            var buffer = BlobchegBuffer.From(built.file, Allocator.Temp);

            // The boundary: the length agreed, so the file is written to the end and the integrity will
            // never agree again. There is nothing to wait for here — this is an error, not a moment.
            Assert.Throws<InvalidOperationException>(() => new BlobchegBlob(buffer, "Domain"));
            buffer.Dispose();
        }

        [Test]
        public void A_foreign_magic_is_caught_on_load()
        {
            var built = Build();
            built.file[0] = 0x00;

            var buffer = BlobchegBuffer.From(built.file, Allocator.Temp);
            Assert.Throws<InvalidOperationException>(() => new BlobchegBlob(buffer, "Domain"));
            buffer.Dispose();
        }

        [Test]
        public void A_file_of_a_foreign_domain_does_not_load_under_this_name()
        {
            var built = Build();

            // Two .bcheg files were swapped: the bytes are whole, the integrity agrees, and the domain is wrong.
            var buffer = BlobchegBuffer.From(built.file, Allocator.Temp);
            var thrown = Assert.Throws<InvalidOperationException>(() => new BlobchegBlob(buffer, "ForeignDomain"));
            StringAssert.Contains("another domain", thrown.Message);
            buffer.Dispose();
        }

        [Test]
        public void A_record_is_read_only_with_its_own_type()
        {
            // A twin: the same size, a different type. The debug contour catches it, and in the editor it is there.
            var built = Build(withDebug: true);
            var blob = new BlobchegBlob(BlobchegBuffer.From(built.file, Allocator.Temp), "Domain");
            try
            {
                Assert.That(blob.Read<TestGun>(built.gun).Rpm, Is.EqualTo(600), "its own type is obliged to read");

                Assert.Throws<InvalidOperationException>(() => blob.Read<TestGunTwin>(built.gun),
                    "a TestGun lies at this address — handing it out as its twin is not allowed even at equal size");
                Assert.Throws<InvalidOperationException>(() => blob.Read<TestShield>(built.gun),
                    "and not as the twin either");
            }
            finally
            {
                blob.Dispose();
            }
        }

        [Test]
        public void An_offset_past_the_end_of_the_buffer_throws()
        {
            var built = Build();
            var blob = new BlobchegBlob(BlobchegBuffer.From(built.file, Allocator.Temp), "Domain");
            try
            {
                Assert.Throws<InvalidOperationException>(() => blob.Read<TestGun>((uint)built.file.Length));
                Assert.Throws<InvalidOperationException>(() => blob.Read<TestGun>(built.gun + 4));
            }
            finally
            {
                blob.Dispose();
            }
        }

        [Test]
        public void The_debug_contour_names_the_record()
        {
            var built = Build(withDebug: true);
            var blob = new BlobchegBlob(BlobchegBuffer.From(built.file, Allocator.Temp), "Domain");
            try
            {
                Assert.That(blob.HasDebug, Is.True);
                blob.Describe(built.shield, out var typeName, out var nodeName);
                Assert.That(typeName, Is.EqualTo(typeof(TestShield).FullName));
                Assert.That(nodeName, Is.EqualTo("shield"));
            }
            finally
            {
                blob.Dispose();
            }
        }

        [Test]
        public void Without_a_debug_contour_Describe_throws()
        {
            var built = Build();
            var blob = new BlobchegBlob(BlobchegBuffer.From(built.file, Allocator.Temp), "Domain");
            try
            {
                Assert.That(blob.HasDebug, Is.False);
                Assert.Throws<InvalidOperationException>(() => blob.Describe(built.gun, out _, out _));
            }
            finally
            {
                blob.Dispose();
            }
        }

        [BurstCompile]
        struct ReadJob : IJob
        {
            public BlobchegBlob Db;
            public uint Offset;
            public NativeArray<float> Result;

            public void Execute() => Result[0] = Db.Read<TestGun>(Offset).AmmoMax;
        }

        [Test]
        public void It_is_read_from_a_bursted_job()
        {
            var built = Build();
            var blob = new BlobchegBlob(BlobchegBuffer.From(built.file, Allocator.Persistent), "Domain");
            var result = new NativeArray<float>(1, Allocator.Persistent);
            try
            {
                new ReadJob { Db = blob, Offset = built.gun, Result = result }.Run();
                Assert.That(result[0], Is.EqualTo(30f));
            }
            finally
            {
                result.Dispose();
                blob.Dispose();
            }
        }

        [Test]
        public void The_transport_reads_the_whole_file()
        {
            Build();

            var transport = new BlobchegFileTransport(_dir);
            var load = transport.Read(BlobchegNaming.FileName("Domain"), Allocator.Persistent);
            load.Complete();

            var blob = new BlobchegBlob(load.Acquire(), "Domain");
            try
            {
                Assert.That(blob.Length, Is.EqualTo(new FileInfo(Path.Combine(_dir, "Domain.bcheg")).Length));
            }
            finally
            {
                blob.Dispose();
            }
        }

        [Test]
        public void The_transport_on_a_missing_file_throws_a_transient()
        {
            var transport = new BlobchegFileTransport(_dir);
            var load = transport.Read(BlobchegNaming.FileName("NoSuchThing"), Allocator.Persistent);

            // The file is not there — but in the editor that also means "not yet": the domain arrived
            // with a pull before the rebuild wrote its file. The failure type is obliged to tell those apart.
            Assert.Throws<BlobchegTransientException>(() => load.Complete());
            load.Dispose();
        }
    }
}
