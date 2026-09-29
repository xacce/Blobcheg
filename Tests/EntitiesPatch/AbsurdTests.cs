using System;
using NUnit.Framework;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;

namespace Blobcheg.PatchTests
{
    public sealed unsafe class AbsurdTests : PatchFixture // impossible scenarios expose unchecked assumptions
    {
        [Test]
        public void The_patch_has_no_right_to_touch_a_single_byte_inside_the_base_itself()
        {
            // The patch walks component memory only; walking record content would corrupt the base.
            var file = Domain(nameof(IPatchHot))
                .Add("gun", new PatchGun { Ammo = 1f, Rpm = 1 })
                .Add("holder", new PatchRefRecord
                {
                    Inner = new BlobchegReference<PatchGun>(BlobchegFormat.HeaderSize),
                    Tag = 0x0BAD_F00D,
                })
                .Seal();

            var hot = Raise(file);

            var before = new byte[hot.Length];
            fixed (byte* dst = before)
                UnsafeUtility.MemCpy(dst, (byte*)hot.Ptr, hot.Length);

            var entity = EM.CreateEntity();
            EM.AddComponentData(entity, new RecordRef
            {
                Record = new BlobchegReference<PatchRefRecord>(file["holder"]),
            });

            Patch();
            Save();

            var after = new byte[hot.Length];
            fixed (byte* dst = after)
                UnsafeUtility.MemCpy(dst, (byte*)hot.Ptr, hot.Length);

            CollectionAssert.AreEqual(before, after,
                "the patch changed the bytes of the base itself: it is obliged to walk the memory of components, " +
                "while the content of records is a question of trust, as with any other read");

            Assert.That(EM.GetComponentData<RecordRef>(entity).Record.Data.Value,
                Is.EqualTo(hot.AddressOf(file["holder"])));

            var record = Copy(EM.GetComponentData<RecordRef>(entity).Record.Value);
            Assert.That(record.Tag, Is.EqualTo(0x0BAD_F00D));
            Assert.That(record.Inner.Data.Value, Is.EqualTo((ulong)BlobchegFormat.HeaderSize),
                "the reference INSIDE the record stayed an offset — the patch does not climb into the base");
        }

        [Test]
        public void A_base_registered_at_the_address_of_a_foreign_record_answers_deterministically()
        {
            var file = HotFile();
            var hot = Raise(file);
            var entity = Gun(file["gun"]);

            Patch();
            var address = SlotOf(entity);

            // A second domain registered at a record address inside the first: who owns the address now?
            var parasite = BlobchegNaming.NameHash("IPatchParasite");
            BlobchegBases.Register(parasite, (byte*)address, BlobchegFormat.HeaderSize * 2);

            try
            {
                Assert.That(BlobchegBases.TryUnresolve(hot.Key, address, out var mine),
                    Is.EqualTo(BlobchegRebase.Patched));
                Assert.That(mine, Is.EqualTo((ulong)file["gun"]),
                    "the offset is obliged to be computed from the base of its own domain and not from the last registered one");

                Assert.That(BlobchegBases.TryUnresolve(parasite, address, out var theirs),
                    Is.EqualTo(BlobchegRebase.Patched));
                Assert.That(theirs, Is.Zero, "for the parasite the same address is the start of its own buffer");

                var bytes = Save(); // the reverse pass still writes offsets of its own base
                Assert.That(Contains(bytes, address), Is.False);
            }
            finally
            {
                BlobchegBases.Unregister(parasite, (byte*)address);
            }
        }

        // Shared slots are refused via Diagnostics, not the log: a LogError would hit every consumer.
        [Test]
        public void A_slot_in_a_shared_component_is_either_patched_or_rejected_out_loud()
        {
            var file = HotFile();
            var hot = Raise(file);
            var offset = file["gun"];

            string complaint = null;
            foreach (var diagnostic in BlobchegPatchTableBuilder.Diagnostics)
                if (diagnostic.Contains(nameof(SharedRef)))
                    complaint = diagnostic;

            Assert.That(complaint, Is.Not.Null,
                "the table build is obliged to notice a slot in a shared component and name the cause: without " +
                "that line \"not patched\" and \"there is no such type at all\" are indistinguishable, and there will be nothing to investigate with");

            Assert.That(complaint, Does.Contain("BlobchegReference"),
                "and to name what exactly in this type is out of the patch's reach");

            var entity = EM.CreateEntity();
            EM.AddSharedComponent(entity, new SharedRef { Gun = new BlobchegReference<PatchGun>(offset) });

            Patch();

            var shared = EM.GetSharedComponent<SharedRef>(entity);

            Assert.That(shared.Gun.Data.Value, Is.EqualTo((ulong)offset),
                "the patch does not walk shared components — and since it said so, the slot is obliged to stay " +
                "exactly the offset that was put into it");
            Assert.That(shared.Gun.IsResolved, Is.False,
                "and not to lie that it is resolved");

            var bytes = Save(); // no patch, so no process address can reach the file
            Assert.That(Contains(bytes, hot.AddressOf(offset)), Is.False);
        }

        [Test]
        public void One_chunk_with_two_generations_at_once()
        {
            var first = HotFile(ammo: 1f, rpm: 11);
            Raise(first);
            var offset = first["gun"];

            var old = Gun(offset);
            Patch();

            var gen2 = Raise(HotFile(ammo: 2f, rpm: 22)); // live path: base rebuilt between patched and raw
            var fresh = Gun(offset);

            Patch();

            Assert.That(SlotOf(old), Is.EqualTo(gen2.AddressOf(offset)),
                "the old entity is obliged to move over onto the new generation");
            Assert.That(SlotOf(fresh), Is.EqualTo(gen2.AddressOf(offset)),
                "and the new one to resolve into the same one");

            Assert.That(Copy(EM.GetComponentData<GunRef>(old).Gun.Value).Rpm, Is.EqualTo(22));
            Assert.That(Copy(EM.GetComponentData<GunRef>(fresh).Gun.Value).Rpm, Is.EqualTo(22));
        }

        [Test]
        public void The_address_of_a_loaded_base_put_into_a_slot_by_hand()
        {
            var file = HotFile();
            var hot = Raise(file);
            var address = hot.AddressOf(file["gun"]); // hand-placed: must still save as an offset

            var entity = EM.CreateEntity();
            EM.AddComponentData(entity, new GunRef
            {
                Gun = new BlobchegReference<PatchGun> { Data = new BlobchegReferenceData { Value = address } },
            });

            Assert.DoesNotThrow(() => Patch(), "the address of a live base in a slot is already a valid state, the patch does not touch it");
            Assert.That(SlotOf(entity), Is.EqualTo(address));

            var bytes = Save();
            Assert.That(Contains(bytes, address), Is.False,
                "and it is obliged to travel into the file as an offset all the same");
        }

        [Test]
        public void A_world_with_an_entity_for_every_byte_of_a_record()
        {
            // Refs to every byte of a record: only the aligned start resolves, the other seven are BadOffset.
            var file = HotFile();
            var hot = Raise(file);
            var start = file["gun"];

            Assume.That(start % BlobchegFormat.RecordAlign, Is.Zero,
                "the start of the record is not aligned — the test is checking the wrong boundary");

            var broken = new Entity[8];
            for (var i = 1u; i < 8; i++)
                broken[i] = Gun(start + i);

            var whole = Gun(start); // created last: a swallowed failure would leave it unpatched

            var error = Assert.Throws<InvalidOperationException>(() => Patch(),
                "seven bytes out of eight are not the start of a record, and each is obliged to be rejected");

            Assert.That(error.Message, Does.Contain(nameof(GunRef)));

            Assert.That(SlotOf(whole), Is.EqualTo(hot.AddressOf(start)),
                "the aligned start of the record is the only one of the eight obliged to pass, and the failure " +
                "of its neighbours has no right to swallow it");

            for (var i = 1u; i < 8; i++)
                Assert.That(SlotOf(broken[i]), Is.EqualTo((ulong)(start + i)),
                    $"byte {i} was rejected — the slot is obliged to stay the number that was in it and not " +
                    "to turn into the address of the middle of a record");
        }

        [Test]
        public void Patching_a_world_without_a_single_loaded_domain_and_without_references()
        {
            Assert.DoesNotThrow(() => Patch()); // runs for every change set, even without Blobcheg
            Assert.That(BlobchegPatchErrors.HasAny, Is.False);

            Assert.DoesNotThrow(() => Save());
            Assert.That(BlobchegPatchErrors.HasAny, Is.False);
        }

        [Test]
        public void A_reference_to_a_record_right_on_top_of_the_debug_contour()
        {
            // The debug-contour offset passes the bounds; only the contour's record-type check can reject it.
            var file = HotFile();
            var hot = Raise(file);
            Assert.That(hot.Blob.HasDebug, Is.True, "the contour was not written — the test is checking the wrong thing");

            var contour = BlobchegFormat.AlignUp((uint)hot.Length - 1);
            if (contour >= (uint)hot.Length)
                contour = BlobchegFormat.AlignUp(file["gun"] + 16);

            Assume.That(contour, Is.LessThan((uint)hot.Length));

            Gun(contour);

            var error = Assert.Throws<InvalidOperationException>(() => Patch(),
                "there is no record at this offset, and the patch is obliged to see that");

            Assert.That(error.Message, Does.Contain(nameof(GunRef)));

            Assert.Throws<InvalidOperationException>(() => Copy(hot.Blob.Read<PatchGun>(contour)),
                "Read knows that there is no record at this offset");
        }
    }
}
