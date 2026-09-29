using System;
using NUnit.Framework;
using Unity.Collections;
using Unity.Entities;

namespace Blobcheg.PatchTests
{
    public sealed unsafe class OrderAndLifecycleTests : PatchFixture // call order and base life cycle
    {
        [Test]
        public void A_patch_without_a_loaded_base_names_the_domain_in_the_message()
        {
            Raise(HotFile());

            var entity = EM.CreateEntity();
            EM.AddComponentData(entity, new GhostRef
            {
                Ghost = new BlobchegReference<PatchGhostRecord>(BlobchegFormat.HeaderSize),
            });

            // Editor bases load at any time: an unloaded domain leaves the slot an offset for a later pass.
            Assert.DoesNotThrow(() => Patch(),
                "the live path waits for the base instead of failing the scene while it loads");
            Assert.That(EM.GetComponentData<GhostRef>(entity).Ghost.Data.Value,
                Is.EqualTo((ulong)BlobchegFormat.HeaderSize),
                "a forgiven failure is obliged to leave the slot untouched");

            Load(Save()); // the player's strict path: a missing base is still an error

            var error = Assert.Throws<InvalidOperationException>(() => BlobchegPatchErrors.ThrowIfAny(),
                "in the player an entity that arrived before the base stays an error");

            Assert.That(error.Message, Does.Contain(nameof(GhostRef)),
                "the component is in the message — by it the scene can at least be found");
            Assert.That(error.Message, Does.Contain(nameof(IPatchGhost)),
                "and the domain is obliged to be named by name: an FNV-64 key is not searchable and occurs nowhere in the project");
        }

        [Test]
        public void A_double_patch_does_not_add_the_address_twice()
        {
            var file = HotFile();
            var hot = Raise(file);
            var entity = Gun(file["gun"]);

            Patch();
            var once = SlotOf(entity);

            Patch();
            var twice = SlotOf(entity);

            Assert.That(once, Is.EqualTo(hot.AddressOf(file["gun"])));
            Assert.That(twice, Is.EqualTo(once),
                "a second pass over an already patched field is obliged to be a no-op and not \"base plus base plus offset\"");

            var gun = Copy(EM.GetComponentData<GunRef>(entity).Gun.Value);
            Assert.That(gun.Rpm, Is.EqualTo(600));
        }

        [Test]
        public void A_triple_patch_and_the_reverse_pass_return_the_original_offset()
        {
            var file = HotFile();
            Raise(file);
            var offset = file["gun"];
            Gun(offset);

            Patch();
            Patch();
            Patch();

            var bytes = Save();
            using (var loaded = LoadRaw(bytes))
            {
                Assert.That(SlotOf(loaded, Single<GunRef>(loaded)), Is.EqualTo(offset),
                    "however many times it was patched, that very offset is obliged to travel into the file");
            }
        }

        [Test]
        public void The_reverse_pass_over_an_unpatched_world_does_not_send_the_offset_negative()
        {
            var file = HotFile();
            Raise(file);
            var offset = file["gun"];
            Gun(offset);

            // No patch at all: blindly subtracting the base would give offset minus address, near ulong.MaxValue.
            var bytes = Save();

            using (var loaded = LoadRaw(bytes))
            {
                Assert.That(SlotOf(loaded, Single<GunRef>(loaded)), Is.EqualTo(offset));
            }
        }

        [Test]
        public void A_double_reverse_pass_does_not_subtract_the_base_twice()
        {
            var file = HotFile();
            Raise(file);
            var offset = file["gun"];
            Gun(offset);

            Patch();
            var first = Save();

            var once = LoadRaw(first);
            Assert.That(SlotOf(once, Single<GunRef>(once)), Is.EqualTo(offset));

            Raise(HotFile());
            var second = Save(once); // second reverse pass over the same raw offsets

            var twice = LoadRaw(second);
            Assert.That(SlotOf(twice, Single<GunRef>(twice)), Is.EqualTo(offset),
                "folding the same offset a second time is obliged to give the same number");
        }

        [Test]
        public void Taking_a_base_off_the_register_while_pointers_are_live_is_obliged_to_be_visible()
        {
            var file = HotFile();
            var hot = Raise(file);
            var entity = Gun(file["gun"]);

            Patch();
            var address = SlotOf(entity);
            Assert.That(BlobchegBases.IsKnownAddress(address), Is.True);

            Drop(hot); // memory freed: ask the registry, never dereference

            Assert.That(BlobchegBases.IsKnownAddress(address), Is.False,
                "a range taken off the register is obliged to stop counting as a live record");
            Assert.That(EM.GetComponentData<GunRef>(entity).Gun.IsResolved, Is.False,
                "IsResolved is obliged to say \"no\" honestly — otherwise the next Value reads freed memory");
        }

        // Accepted limit: the registry keeps address and length, no generation, so it cannot see a free.
        [Test]
        public void The_registry_cannot_tell_a_freed_but_unregistered_buffer_an_accepted_limit()
        {
            var buffer = BlobchegBuffer.Alloc(64, Allocator.Persistent);
            var key = BlobchegNaming.NameHash("IPatchFreed");
            var address = (ulong)buffer.Ptr + BlobchegFormat.HeaderSize;

            BlobchegBases.Register(key, buffer.Ptr, buffer.Length);
            Assert.That(BlobchegBases.IsKnownAddress(address), Is.True);

            buffer.Dispose(); // the mistake: freed directly, Unregister never called

            Assert.That(BlobchegBases.IsKnownAddress(address), Is.True,
                "the registry still answers \"yes\" — and cannot answer otherwise: an address has no generation. " +
                "The contract is plain: whoever put it on the register takes it off, and in exactly the place where they free it");

            BlobchegBases.Unregister(key, buffer.Ptr);
        }

        [Test]
        public void A_rebuild_in_the_order_unregister_then_load_is_obliged_to_translate_the_pointers()
        {
            var first = HotFile(ammo: 1f, rpm: 11);
            var gen1 = Raise(first);
            var entity = Gun(first["gun"]);

            Patch();
            Assert.That(SlotOf(entity), Is.EqualTo(gen1.AddressOf(first["gun"])));

            Drop(gen1); // unregister before load: the previous generation must survive
            Raise(HotFile(ammo: 2f, rpm: 22));

            Assert.DoesNotThrow(() => Patch(),
                "a rebuild is obliged to translate the handed-out pointers regardless of the order of unregistering and loading");

            var gun = Copy(EM.GetComponentData<GunRef>(entity).Gun.Value);
            Assert.That(gun.Rpm, Is.EqualTo(22), "after the rebuild the new generation is what is read");
        }

        [Test]
        public void Unregistering_with_a_foreign_pointer_does_not_wipe_out_a_live_base()
        {
            var hot = Raise(HotFile());
            var cold = Raise(Domain(nameof(IPatchCold)).Add("note", new PatchNote { Tier = 1 }).Seal());

            BlobchegBases.Unregister(hot.Key, (byte*)cold.Ptr); // typo: a neighbouring base's pointer

            Assert.That(BlobchegBases.TryGet(hot.Key, out var ptr, out _), Is.True,
                "unregistering with a foreign pointer has no right to wipe out a live base");
            Assert.That((ulong)ptr, Is.EqualTo(hot.Ptr));
        }

        [Test]
        public void Unregistering_a_domain_that_does_not_exist_neither_throws_nor_breaks_anything()
        {
            var hot = Raise(HotFile());

            Assert.DoesNotThrow(
                () => BlobchegBases.Unregister(BlobchegNaming.NameHash("IPatchNeverWas"), (byte*)hot.Ptr));

            Assert.That(BlobchegBases.TryGet(hot.Key, out _, out _), Is.True);
        }
    }
}
