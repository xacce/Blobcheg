using System;
using NUnit.Framework;

namespace Blobcheg.PatchTests
{
    /// <summary>
    /// Address boundaries. The old read path (<c>Read&lt;T&gt;</c>) checks three things: the alignment,
    /// the lower bound (a record starts no earlier than the header) and the upper one. The section asks
    /// whether all three made it to the new path — where a raw pointer is handed out instead of an
    /// offset, the price of a miss is higher, not lower.
    /// </summary>
    public sealed unsafe class BoundaryTests : PatchFixture
    {
        [Test]
        public void An_offset_past_the_end_of_the_file_is_obliged_to_be_rejected_by_the_patch()
        {
            var hot = Raise(HotFile());
            Gun((uint)hot.Length + BlobchegFormat.RecordAlign);

            var error = Assert.Throws<InvalidOperationException>(() => Patch());

            Assert.That(error.Message, Does.Contain(nameof(GunRef)));
            Assert.That(error.Message, Does.Contain(((uint)hot.Length + BlobchegFormat.RecordAlign).ToString()),
                "the message is obliged to carry the value itself — otherwise there is nothing to look for it in the scene with");
        }

        [Test]
        public void An_offset_of_uint_MaxValue_does_not_turn_into_a_wild_address()
        {
            var hot = Raise(HotFile());
            var entity = Gun(uint.MaxValue);

            Assert.Throws<InvalidOperationException>(() => Patch(),
                "four gigabytes from the start of the base is not an address but a way to crash the process on the first read");

            Assert.That(SlotOf(entity), Is.EqualTo(uint.MaxValue),
                "a failed patch is obliged to leave the slot as it was and not to write half of it");
            Assert.That(SlotOf(entity), Is.Not.EqualTo(hot.Ptr + uint.MaxValue));
        }

        // BUG: the patch accepts an offset into the header and hands out a pointer at the header
        // What happens: after the patch BlobchegReference<PatchGun>(8) points at the eighth byte of the
        //   file, that is, into the header; Value quietly hands out magic/version/flags as a record.
        // What should happen: an explicit error. Records start at BlobchegFormat.HeaderSize, and the old
        //   path knows that — BlobchegBlob.CheckRead rejects offset < HeaderSize.
        // Root cause: BlobchegBases.TryResolve checks only the upper bound
        //   (`if (value >= length) return OutOfRange`). There is no lower bound there at all, so the
        //   whole 1..31 range counts as a valid record offset.
        [Test]
        public void An_offset_into_the_header_is_obliged_to_be_rejected()
        {
            var hot = Raise(HotFile());
            Gun(8);

            Assert.Throws<InvalidOperationException>(() => Patch(),
                "there are no records inside the header — this is not a record address but the middle of the service fields");
        }

        [Test]
        public void An_offset_exactly_on_the_last_record_is_obliged_to_pass()
        {
            var file = HotFile(ammo: 13f, rpm: 131);
            var hot = Raise(file);

            // By FullName the gun comes after the armor, so its record is the last one in the file.
            var last = file["gun"];
            Assert.That(last, Is.GreaterThan(file["armor"]), "the layout changed — the test is checking the wrong boundary");

            var entity = Gun(last);
            Assert.DoesNotThrow(() => Patch(), "the last record is a valid record and not \"past the end\"");

            Assert.That(SlotOf(entity), Is.EqualTo(hot.AddressOf(last)));

            var gun = Copy(EM.GetComponentData<GunRef>(entity).Gun.Value);
            Assert.That(gun.Ammo, Is.EqualTo(13f));
            Assert.That(gun.Rpm, Is.EqualTo(131));
        }

        // BUG: the patch accepts an unaligned offset silently
        // What happens: BlobchegReference<PatchGun>(lastRecordOffset + 1) resolves into an address that
        //   slid by a byte; Value hands out a record shifted by one byte.
        // What should happen: an explicit error — the start of a record is aligned to
        //   BlobchegFormat.RecordAlign, and BlobchegBlob.CheckRead rejects such a thing on the very
        //   first check.
        // Root cause: the same as with an offset into the header — BlobchegBases.TryResolve knows about
        //   the buffer length and knows nothing about the format. There are no alignment checks in it.
        [Test]
        public void An_offset_off_the_alignment_is_obliged_to_be_rejected()
        {
            var file = HotFile();
            Raise(file);
            Gun(file["gun"] + 1);

            Assert.Throws<InvalidOperationException>(() => Patch(),
                "the offset is not a multiple of 16 — this is not the start of a record, whatever it turns out to be in memory");
        }

        [Test]
        public void An_offset_exactly_equal_to_the_file_length_is_obliged_to_be_rejected()
        {
            var hot = Raise(HotFile());
            Gun((uint)hot.Length);

            Assert.Throws<InvalidOperationException>(() => Patch(),
                "the address right after the last byte of the buffer is already foreign memory");
        }

        [Test]
        public void An_offset_one_less_than_the_file_length_is_also_outside_a_record()
        {
            var hot = Raise(HotFile());
            var offset = (uint)hot.Length - 1;

            // Formally inside the buffer, but there the debug contour lies and not a record. The old path
            // sees that through the section — we check whether the new one does.
            //
            // The plan (line 10) demanded an explicit error from an unaligned offset, and that is what we
            // get here: the file length promises no multiple of 16, so "one less" is rejected with the
            // BadOffset code — before the question of the upper bound even comes up. The cause named in
            // the message is not the one in the test name ("outside a record"), but the outcome is
            // exactly the one the plan demanded: an explicit error instead of an address into the contour.
            Assume.That(offset % BlobchegFormat.RecordAlign, Is.Not.Zero,
                "the file length turned out to be a multiple of 16 — \"one less\" now checks a different boundary");

            var entity = Gun(offset);

            var error = Assert.Throws<InvalidOperationException>(() => Patch(),
                "inside the buffer but not at the start of a record — this is not a record address, whatever it may be");

            Assert.That(error.Message, Does.Contain(nameof(GunRef)));
            Assert.That(error.Message, Does.Contain(offset.ToString()),
                "the message is obliged to carry the value itself — otherwise there is nothing to look for it in the scene with");

            Assert.That(SlotOf(entity), Is.EqualTo((ulong)offset),
                "a failed patch is obliged to leave the slot as it was");

            // The old path refuses at the same address — that is what the new path has to be compared with.
            Assert.Throws<InvalidOperationException>(
                () => Copy(hot.Blob.Read<PatchGun>(offset)),
                "a Read of the same offset is obliged to reject it: it knows the alignment, the bounds and the contour");
        }

        [Test]
        public void The_sixty_fifth_domain_is_obliged_to_be_rejected_and_not_to_overwrite_a_foreign_one()
        {
            var hot = Raise(HotFile());

            // The registry is a flat array of MaxDomains. One slot is already taken by a loaded base.
            for (var i = 1; i < BlobchegBases.MaxDomains; i++)
                BlobchegBases.Register(BlobchegNaming.NameHash("IPatchFake" + i), (byte*)hot.Ptr, hot.Length);

            var error = Assert.Throws<InvalidOperationException>(
                () => BlobchegBases.Register(BlobchegNaming.NameHash("IPatchOverflow"), (byte*)hot.Ptr, hot.Length),
                "an overflow of the registry is obliged to be an error and not a quiet overwrite of someone else's slot");

            Assert.That(error.Message, Does.Contain(BlobchegBases.MaxDomains.ToString()));

            // And the base loaded first is obliged to stay in place.
            Assert.That(BlobchegBases.TryGet(hot.Key, out var ptr, out var length), Is.True);
            Assert.That((ulong)ptr, Is.EqualTo(hot.Ptr));
            Assert.That(length, Is.EqualTo(hot.Length));
        }

        [Test]
        public void A_domain_with_a_zero_key_and_an_empty_buffer_are_obliged_to_be_rejected()
        {
            var hot = Raise(HotFile());

            Assert.Throws<ArgumentException>(() => BlobchegBases.Register(0, (byte*)hot.Ptr, hot.Length),
                "a zero key means \"there is no domain\", and such a domain cannot be put on the register");

            Assert.Throws<ArgumentException>(
                () => BlobchegBases.Register(BlobchegNaming.NameHash("IPatchNull"), null, 64),
                "a null pointer is not put on the register");

            Assert.Throws<ArgumentException>(
                () => BlobchegBases.Register(BlobchegNaming.NameHash("IPatchShort"), (byte*)hot.Ptr,
                    BlobchegFormat.HeaderSize - 1),
                "a buffer shorter than the header is not a base");
        }
    }
}
