using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;

namespace Blobcheg.AdvancedTests
{
    /// <summary>
    /// Reading from jobs, reentrancy of the rebuild and volume: a hundred thousand router rows, a record
    /// of megabytes, exactly one node and a hundred thousand reads in a row.
    /// </summary>
    public sealed class ConcurrencyAndVolumeTests : AdvancedFixture
    {
        /// <summary>
        /// Reading a base straight from a bursted job — what the package exists for in the first place. If
        /// that does not compile or does not schedule, everything else is beside the point.
        /// </summary>
        [BurstCompile]
        struct AdvReadJob : IJobParallelFor
        {
            public AdvCombatDb Db;

            [ReadOnly] public NativeArray<uint> Offsets;

            [WriteOnly] public NativeArray<int> Rpm;

            public void Execute(int index) => Rpm[index] = Db.Read<AdvGun>(Offsets[index]).Rpm;
        }

        [Test]
        public void Parallel_reading_from_jobs_gives_the_same_values()
        {
            var nodes = new List<AdvComboNodeSo>();
            for (var i = 0; i < 16; i++)
            {
                var node = Node<AdvComboNodeSo>("Combo" + i.ToString("D2"));
                node.rpm = 100 + i;
                Dirty(node);
                nodes.Add(node);
            }

            Rebuild();

            var offsets = new NativeArray<uint>(nodes.Count, Allocator.TempJob);
            var rpm = new NativeArray<int>(nodes.Count, Allocator.TempJob);
            var db = Combat();
            try
            {
                for (var i = 0; i < nodes.Count; i++)
                    offsets[i] = OffsetOf(nodes[i], "IAdvCombat");

                new AdvReadJob { Db = db, Offsets = offsets, Rpm = rpm }.Schedule(nodes.Count, 4).Complete();

                for (var i = 0; i < nodes.Count; i++)
                {
                    Assert.That(rpm[i], Is.EqualTo(nodes[i].rpm),
                        $"node {nodes[i].name} was read from the job with the wrong value");
                }
            }
            finally
            {
                offsets.Dispose();
                rpm.Dispose();
                db.Dispose();
            }
        }

        /// <summary>
        /// A node may touch the AssetDatabase with anything inside its <c>Write</c> and enter a rebuild
        /// from the middle of a rebuild. The guard stands on the rebuild itself and not on the import
        /// hook: a nested run goes over a half-filled collector and half-handed-out ids, and "the file is
        /// built" after it means nothing.
        /// </summary>
        [Test]
        public void A_rebuild_from_the_middle_of_a_rebuild_is_rejected()
        {
            Node<AdvReentrantNodeSo>("Reentrant");
            AdvReentrantNodeSo.Forget();

            Rebuild();

            Assert.That(AdvReentrantNodeSo.Reentered, Is.Zero,
                "the node called the rebuild from Write and the rebuild allowed it");
        }

        [Test]
        public void A_hundred_thousand_router_rows_are_addressable()
        {
            const int rows = 100_000;
            const int domains = 3;

            var pairs = Enumerable.Range(0, domains)
                .Select(i => new KeyValuePair<string, string>("Domain" + i, "member" + i))
                .ToList();

            var width = BlobchegRouterFormat.MaskWidthFor(domains);
            var layout = BlobchegRouterFormat.LayoutHash(pairs, width);

            var writer = BlobchegRouterWriter.Open(Scratch, "AdvVolumeRouter", domains, layout);
            for (var i = 0; i < rows; i++)
            {
                // Every third row is empty: holes in the mask are obliged to survive at volume the same way
                // as with two rows.
                writer.Append("row" + i, i % 3 == 0
                    ? Array.Empty<BlobchegRouterCell>()
                    : new[] { new BlobchegRouterCell(i % domains, (uint)(BlobchegFormat.HeaderSize + i * 16)) });
            }

            writer.Flush();

            var blob = new BlobchegRouterBlob(
                BlobchegBuffer.From(File.ReadAllBytes(writer.FilePath), Allocator.Persistent),
                "AdvVolumeRouter", domains, layout);

            try
            {
                Assert.That(blob.Count, Is.EqualTo(rows));

                for (var i = 0; i < rows; i++)
                {
                    var row = blob.Get(blob.IdAt((uint)i));
                    if (i % 3 == 0)
                    {
                        Assert.That(row.Mask, Is.EqualTo(0ul), $"row {i} is obliged to stay empty");
                        continue;
                    }

                    Assert.That(row.Offset(i % domains), Is.EqualTo((uint)(BlobchegFormat.HeaderSize + i * 16)),
                        $"row {i} handed out a foreign offset");
                }

                Assert.Throws<InvalidOperationException>(() => blob.Get(blob.IdAt((uint)rows)));
            }
            finally
            {
                blob.Dispose();
            }
        }

        [Test]
        public void A_record_of_megabytes_outlives_the_round_trip()
        {
            const int megabytes = 2;

            var huge = Node<AdvHugeNodeSo>("Huge");
            huge.megabytes = megabytes;
            Dirty(huge);
            Node<AdvLooseNodeSo>("Small");

            Rebuild();

            var offset = (int)OffsetOf(huge, "IAdvLoose");
            var size = megabytes * 1024 * 1024;
            var file = Bytes("IAdvLoose");

            Assert.That(file.Length, Is.GreaterThanOrEqualTo(offset + size),
                "a multi-megabyte record is obliged to land in the file whole");
            Assert.That(file[offset], Is.EqualTo((byte)0), "the first byte of the record");
            Assert.That(file[offset + 4096], Is.EqualTo((byte)1), "and the mark inside it");
            Assert.That(file[offset + size - 1], Is.EqualTo((byte)0xFE), "and its last byte too");
        }

        [Test]
        public void Exactly_one_node_is_addressed_by_row_zero()
        {
            var only = Node<AdvColdOnlyNodeSo>("Only");
            only.tier = 77;
            Dirty(only);

            Rebuild();

            var id = IdOf(only, AdvRouter.RouterName);
            Assert.That(id.Index, Is.EqualTo(0u), "the only node is row zero");
            Assert.That(id.IsValid, Is.True, "but its id is not zero: the tag tells row zero from \"not assigned\"");

            var router = Router();
            var cold = Cold();
            try
            {
                Assert.That(router.Count, Is.EqualTo(1));
                Assert.That(cold.Read<AdvColdInfo>(router.GetCold(id)).Tier, Is.EqualTo(77));
                Assert.Throws<InvalidOperationException>(() => router.Get(router.IdAt(1)));
            }
            finally
            {
                router.Dispose();
                cold.Dispose();
            }
        }

        [Test]
        public void A_hundred_thousand_reads_in_a_row_are_stable()
        {
            var node = Node<AdvComboNodeSo>("Combo");
            node.rpm = 4242;
            Dirty(node);
            Rebuild();

            var offset = OffsetOf(node, "IAdvCombat");
            var db = Combat();
            try
            {
                const int reads = 100_000;

                // A warm-up: the first read pays for the JIT of the method, and over a hundred thousand
                // turns that payment would smear across the measurement.
                for (var i = 0; i < 1024; i++)
                {
                    if (db.Read<AdvGun>(offset).Rpm != 4242)
                        Assert.Fail("the warm-up read something other than what was written");
                }

                var wrong = 0;
                var watch = System.Diagnostics.Stopwatch.StartNew();
                for (var i = 0; i < reads; i++)
                {
                    if (db.Read<AdvGun>(offset).Rpm != 4242)
                        wrong++;
                }

                watch.Stop();

                Assert.That(wrong, Is.Zero, "a read is a pure reinterpretation; it has no state between calls");

                // Not a threshold but a timing: what a read costs in the editor through the generated
                // facade, on a file from a real rebuild. Taken before an AtomicSafetyHandle lands in
                // CheckRead, so that whose price is whose stays visible afterwards. The breakdown by layer
                // is in Tests/BlobchegReadCostTests.
                UnityEngine.Debug.Log(
                    $"Blobcheg: {reads} reads through the facade in {watch.Elapsed.TotalMilliseconds:F2} ms — " +
                    $"{watch.Elapsed.TotalMilliseconds * 1e6 / reads:F2} ns per read " +
                    $"(editor, debug contour {(db.HasDebug ? "present" : "absent")})");
            }
            finally
            {
                db.Dispose();
            }
        }
    }
}
