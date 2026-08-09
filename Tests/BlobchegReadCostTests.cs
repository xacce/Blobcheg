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
    /// Timing of a read in the editor. This is not a behaviour check: the numbers here are not compared
    /// against any threshold, the tests fail only if the read returned something other than what was
    /// written — that is, if an empty loop was measured.
    ///
    /// Why: ever since the debug contour was switched on by default, every <c>Read</c> in the editor
    /// does a binary search over the debug section, while before that it was a reinterpretation at an
    /// offset. The price of that was never measured. The measurement is taken BEFORE an
    /// <c>AtomicSafetyHandle</c> lands in <c>CheckRead</c>: otherwise, once it appears, there will be no
    /// telling whose price is whose.
    ///
    /// The numbers are printed into the log; repeat after any edit of <c>CheckRead</c> with the same run.
    /// </summary>
    public sealed class BlobchegReadCostTests
    {
        const string DomainName = "CostDomain";

        /// <summary>The record being measured: eight bytes, like an ordinary consumer record.</summary>
        struct CostGun
        {
            public float AmmoMax;
            public int Rpm;
        }

        /// <summary>The value in every record is the same — the sum shows that the loop was not thrown away.</summary>
        const int Rpm = 4242;

        string _dir;

        // Fields and not locals: the measured loop must not read a closure field on every turn.
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

        // ------------------------------------------------------------- the rig

        /// <summary>
        /// The file cycle: a domain of <paramref name="records"/> records of one type. No assets are
        /// needed — what must be measured is the price of a read and not the price of a rebuild.
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

        // ------------------------------------------------------------- the loops

        /// <summary>
        /// The floor of the rig: the same walk over the offset array with no read at all. Everything
        /// else is to be read as "this plus that much".
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

        /// <summary>The release path: a pure reinterpretation at an offset, no checks at all.</summary>
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

        /// <summary>The editor path: <c>Read</c> with everything that stands behind ENABLE_UNITY_COLLECTIONS_CHECKS.</summary>
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

        // ------------------------------------------------------------- the stopwatch

        /// <summary>The best of three after a warm-up: the minimum is robust against outside machine noise.</summary>
        static double NsPerRead(Func<int, long> pass, int iterations, long expectedSum)
        {
            pass(Math.Max(1024, iterations / 10));

            var best = double.MaxValue;
            for (var attempt = 0; attempt < 3; attempt++)
            {
                var watch = Stopwatch.StartNew();
                var sum = pass(iterations);
                watch.Stop();

                Assert.That(sum, Is.EqualTo(expectedSum), "the measuring loop read something other than what was written");

                var ns = watch.Elapsed.TotalMilliseconds * 1e6 / iterations;
                if (ns < best)
                    best = ns;
            }

            return best;
        }

        // ------------------------------------------------------------- the measurements

        [Test]
        public void The_price_of_a_read_in_the_editor_broken_down_by_layer()
        {
            const int records = 4096;
            const int iterations = 1_000_000;

            var withDebug = Build(records, true, out var debugOffsets);
            var noDebug = Build(records, false, out var plainOffsets);

            double loop, raw, checksOnly, checksAndDebug;

            Open(noDebug, plainOffsets);
            try
            {
                Assert.That(_blob.HasDebug, Is.False, "this file was assembled without a debug contour");
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
                Assert.That(_blob.HasDebug, Is.True, "and this one with a contour");
                checksAndDebug = NsPerRead(PassRead, iterations, (long)Rpm * iterations);
            }
            finally
            {
                Close();
            }

            UnityEngine.Debug.Log(
                $"Blobcheg, the price of one read in the editor ({records} records, {iterations} reads, best of three):\n" +
                $"  rig floor (walk without a read) : {loop:F2} ns\n" +
                $"  reinterpretation without checks : {raw:F2} ns   (the release player path)\n" +
                $"  Read, file without a contour    : {checksOnly:F2} ns   (+{checksOnly - raw:F2} — alignment and bounds)\n" +
                $"  Read, file with a contour       : {checksAndDebug:F2} ns   (+{checksAndDebug - checksOnly:F2} — binary search and type check)\n" +
                $"  editor against release, in all  : x{(raw > 0 ? checksAndDebug / raw : 0):F1}");
        }

        [Test]
        public void The_price_of_the_debug_contour_grows_with_the_number_of_records()
        {
            const int iterations = 500_000;
            var sizes = new[] { 1, 64, 1024, 16384, 65536 };
            var report = $"Blobcheg, the price of a read with the debug contour by base size ({iterations} reads):\n";

            foreach (var records in sizes)
            {
                var file = Build(records, true, out var offsets);

                Open(file, offsets);
                try
                {
                    var raw = NsPerRead(PassRaw, iterations, (long)Rpm * iterations);
                    var read = NsPerRead(PassRead, iterations, (long)Rpm * iterations);
                    report += $"  {records,6} records: Read {read,7:F2} ns, reinterpretation {raw,6:F2} ns, " +
                              $"contour and checks +{read - raw:F2} ns\n";
                }
                finally
                {
                    Close();
                }
            }

            UnityEngine.Debug.Log(report);
        }

        /// <summary>
        /// Both read checks call generic intrinsics: the bounds call <c>SizeOf&lt;T&gt;</c>, the type
        /// check calls <c>GetHashCode32&lt;T&gt;</c>. In a bursted job those are folded constants, in the
        /// editor on Mono they are calls on every read. Without this measurement the constant part of
        /// the contour's price would be charged to the binary search, which barely exists when the base
        /// holds a single record.
        /// </summary>
        [Test]
        public void The_price_of_the_generic_intrinsics_in_the_editor()
        {
            const int iterations = 1_000_000;

            var sizeOf = NsPerRead(PassSizeOf, iterations, (long)UnsafeUtility.SizeOf<CostGun>() * iterations);
            var hash = NsPerRead(PassTypeHash, iterations,
                unchecked((long)(uint)BurstRuntime.GetHashCode32<CostGun>()) * iterations);

            UnityEngine.Debug.Log(
                $"Blobcheg, the price of the generic intrinsics in the editor ({iterations} calls, best of three):\n" +
                $"  UnsafeUtility.SizeOf<T>()      : {sizeOf:F2} ns   (called from the bounds check)\n" +
                $"  BurstRuntime.GetHashCode32<T>(): {hash:F2} ns   (called from the type check)\n" +
                $"  together per read              : {sizeOf + hash:F2} ns — under Burst these are constants, in the editor they are not");
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
