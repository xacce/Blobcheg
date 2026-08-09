using System;
using NUnit.Framework;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;

namespace Blobcheg.PatchTests
{
    /// <summary>
    /// Scenarios that do not happen. They are exactly what uncovers the implicit assumptions: where a
    /// reasonable developer will not walk, nobody checked the assumption.
    /// </summary>
    public sealed unsafe class AbsurdTests : PatchFixture
    {
        [Test]
        public void The_patch_has_no_right_to_touch_a_single_byte_inside_the_base_itself()
        {
            // The FILE holds a record that itself consists of a reference. If the patch walks not only
            // the memory of components but the content of records as well, it spoils the base — and
            // spoils it for everyone who reads by the old path.
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

            // And the component slot is patched while the reference nested in the record is not.
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

            // Absurd: we register a SECOND domain at the address of a record inside the first one. The
            // registry knows nothing about overlapping buffers — the question is who owns the address now.
            var parasite = BlobchegNaming.NameHash("IPatchParasite");
            BlobchegBases.Register(parasite, (byte*)address, BlobchegFormat.HeaderSize * 2);

            try
            {
                // The slot carries ITS OWN domain, so the folding is obliged to be computed from its own
                // base and not from whoever registered last.
                Assert.That(BlobchegBases.TryUnresolve(hot.Key, address, out var mine),
                    Is.EqualTo(BlobchegRebase.Patched));
                Assert.That(mine, Is.EqualTo((ulong)file["gun"]),
                    "the offset is obliged to be computed from the base of its own domain and not from the last registered one");

                Assert.That(BlobchegBases.TryUnresolve(parasite, address, out var theirs),
                    Is.EqualTo(BlobchegRebase.Patched));
                Assert.That(theirs, Is.Zero, "for the parasite the same address is the start of its own buffer");

                // The promise holds all the same: the reverse pass of the world hands out the offset of its own base.
                var bytes = Save();
                Assert.That(Contains(bytes, address), Is.False);
            }
            finally
            {
                BlobchegBases.Unregister(parasite, (byte*)address);
            }
        }

        // The plan (line 48) allowed exactly two outcomes: "EITHER an explicit refusal at
        // installation/patch time, OR it is patched like everything else". The implementation chose the
        // first. The type walk now takes ISharedComponentData into the candidates as well — not to
        // register it but to notice a slot in it: the type is not registered, and the trouble leaves as a
        // line in BlobchegPatchTableBuilder.Diagnostics.
        //
        // It does not go into the log on purpose: the walk sees every type in the process, this fixture
        // included, and a Debug.LogError at installation time would mean an error in the consumer's
        // console right after installing the package — about a test of the package itself. The refusal
        // sounds where it matters: on reading Value.
        //
        // The inadmissible middle of the plan — "silently skipped and then serialised as a process
        // address" — is closed from both sides: since the slot is not patched at all, there is nowhere
        // for a process address to come from in the file, and the reverse pass spoils nothing here.
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

            // The main point of the plan: a process address does not travel into the file. It cannot get
            // there either — there was no patch and the slot still holds an offset.
            var bytes = Save();
            Assert.That(Contains(bytes, hot.AddressOf(offset)), Is.False);
        }

        [Test]
        public void One_chunk_with_two_generations_at_once()
        {
            // Exactly what the live path produces: some of the chunk's entities have already been through
            // the patch, some arrived raw with a change set, and the base was rebuilt between those two
            // events.
            var first = HotFile(ammo: 1f, rpm: 11);
            Raise(first);
            var offset = first["gun"];

            var old = Gun(offset);
            Patch();

            var gen2 = Raise(HotFile(ammo: 2f, rpm: 22));
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
            // A developer read that after the patch the slot holds an address and decided to put it there
            // themselves — at bake time, from a value obtained in the editor. Such a world must not travel
            // into a file at all: a process address does not survive even a restart of the editor.
            var file = HotFile();
            var hot = Raise(file);
            var address = hot.AddressOf(file["gun"]);

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
            // Absurd by construction: entities with references to EVERY byte of a record, the middle
            // included. What is checked is not the meaning but that none of them gives a
            // non-deterministic answer.
            //
            // The plan (line 10, "An offset off the alignment is obliged to be rejected") demands an
            // explicit error from every unaligned offset — so out of the eight bytes of a record exactly
            // one has the right to become an address, its own start. The implementation answers exactly
            // that way: BadOffset on the other seven. Determinism does not suffer from that but grows
            // stronger: the only accepted answer is the only lawful one.
            var file = HotFile();
            var hot = Raise(file);
            var start = file["gun"];

            Assume.That(start % BlobchegFormat.RecordAlign, Is.Zero,
                "the start of the record is not aligned — the test is checking the wrong boundary");

            // The aligned entity is created LAST: if the failure of the very first byte swallowed the
            // rest, it would be left unpatched and that would be visible.
            var broken = new Entity[8];
            for (var i = 1u; i < 8; i++)
                broken[i] = Gun(start + i);

            var whole = Gun(start);

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
            // No base, no references, no entities — and still not a single exception: the live path is
            // called for EVERY applied change set, in projects without Blobcheg at all included.
            Assert.DoesNotThrow(() => Patch());
            Assert.That(BlobchegPatchErrors.HasAny, Is.False);

            Assert.DoesNotThrow(() => Save());
            Assert.That(BlobchegPatchErrors.HasAny, Is.False);
        }

        [Test]
        public void A_reference_to_a_record_right_on_top_of_the_debug_contour()
        {
            // One more address that does not happen: the offset of the debug section itself. It is
            // aligned, it is past the header and it is inside the buffer — by the bounds it is
            // indistinguishable from a record. Only the contour itself can reject it, and the plan
            // (line 19) demands exactly that: "inside the bounds — a rejection by record type (the debug
            // contour). Never a silent read of someone else's bytes."
            //
            // The implementation now does exactly that: having got the address, the patch asks the
            // contour whether a record of the declared type starts there and fails with the WrongRecord
            // code. The check that used to live only on the old path (BlobchegBlob.Read) made it to the
            // new one.
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

            // The old path refuses at the same address in exactly the same way — both paths say one thing.
            Assert.Throws<InvalidOperationException>(() => Copy(hot.Blob.Read<PatchGun>(contour)),
                "Read knows that there is no record at this offset");
        }
    }
}
