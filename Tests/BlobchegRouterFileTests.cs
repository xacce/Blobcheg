using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using Unity.Collections;

namespace Blobcheg.Tests
{
    /// <summary>
    /// The router file on its own: the layout, the popcount lookup, the bounds. There are no assets and
    /// no codegen here — what is proven is exactly the binary and reading from it.
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

        /// <summary>A load that is obliged to throw: we free the buffer ourselves — ownership never passed to it.</summary>
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
        public void A_row_hands_out_offsets_by_bit_and_not_by_order_in_the_file()
        {
            const int domains = 8;
            var hash = HashOf(domains);

            var writer = BlobchegRouterWriter.Open(_dir, "R", domains, hash);
            // The cells arrive shuffled on purpose: in the file they are obliged to land by ascending bit.
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
                Assert.That(b.Mask, Is.Zero, "a node may have joined the router without writing into any of its bases");
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
        public void A_mask_of_any_width_is_read_including_the_top_bit(int domains, int top)
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
                Assert.That(row.Offset(top), Is.EqualTo(32u), "the top bit lies inside a mask of the chosen width");
            }
            finally
            {
                router.Dispose();
            }
        }

        [Test]
        public void An_unknown_id_throws_while_TryGet_answers_false()
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
        public void A_file_built_for_a_different_set_of_bases_does_not_load()
        {
            const int domains = 4;
            var writer = BlobchegRouterWriter.Open(_dir, "R", domains, HashOf(domains));
            writer.Append("a", Row((0, 48)));
            writer.Flush();

            RequireThrows("R", domains, HashOf(domains) ^ 1, "a different set of bases");
            RequireThrows("R", domains + 1, HashOf(domains), "bases");
        }

        [Test]
        public void A_base_and_a_router_are_not_mixed_up()
        {
            var domain = BlobchegWriter.Open(_dir, "D");
            domain.Append(new BlobchegRecord("T", "k", 0, "n", new byte[16]));
            domain.Flush();

            var router = BlobchegRouterWriter.Open(_dir, "R", 2, HashOf(2));
            router.Append("a", Row((0, 64)));
            router.Flush();

            RequireThrows("D", 2, HashOf(2), "router");

            var buffer = BlobchegBuffer.From(Bytes("R"), Allocator.Persistent);
            try
            {
                var asBase = Assert.Throws<InvalidOperationException>(() => new BlobchegBlob(buffer, "R"));
                StringAssert.Contains("base", asBase.Message);
            }
            finally
            {
                buffer.Dispose();
            }
        }

        [Test]
        public void A_file_of_a_foreign_router_does_not_load_under_this_name()
        {
            const int domains = 2;
            var hash = HashOf(domains);

            var writer = BlobchegRouterWriter.Open(_dir, "R", domains, hash);
            writer.Append("a", Row((0, 48)));
            writer.Flush();

            // The files were swapped: the content is whole, the integrity agrees, and the router is wrong.
            File.Copy(Path.Combine(_dir, BlobchegNaming.FileName("R")),
                Path.Combine(_dir, BlobchegNaming.FileName("Alien")));

            RequireThrows("Alien", domains, hash, "another router");
        }

        [Test]
        public void An_id_of_a_foreign_router_is_rejected_by_the_tag()
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
                Assert.That(alien.Index, Is.EqualTo(router.IdAt(0).Index), "the row is the same — the tag differs");
                Assert.That(alien, Is.Not.EqualTo(router.IdAt(0)));

                Assert.Throws<InvalidOperationException>(() => router.Get(alien),
                    "an id of a neighbouring router falls into the range of this one — only the tag tells them apart");
                Assert.That(router.TryGet(alien, out _), Is.False);
            }
            finally
            {
                router.Dispose();
                other.Dispose();
            }
        }

        [Test]
        public void A_default_id_does_not_resolve()
        {
            const int domains = 2;
            var hash = HashOf(domains);

            var writer = BlobchegRouterWriter.Open(_dir, "R", domains, hash);
            writer.Append("a", Row((0, 48)));
            writer.Flush();

            Assert.That(default(BlobchegId).IsValid, Is.False, "a zero-initialised field means \"not set\"");

            var router = Load("R", domains, hash);
            try
            {
                Assert.Throws<InvalidOperationException>(() => router.Get(default),
                    "otherwise a forgotten field would quietly lead to the first node of the router");
                Assert.That(router.TryGet(default, out _), Is.False);
            }
            finally
            {
                router.Dispose();
            }
        }

        [Test]
        public void Identical_content_does_not_rewrite_the_file()
        {
            const int domains = 2;
            var hash = HashOf(domains);

            var first = BlobchegRouterWriter.Open(_dir, "R", domains, hash);
            first.Append("a", Row((0, 48)));
            first.Flush();
            Assert.That(first.FileChanged, Is.True, "there was no file yet");

            var again = BlobchegRouterWriter.Open(_dir, "R", domains, hash);
            again.Append("a", Row((0, 48)));
            again.Flush();
            Assert.That(again.FileChanged, Is.False, "the same content means the file is not touched, otherwise everything gets rebaked");
            Assert.That(again.ContentHash, Is.EqualTo(first.ContentHash));
        }

        [Test]
        public void A_base_named_twice_and_a_bit_past_the_ceiling_both_throw()
        {
            var writer = BlobchegRouterWriter.Open(_dir, "R", 4, HashOf(4));

            Assert.Throws<InvalidOperationException>(() => writer.Append("a", Row((1, 16), (1, 32))));
            Assert.Throws<ArgumentOutOfRangeException>(() => writer.Append("b", Row((4, 16))));
            Assert.Throws<ArgumentOutOfRangeException>(() => BlobchegRouterFormat.MaskWidthFor(65));
        }

        [Test]
        public void The_debug_contour_names_the_node_by_id()
        {
            const int domains = 2;
            var hash = HashOf(domains);

            var writer = BlobchegRouterWriter.Open(_dir, "R", domains, hash);
            writer.Append("Pistol", Row((0, 48)));
            writer.Append("Armor", Row((1, 64)));
            writer.Flush(true);

            var router = Load("R", domains, hash);
            try
            {
                Assert.That(router.HasDebug, Is.True);
                Assert.That(router.Describe(router.IdAt(0)), Is.EqualTo("Pistol"));
                Assert.That(router.Describe(router.IdAt(1)), Is.EqualTo("Armor"));
            }
            finally
            {
                router.Dispose();
            }
        }
    }
}
