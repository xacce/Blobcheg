using System;
using System.Diagnostics;
using System.IO;
using NUnit.Framework;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using UnityEngine;

namespace Blobcheg.Tests
{
    /// <summary>
    /// Хронометраж чтения в редакторе. Это не проверка поведения: числа тут не сравниваются ни с
    /// каким порогом, тесты падают только если чтение вернуло не то, что записано, — то есть если
    /// измеряли пустой цикл.
    ///
    /// Зачем: с тех пор как отладочный контур включили по умолчанию, каждый <c>Read</c> в редакторе
    /// делает двоичный поиск по debug-секции, а раньше это была реинтерпретация по оффсету. Цена
    /// этого не была мерена. Замер снят ДО того, как в <c>CheckRead</c> ляжет
    /// <c>AtomicSafetyHandle</c>: иначе после его появления нельзя будет сказать, чья цена.
    ///
    /// Цифры печатаются в лог; повторить после любой правки <c>CheckRead</c> — тем же прогоном.
    /// </summary>
    public sealed class BlobchegReadCostTests
    {
        const string DomainName = "CostDomain";

        /// <summary>Замеряемая запись: восемь байт, как обычная запись потребителя.</summary>
        struct CostGun
        {
            public float AmmoMax;
            public int Rpm;
        }

        /// <summary>Значение в каждой записи одно и то же — по сумме видно, что цикл не выкинули.</summary>
        const int Rpm = 4242;

        string _dir;

        // Полями, а не локальными: замеряемый цикл не должен читать поле замыкания на каждом витке.
        BlobchegBuffer _buffer;
        BlobchegBlob _blob;
        uint[] _offsets;

        [SetUp]
        public void SetUp()
        {
            _dir = Path.Combine(Path.GetTempPath(), "blobcheg-cost-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_dir))
                Directory.Delete(_dir, true);
        }

        // ------------------------------------------------------------- стенд

        /// <summary>
        /// Файловый цикл: домен из <paramref name="records"/> записей одного типа. Ассеты не нужны —
        /// мерить надо цену чтения, а не цену пересборки.
        /// </summary>
        unsafe byte[] Build(int records, bool withDebug, out uint[] offsets)
        {
            var writer = BlobchegWriter.Open(_dir, DomainName);
            var typeHash = unchecked((uint)BurstRuntime.GetHashCode32<CostGun>());
            var typeName = typeof(CostGun).FullName;
            var tickets = new int[records];

            for (var i = 0; i < records; i++)
            {
                var bytes = new byte[UnsafeUtility.SizeOf<CostGun>()];
                var value = new CostGun { AmmoMax = 30f, Rpm = Rpm };
                fixed (byte* destination = bytes)
                    UnsafeUtility.CopyStructureToPtr(ref value, destination);

                tickets[i] = writer.Append(new BlobchegRecord(typeName, i.ToString("D6"), typeHash, "gun" + i, bytes));
            }

            writer.Flush(withDebug);

            offsets = new uint[records];
            for (var i = 0; i < records; i++)
                offsets[i] = writer.OffsetOf(tickets[i]);

            return File.ReadAllBytes(writer.FilePath);
        }

        void Open(byte[] file, uint[] offsets)
        {
            _offsets = offsets;
            _buffer = BlobchegBuffer.From(file, Allocator.Persistent);
            _blob = new BlobchegBlob(_buffer, DomainName);
        }

        void Close()
        {
            _blob.Dispose();
            _buffer = default;
            _offsets = null;
        }

        // ------------------------------------------------------------- циклы

        /// <summary>
        /// Пол стенда: тот же обход массива оффсетов без всякого чтения. Всё остальное надо читать
        /// как «это плюс столько».
        /// </summary>
        long PassLoop(int iterations)
        {
            var offsets = _offsets;
            var count = offsets.Length;
            var slot = 0;
            long sum = 0;

            for (var i = 0; i < iterations; i++)
            {
                sum += offsets[slot];
                if (++slot == count)
                    slot = 0;
            }

            return sum;
        }

        /// <summary>Релизный путь: чистая реинтерпретация по оффсету, никаких проверок.</summary>
        unsafe long PassRaw(int iterations)
        {
            var ptr = _buffer.Ptr;
            var offsets = _offsets;
            var count = offsets.Length;
            var slot = 0;
            long sum = 0;

            for (var i = 0; i < iterations; i++)
            {
                sum += UnsafeUtility.AsRef<CostGun>(ptr + offsets[slot]).Rpm;
                if (++slot == count)
                    slot = 0;
            }

            return sum;
        }

        /// <summary>Путь редактора: <c>Read</c> со всем, что стоит за ENABLE_UNITY_COLLECTIONS_CHECKS.</summary>
        long PassRead(int iterations)
        {
            var offsets = _offsets;
            var count = offsets.Length;
            var slot = 0;
            long sum = 0;

            for (var i = 0; i < iterations; i++)
            {
                sum += _blob.Read<CostGun>(offsets[slot]).Rpm;
                if (++slot == count)
                    slot = 0;
            }

            return sum;
        }

        // ------------------------------------------------------------- хронометр

        /// <summary>Лучшее из трёх после прогрева: минимум устойчив к постороннему шуму машины.</summary>
        static double NsPerRead(Func<int, long> pass, int iterations, long expectedSum)
        {
            pass(Math.Max(1024, iterations / 10));

            var best = double.MaxValue;
            for (var attempt = 0; attempt < 3; attempt++)
            {
                var watch = Stopwatch.StartNew();
                var sum = pass(iterations);
                watch.Stop();

                Assert.That(sum, Is.EqualTo(expectedSum), "цикл замера прочитал не то, что записано");

                var ns = watch.Elapsed.TotalMilliseconds * 1e6 / iterations;
                if (ns < best)
                    best = ns;
            }

            return best;
        }

        // ------------------------------------------------------------- замеры

        [Test]
        public void Цена_чтения_в_редакторе_разложена_по_слоям()
        {
            const int records = 4096;
            const int iterations = 1_000_000;

            var withDebug = Build(records, true, out var debugOffsets);
            var noDebug = Build(records, false, out var plainOffsets);

            double loop, raw, checksOnly, checksAndDebug;

            Open(noDebug, plainOffsets);
            try
            {
                Assert.That(_blob.HasDebug, Is.False, "этот файл собран без отладочного контура");
                loop = NsPerRead(PassLoop, iterations, SumOfOffsets(plainOffsets, iterations));
                raw = NsPerRead(PassRaw, iterations, (long)Rpm * iterations);
                checksOnly = NsPerRead(PassRead, iterations, (long)Rpm * iterations);
            }
            finally
            {
                Close();
            }

            Open(withDebug, debugOffsets);
            try
            {
                Assert.That(_blob.HasDebug, Is.True, "а этот — с контуром");
                checksAndDebug = NsPerRead(PassRead, iterations, (long)Rpm * iterations);
            }
            finally
            {
                Close();
            }

            UnityEngine.Debug.Log(
                $"Blobcheg, цена одного чтения в редакторе ({records} записей, {iterations} чтений, лучшее из трёх):\n" +
                $"  пол стенда (обход без чтения) : {loop:F2} нс\n" +
                $"  реинтерпретация без проверок  : {raw:F2} нс   (путь релизного плеера)\n" +
                $"  Read, файл без контура        : {checksOnly:F2} нс   (+{checksOnly - raw:F2} — выравнивание и границы)\n" +
                $"  Read, файл с контуром         : {checksAndDebug:F2} нс   (+{checksAndDebug - checksOnly:F2} — двоичный поиск и сверка типа)\n" +
                $"  итого редактор против релиза  : x{(raw > 0 ? checksAndDebug / raw : 0):F1}");
        }

        [Test]
        public void Цена_отладочного_контура_растёт_с_числом_записей()
        {
            const int iterations = 500_000;
            var sizes = new[] { 1, 64, 1024, 16384, 65536 };
            var report = $"Blobcheg, цена чтения с отладочным контуром по объёму базы ({iterations} чтений):\n";

            foreach (var records in sizes)
            {
                var file = Build(records, true, out var offsets);

                Open(file, offsets);
                try
                {
                    var raw = NsPerRead(PassRaw, iterations, (long)Rpm * iterations);
                    var read = NsPerRead(PassRead, iterations, (long)Rpm * iterations);
                    report += $"  {records,6} записей: Read {read,7:F2} нс, реинтерпретация {raw,6:F2} нс, " +
                              $"контур и проверки +{read - raw:F2} нс\n";
                }
                finally
                {
                    Close();
                }
            }

            UnityEngine.Debug.Log(report);
        }

        /// <summary>
        /// Обе проверки чтения зовут дженериковые интринсики: границы — <c>SizeOf&lt;T&gt;</c>, сверка
        /// типа — <c>GetHashCode32&lt;T&gt;</c>. В бёрстовой джобе это свёрнутые константы, в редакторе
        /// на Mono — вызовы на каждом чтении. Без этого замера постоянная часть цены контура
        /// записалась бы на двоичный поиск, которого при одной записи в базе почти нет.
        /// </summary>
        [Test]
        public void Цена_дженериковых_интринсиков_в_редакторе()
        {
            const int iterations = 1_000_000;

            var sizeOf = NsPerRead(PassSizeOf, iterations, (long)UnsafeUtility.SizeOf<CostGun>() * iterations);
            var hash = NsPerRead(PassTypeHash, iterations,
                unchecked((long)(uint)BurstRuntime.GetHashCode32<CostGun>()) * iterations);

            UnityEngine.Debug.Log(
                $"Blobcheg, цена дженериковых интринсиков в редакторе ({iterations} вызовов, лучшее из трёх):\n" +
                $"  UnsafeUtility.SizeOf<T>()      : {sizeOf:F2} нс   (зовётся из проверки границ)\n" +
                $"  BurstRuntime.GetHashCode32<T>(): {hash:F2} нс   (зовётся из сверки типа)\n" +
                $"  вместе на одно чтение          : {sizeOf + hash:F2} нс — под Burst это константы, в редакторе нет");
        }

        static long PassSizeOf(int iterations)
        {
            long sum = 0;
            for (var i = 0; i < iterations; i++)
                sum += UnsafeUtility.SizeOf<CostGun>();

            return sum;
        }

        static long PassTypeHash(int iterations)
        {
            long sum = 0;
            for (var i = 0; i < iterations; i++)
                sum += unchecked((uint)BurstRuntime.GetHashCode32<CostGun>());

            return sum;
        }

        static long SumOfOffsets(uint[] offsets, int iterations)
        {
            var slot = 0;
            long sum = 0;
            for (var i = 0; i < iterations; i++)
            {
                sum += offsets[slot];
                if (++slot == offsets.Length)
                    slot = 0;
            }

            return sum;
        }
    }
}
