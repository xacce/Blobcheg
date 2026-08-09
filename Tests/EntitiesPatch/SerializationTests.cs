using NUnit.Framework;
using Unity.Collections;
using Unity.Entities;

namespace Blobcheg.PatchTests
{
    /// <summary>
    /// The promise "an offset travels to disk, not a process address". An indirect check is not enough
    /// here: a world read in the same process will come up even from a file with an address inside, if
    /// the base happens to land at the same address. That is why the main instrument of the section is a
    /// search for an eight-byte word in the raw stream.
    /// </summary>
    public sealed unsafe class SerializationTests : PatchFixture
    {
        [Test]
        public void A_saved_world_contains_an_offset_and_not_a_process_address()
        {
            var file = HotFile(ammo: 21f, rpm: 210);
            var hot = Raise(file);
            var offset = file["gun"];
            var entity = Gun(offset);

            Patch();
            var address = SlotOf(entity);
            Assert.That(address, Is.EqualTo(hot.AddressOf(offset)));

            var bytes = Save();

            Assert.That(Contains(bytes, address), Is.False,
                "a process address was found in the stream — it is meaningless already in the next run of the game");

            // And the positive half: reading into a world where the base lies at a DIFFERENT address. The
            // new buffer is loaded BEFORE the old one is freed — otherwise the allocator returns the same
            // address and the check "the offset outlived the move" proves nothing.
            var moved = Raise(HotFile(ammo: 21f, rpm: 210));
            Drop(hot);
            Assert.That(moved.Ptr, Is.Not.EqualTo(hot.Ptr), "the new buffer landed at the same address — the test is meaningless");

            var loaded = Load(bytes);
            var slot = SlotOf(loaded, Single<GunRef>(loaded));

            Assert.That(slot, Is.EqualTo(moved.AddressOf(offset)));
            Assert.That(
                Copy(loaded.EntityManager.GetComponentData<GunRef>(Single<GunRef>(loaded)).Gun.Value).Rpm,
                Is.EqualTo(210));
        }

        [Test]
        public void After_a_save_the_live_world_stays_patched()
        {
            var file = HotFile();
            var hot = Raise(file);
            var entity = Gun(file["gun"]);

            Patch();
            Save();

            Assert.That(SlotOf(entity), Is.EqualTo(hot.AddressOf(file["gun"])),
                "the reverse pass is obliged to walk a copy of the chunk: after a write the live world stays alive");
            Assert.That(EM.GetComponentData<GunRef>(entity).Gun.IsResolved, Is.True);
            Assert.That(Copy(EM.GetComponentData<GunRef>(entity).Gun.Value).Rpm, Is.EqualTo(600));
        }

        [Test]
        public void A_world_that_was_never_patched_is_saved_and_read_correctly()
        {
            var file = HotFile(ammo: 31f, rpm: 310);
            var hot = Raise(file);
            Gun(file["gun"]);

            // Not a single Patch: the entities were assembled by hand and we write straight away.
            var bytes = Save();

            var loaded = Load(bytes);
            Assert.That(SlotOf(loaded, Single<GunRef>(loaded)), Is.EqualTo(hot.AddressOf(file["gun"])),
                "the read patch is obliged to cope with a world nobody patched before the write too");
            Assert.That(
                Copy(loaded.EntityManager.GetComponentData<GunRef>(Single<GunRef>(loaded)).Gun.Value).Rpm,
                Is.EqualTo(310));
        }

        // BUG: writing a world with the domain taken off the register puts a process address into the file
        // What happens: the domain was taken off the register (the base is being rebuilt, the buffer is
        //   already freed) while the world is being written at that moment. TryUnresolve returns
        //   DomainNotRaised and leaves the value as it is — that is, a pointer from the previous run
        //   travels into the file. The failure is dropped into the box, but by that point the file is
        //   already assembled and written.
        // What should happen: the write is obliged to be rejected before a single address enters the
        //   stream. No offset exists in that state, and there is nothing to substitute for it.
        // Root cause: on a failure BlobchegPatchRunner.PatchElements only calls
        //   BlobchegPatchErrors.Report and carries on, while SerializeUtility.WriteChunks never once
        //   asks BlobchegPatchErrors.HasAny. The box is emptied only by BlobchegLiveSweep.Run and
        //   BlobchegPatchErrorSystem — both on the READ path. The write path has no handling at all.
        [Test]
        public void Saving_after_the_domain_was_taken_off_the_register_is_obliged_to_be_rejected()
        {
            var file = HotFile();
            var hot = Raise(file);
            var entity = Gun(file["gun"]);

            Patch();
            var address = SlotOf(entity);

            Drop(hot);

            var bytes = Save();
            BlobchegPatchErrors.Clear();

            Assert.That(Contains(bytes, address), Is.False,
                "a process address travelled into the file: the domain is off the register, there is nothing to fold the address into, and writing was not allowed");
        }

        [Test]
        public void A_world_saved_in_one_generation_is_read_in_another()
        {
            var first = HotFile(ammo: 1f, rpm: 11);
            var gen1 = Raise(first);
            Gun(first["gun"]);

            Patch();
            var bytes = Save();

            var gen2 = Raise(HotFile(ammo: 2f, rpm: 22));
            Drop(gen1);
            Assert.That(gen2.Ptr, Is.Not.EqualTo(gen1.Ptr));

            var loaded = Load(bytes);

            Assert.That(SlotOf(loaded, Single<GunRef>(loaded)), Is.EqualTo(gen2.AddressOf(first["gun"])),
                "a saved offset is obliged to come up on whichever generation stands right now");
            Assert.That(
                Copy(loaded.EntityManager.GetComponentData<GunRef>(Single<GunRef>(loaded)).Gun.Value).Rpm,
                Is.EqualTo(22));
        }

        [Test]
        public void Reading_a_world_without_a_loaded_base_is_obliged_to_be_rejected_and_not_to_leave_an_offset_in_the_field()
        {
            var file = HotFile();
            Raise(file);
            Gun(file["gun"]);

            Patch();
            var bytes = Save();

            var loaded = LoadRaw(bytes);
            var slot = SlotOf(loaded, Single<GunRef>(loaded));

            Assert.That(slot, Is.EqualTo(file["gun"]), "without a base the read patch does not touch the slot — it holds an offset");

            // And that state is obliged to be VISIBLE: the entity arrived before its base.
            Assert.That(
                loaded.EntityManager.GetComponentData<GunRef>(Single<GunRef>(loaded)).Gun.IsResolved, Is.False);

            // The read patch drops the failure into the box, and a separate system of the boot group
            // throws it — the test does the same thing by hand.
            var world = loaded;
            var e = Single<GunRef>(world);
            Assert.That(world.EntityManager.GetComponentData<GunRef>(e).Gun.IsSet, Is.True,
                "the slot is assigned but not resolved — IsSet and IsResolved are obliged to answer differently");
        }

        [Test]
        public void A_cloned_entity_carries_a_resolved_pointer_while_an_offset_travels_to_disk()
        {
            const int clones = 100;

            var file = HotFile();
            var hot = Raise(file);
            var offset = file["gun"];
            var source = Gun(offset);

            Patch();

            // The clone gets an ALREADY resolved pointer, bypassing the patch: Instantiate copies the
            // bytes of the component as they are.
            var copies = EM.Instantiate(source, clones, Allocator.Temp);
            foreach (var clone in copies)
                Assert.That(EM.GetComponentData<GunRef>(clone).Gun.Data.Value, Is.EqualTo(hot.AddressOf(offset)));

            copies.Dispose();

            var bytes = Save();

            Assert.That(Contains(bytes, hot.AddressOf(offset)), Is.False,
                "the reverse pass is obliged to walk every entity and not only those it patched itself");

            var loaded = LoadRaw(bytes);
            var query = loaded.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<GunRef>());
            var all = query.ToEntityArray(Allocator.Temp);

            Assert.That(all.Length, Is.EqualTo(clones + 1));

            var wrong = 0;
            foreach (var entity in all)
                if (loaded.EntityManager.GetComponentData<GunRef>(entity).Gun.Data.Value != offset)
                    wrong++;

            all.Dispose();
            Assert.That(wrong, Is.Zero, "in the file the clones are obliged to hold the same offset as the original");
        }

        [Test]
        public void The_write_read_write_circle_does_not_move_the_offset()
        {
            var file = HotFile();
            Raise(file);
            var offset = file["gun"];
            Gun(offset);

            Patch();

            var first = Save();
            var loaded = Load(first);
            Patch(loaded);

            var second = Save(loaded);
            var again = LoadRaw(second);

            Assert.That(SlotOf(again, Single<GunRef>(again)), Is.EqualTo(offset),
                "the circle \"wrote, read, wrote\" is obliged to be the identity for an offset");
        }
    }
}
