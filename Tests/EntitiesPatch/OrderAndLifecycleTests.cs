using System;
using NUnit.Framework;
using Unity.Collections;
using Unity.Entities;

namespace Blobcheg.PatchTests
{
    /// <summary>
    /// The order of calls and the life cycle of a base. This is where the promises "the patch is
    /// idempotent" and "the domain is not loaded means an explicit error" get broken, and the reverse
    /// direction is checked along the way: a reverse pass over a world that was never patched is obliged
    /// to be a no-op and not a subtraction of an address from an offset.
    /// </summary>
    public sealed unsafe class OrderAndLifecycleTests : PatchFixture
    {
        // BUG: the message about an unloaded domain names the key and not the domain
        // What happens: the error text contains "domain 8A1C…F3 is not loaded" — sixteen hexadecimal
        //   digits instead of the name of the marker interface. A human has nothing to do with that
        //   number: it occurs nowhere in the code.
        // What should happen: the message is obliged to carry the domain name — "IPatchGhost".
        // Root cause: BlobchegPatchErrors.Slot stores only a ulong DomainKey, and there is no reverse
        //   "key → name" map either in the box or in BlobchegPatchTable. Meanwhile
        //   BlobchegPatchTableBuilder.CollectDomains builds exactly such a map while assembling the
        //   table and throws it away right afterwards — the names exist, they were simply not kept.
        [Test]
        public void A_patch_without_a_loaded_base_names_the_domain_in_the_message()
        {
            Raise(HotFile());

            var entity = EM.CreateEntity();
            EM.AddComponentData(entity, new GhostRef
            {
                Ghost = new BlobchegReference<PatchGhostRecord>(BlobchegFormat.HeaderSize),
            });

            // The live pass runs where authoring happens, and the order in which bases load does not obey
            // it: the editor world loads subscenes whenever Unity decides, while bases are loaded by
            // reading a file. "The domain is not loaded yet" is a state for it and not trouble — but the
            // slot is obliged to stay exactly the offset it arrived as, so that the pass after the base
            // loads brings it to an address.
            Assert.DoesNotThrow(() => Patch(),
                "the live path waits for the base instead of failing the scene while it loads");
            Assert.That(EM.GetComponentData<GhostRef>(entity).Ghost.Data.Value,
                Is.EqualTo((ulong)BlobchegFormat.HeaderSize),
                "a forgiven failure is obliged to leave the slot untouched");

            // And the strict question — the one the player asks, where the order is ours — is still
            // trouble and is still obliged to name the culprits.
            Load(Save());

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

            // There was no patch at all: the entity was created by hand and we write the world straight
            // away. Blindly subtracting the base address here would give offset minus address — that is,
            // a number close to ulong.MaxValue.
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

            // A world from a file, the slots hold raw offsets. We load the base again and write it once
            // more — that is the second reverse pass over the same data.
            var once = LoadRaw(first);
            Assert.That(SlotOf(once, Single<GunRef>(once)), Is.EqualTo(offset));

            Raise(HotFile());
            var second = Save(once);

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

            Drop(hot);

            // The memory is freed — dereferencing is not allowed, so we ask the registry and not the memory.
            Assert.That(BlobchegBases.IsKnownAddress(address), Is.False,
                "a range taken off the register is obliged to stop counting as a live record");
            Assert.That(EM.GetComponentData<GunRef>(entity).Gun.IsResolved, Is.False,
                "IsResolved is obliged to say \"no\" honestly — otherwise the next Value reads freed memory");
        }

        /// <summary>
        /// An accepted limit, not a victory. A base is a value struct with an owning pointer, and it has
        /// no cell that outlives the freeing of the memory itself. That is why the registry cannot tell
        /// "the buffer was freed and taking it off the register was forgotten": it stores an address and
        /// a length, not an allocation generation. The test exists so that the limit looks like a
        /// decision and not an oversight.
        /// </summary>
        [Test]
        public void The_registry_cannot_tell_a_freed_but_unregistered_buffer_an_accepted_limit()
        {
            var buffer = BlobchegBuffer.Alloc(64, Allocator.Persistent);
            var key = BlobchegNaming.NameHash("IPatchFreed");
            var address = (ulong)buffer.Ptr + BlobchegFormat.HeaderSize;

            BlobchegBases.Register(key, buffer.Ptr, buffer.Length);
            Assert.That(BlobchegBases.IsKnownAddress(address), Is.True);

            // Exactly the mistake that gets made: the buffer was freed directly and Unregister was never called.
            buffer.Dispose();

            Assert.That(BlobchegBases.IsKnownAddress(address), Is.True,
                "the registry still answers \"yes\" — and cannot answer otherwise: an address has no generation. " +
                "The contract is plain: whoever put it on the register takes it off, and in exactly the place where they free it");

            BlobchegBases.Unregister(key, buffer.Ptr);
        }

        // BUG: a rebuild in the order "free the old one first, then load the new one" loses every handed-out pointer
        // What happens: if the old base is taken off the register BEFORE the new one stands up, the slot
        //   of the domain disappears from the registry entirely; the next registration creates the slot
        //   anew with PrevPtrs = 0. Every already handed-out pointer becomes OutOfRange, and the patch
        //   fails instead of translating.
        // What should happen: the promise of the feature — a rebuild translates the already handed-out
        //   pointers onto the new buffer, with no caveats about the order.
        // Root cause: the previous generation lives in BlobchegBases.Table.PrevPtrs and is filled ONLY in
        //   the Register branch, where the slot already exists. By that moment Unregister has already
        //   removed the slot by swapping it with the last one (t.Keys[slot] = t.Keys[last]), and the
        //   address of the old buffer is forgotten forever. The order "load the new one, then free the
        //   old one" is checked nowhere — it is only described in the comment on Unregister.
        [Test]
        public void A_rebuild_in_the_order_unregister_then_load_is_obliged_to_translate_the_pointers()
        {
            var first = HotFile(ammo: 1f, rpm: 11);
            var gen1 = Raise(first);
            var entity = Gun(first["gun"]);

            Patch();
            Assert.That(SlotOf(entity), Is.EqualTo(gen1.AddressOf(first["gun"])));

            // A rebuild of the domain: the old one was freed, the new one was loaded.
            Drop(gen1);
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

            // A typical typo: the domain was unregistered while passing the pointer of a neighbouring base.
            BlobchegBases.Unregister(hot.Key, (byte*)cold.Ptr);

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
