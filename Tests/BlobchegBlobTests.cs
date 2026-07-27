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

    /// <summary>
    /// Подъём базы: целостность, чтение по оффсету, отладочный контур. Всё, что за
    /// <c>ENABLE_UNITY_COLLECTIONS_CHECKS</c>, проверяется тут — в редакторе дефайн стоит.
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
        public void Читает_записанное_по_оффсету()
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
        public void Подмена_байта_ловится_на_подъёме()
        {
            var built = Build();
            built.file[built.gun] ^= 0xFF;

            var buffer = BlobchegBuffer.From(built.file, Allocator.Temp);
            var thrown = Assert.Throws<InvalidOperationException>(() => new BlobchegBlob(buffer, "Domain"));
            StringAssert.Contains("целостность", thrown.Message);
            buffer.Dispose();
        }

        [Test]
        public void Обрезанный_файл_ловится_на_подъёме()
        {
            var built = Build();
            var cut = new byte[built.file.Length - BlobchegFormat.RecordAlign];
            Buffer.BlockCopy(built.file, 0, cut, 0, cut.Length);

            var buffer = BlobchegBuffer.From(cut, Allocator.Temp);
            Assert.Throws<InvalidOperationException>(() => new BlobchegBlob(buffer, "Domain"));
            buffer.Dispose();
        }

        [Test]
        public void Чужой_magic_ловится_на_подъёме()
        {
            var built = Build();
            built.file[0] = 0x00;

            var buffer = BlobchegBuffer.From(built.file, Allocator.Temp);
            Assert.Throws<InvalidOperationException>(() => new BlobchegBlob(buffer, "Domain"));
            buffer.Dispose();
        }

        [Test]
        public void Оффсет_за_концом_буфера_бросает()
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
        public void Отладочный_контур_называет_запись()
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
        public void Без_отладочного_контура_Describe_бросает()
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
        public void Читается_из_бёрстовой_джобы()
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
        public void Транспорт_читает_файл_целиком()
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
        public void Транспорт_на_отсутствующем_файле_бросает()
        {
            var transport = new BlobchegFileTransport(_dir);
            var load = transport.Read(BlobchegNaming.FileName("НетТакого"), Allocator.Persistent);
            Assert.Throws<InvalidOperationException>(() => load.Complete());
            load.Dispose();
        }
    }
}
