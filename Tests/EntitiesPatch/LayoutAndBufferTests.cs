using System;
using NUnit.Framework;
using Unity.Collections;
using Unity.Entities;

namespace Blobcheg.PatchTests
{
    /// <summary>
    /// Where the patch looks for a slot and whether it finds it. The field walk is the quietest part of
    /// the feature: a slot that was not found neither throws nor logs, it simply stays an offset forever,
    /// and that is learned on the first <c>Value</c> in a job in a build.
    /// </summary>
    public sealed unsafe class LayoutAndBufferTests : PatchFixture
    {
        [Test]
        public void A_slot_as_the_second_field_after_an_unaligned_byte()
        {
            var file = HotFile(ammo: 5f, rpm: 55);
            var hot = Raise(file);
            var offset = file["gun"];

            var entity = EM.CreateEntity();
            EM.AddComponentData(entity, new PackedRef
            {
                Head = 0xAB,
                Gun = new BlobchegReference<PatchGun>(offset),
                Tail = 0xCD,
            });

            Patch();

            var packed = EM.GetComponentData<PackedRef>(entity);

            Assert.That(packed.Gun.Data.Value, Is.EqualTo(hot.AddressOf(offset)),
                "a slot at byte offset 1 is obliged to be found: the walk computes field offsets and does not guess from the alignment");
            Assert.That(packed.Head, Is.EqualTo((byte)0xAB), "the patch drove to the left of the slot");
            Assert.That(packed.Tail, Is.EqualTo((byte)0xCD), "the patch drove to the right of the slot");
            Assert.That(Copy(packed.Gun.Value).Rpm, Is.EqualTo(55));
        }

        [Test]
        public void An_unaligned_slot_outlives_the_reverse_pass_too()
        {
            var file = HotFile();
            Raise(file);
            var offset = file["gun"];

            var entity = EM.CreateEntity();
            EM.AddComponentData(entity, new PackedRef
            {
                Head = 1,
                Gun = new BlobchegReference<PatchGun>(offset),
                Tail = 2,
            });

            Patch();
            var bytes = Save();

            var loaded = LoadRaw(bytes);
            var packed = loaded.EntityManager.GetComponentData<PackedRef>(Single<PackedRef>(loaded));

            Assert.That(packed.Gun.Data.Value, Is.EqualTo(offset));
            Assert.That(packed.Head, Is.EqualTo((byte)1));
            Assert.That(packed.Tail, Is.EqualTo((byte)2));
        }

        [Test]
        public void A_slot_at_the_second_level_of_nesting()
        {
            var file = HotFile(ammo: 6f, rpm: 66);
            var hot = Raise(file);
            var offset = file["gun"];

            var entity = EM.CreateEntity();
            EM.AddComponentData(entity, new ShallowNestRef
            {
                Head = 7,
                Inner = new NestOne { A = 8, Gun = new BlobchegReference<PatchGun>(offset) },
            });

            Patch();

            var nested = EM.GetComponentData<ShallowNestRef>(entity);
            Assert.That(nested.Inner.Gun.Data.Value, Is.EqualTo(hot.AddressOf(offset)));
            Assert.That(nested.Head, Is.EqualTo(7));
            Assert.That(nested.Inner.A, Is.EqualTo(8));
        }

        [Test]
        public void A_slot_at_the_third_level_of_nesting()
        {
            var file = HotFile(ammo: 9f, rpm: 99);
            var hot = Raise(file);
            var offset = file["gun"];

            var entity = EM.CreateEntity();
            EM.AddComponentData(entity, new DeepNestRef
            {
                Head = -1,
                Inner = new NestTwo
                {
                    S = 3,
                    Inner = new NestOne { A = 4, Gun = new BlobchegReference<PatchGun>(offset) },
                },
            });

            Patch();

            var deep = EM.GetComponentData<DeepNestRef>(entity);
            Assert.That(deep.Inner.Inner.Gun.Data.Value, Is.EqualTo(hot.AddressOf(offset)),
                "the walk is obliged to be recursive and not \"the fields of the first level\"");
            Assert.That(deep.Head, Is.EqualTo(-1));
            Assert.That(deep.Inner.S, Is.EqualTo((short)3));
            Assert.That(Copy(deep.Inner.Inner.Gun.Value).Rpm, Is.EqualTo(99));
        }

        [Test]
        public void Two_slots_of_different_record_types_in_one_component_cannot_be_mixed_up()
        {
            var file = HotFile(ammo: 10f, rpm: 101, hp: 202f, plates: 4);
            var hot = Raise(file);

            var entity = EM.CreateEntity();
            EM.AddComponentData(entity, new PairRef
            {
                Gun = new BlobchegReference<PatchGun>(file["gun"]),
                Armor = new BlobchegReference<PatchArmor>(file["armor"]),
            });

            Patch();

            var pair = EM.GetComponentData<PairRef>(entity);

            Assert.That(pair.Gun.Data.Value, Is.EqualTo(hot.AddressOf(file["gun"])));
            Assert.That(pair.Armor.Data.Value, Is.EqualTo(hot.AddressOf(file["armor"])));
            Assert.That(pair.Gun.Data.Value, Is.Not.EqualTo(pair.Armor.Data.Value),
                "two slots of one component got the same address — the walk confused their offsets");

            Assert.That(Copy(pair.Gun.Value).Rpm, Is.EqualTo(101));
            Assert.That(Copy(pair.Armor.Value).Plates, Is.EqualTo(4));
        }

        [Test]
        public void A_buffer_of_three_elements_is_patched_element_by_element()
        {
            var file = Domain(nameof(IPatchHot))
                .Add("g0", new PatchGun { Ammo = 1f, Rpm = 1 })
                .Add("g1", new PatchGun { Ammo = 2f, Rpm = 2 })
                .Add("g2", new PatchGun { Ammo = 3f, Rpm = 3 })
                .Seal();

            var hot = Raise(file);

            var entity = EM.CreateEntity();
            var buffer = EM.AddBuffer<RefElement>(entity);
            for (var i = 0; i < 3; i++)
                buffer.Add(new RefElement { Gun = new BlobchegReference<PatchGun>(file["g" + i]), Marker = i });

            Patch();

            var patched = EM.GetBuffer<RefElement>(entity);
            for (var i = 0; i < 3; i++)
            {
                var element = patched[i];

                Assert.That(element.Gun.Data.Value, Is.EqualTo(hot.AddressOf(file["g" + i])),
                    $"element {i} is obliged to reach ITS OWN record and not the first one in the buffer");
                Assert.That(Copy(element.Gun.Value).Rpm, Is.EqualTo(i + 1));
                Assert.That(element.Marker, Is.EqualTo(i));
            }
        }

        // BUG: one broken reference in a scene makes the world unsaveable
        // What happens: the patch rejected the broken element and left it as it was — exactly as promised.
        //   But while writing the world the REVERSE pass meets that same number again, rejects it with the
        //   same OutOfRange, and the end of serialisation raises the accumulated failure into an
        //   exception. Save() returns no bytes at all: a scene with one broken reference cannot be saved
        //   in order to be repaired later.
        // What should happen (the plan, line 31): "an explicit error AND a consistent state: after it the
        //   reverse pass returns their original offsets to the untouched elements. Not a single element
        //   was left as a raw pointer that travels to disk as an address." That is, the error belongs to
        //   the patch, while writing the world is obliged to go through and return the same three numbers
        //   into the file that were in it.
        // Root cause: an asymmetry of strictness between the two directions of one pass.
        //   BlobchegBases.TryUnresolve answers OutOfRange for any value that lies neither in the current
        //   generation nor in the retired ones while being no less than the buffer length — without
        //   telling "a stale address that is about to leak to disk" from "a bad offset the patch already
        //   touched and that was never an address". The second case is safe by construction: the value
        //   falls into no range the registry knows, so it cannot be a pointer, and leaving it as it is
        //   makes an exact round trip. For the sake of the first case (the plan, line 37) this strictness
        //   is not needed: a buffer taken off the register moves into the retired generations, and its
        //   address folds into an offset as it should — the test
        //   Saving_after_the_domain_was_taken_off_the_register_is_obliged_to_be_rejected passes exactly
        //   that way. Above, however, SerializeUtility.SerializeWorldInternal makes a failure of the
        //   reverse pass fatal for the whole write, while the forward pass treats the same failure as the
        //   trouble of one slot and sweeps on.
        [Test]
        public void A_broken_element_in_the_middle_of_a_buffer_does_not_leave_the_neighbours_half_patched()
        {
            var file = HotFile();
            var hot = Raise(file);
            var good = file["gun"];
            var bad = (uint)hot.Length + BlobchegFormat.RecordAlign;

            var entity = EM.CreateEntity();
            var buffer = EM.AddBuffer<RefElement>(entity);
            buffer.Add(new RefElement { Gun = new BlobchegReference<PatchGun>(good), Marker = 0 });
            buffer.Add(new RefElement { Gun = new BlobchegReference<PatchGun>(bad), Marker = 1 });
            buffer.Add(new RefElement { Gun = new BlobchegReference<PatchGun>(good), Marker = 2 });

            Assert.Throws<InvalidOperationException>(() => Patch(), "a broken element is obliged to be an error");

            var patched = EM.GetBuffer<RefElement>(entity);

            Assert.That(patched[0].Gun.Data.Value, Is.EqualTo(hot.AddressOf(good)));
            Assert.That(patched[2].Gun.Data.Value, Is.EqualTo(hot.AddressOf(good)),
                "the element AFTER the broken one is obliged to be processed: the failure of one has no right to swallow the rest");
            Assert.That(patched[1].Gun.Data.Value, Is.EqualTo(bad),
                "the broken element is obliged to stay the number that was in it and not to turn into a wild address");

            // And the state is obliged to be consistent: the reverse pass over this buffer hands out not a
            // single process address.
            var bytes = Save();
            BlobchegPatchErrors.Clear();

            Assert.That(Contains(bytes, hot.AddressOf(good)), Is.False,
                "a process address travelled into the file — a half-patched buffer leaked to disk");

            var loaded = LoadRaw(bytes);
            var stored = loaded.EntityManager.GetBuffer<RefElement>(SingleBuffer<RefElement>(loaded));

            Assert.That(stored[0].Gun.Data.Value, Is.EqualTo(good));
            Assert.That(stored[1].Gun.Data.Value, Is.EqualTo(bad));
            Assert.That(stored[2].Gun.Data.Value, Is.EqualTo(good));
        }

        [Test]
        public void A_buffer_of_a_hundred_thousand_elements_is_patched_whole()
        {
            const int count = 100_000;

            var file = HotFile();
            var hot = Raise(file);
            var offset = file["gun"];

            var entity = EM.CreateEntity();
            var buffer = EM.AddBuffer<RefElement>(entity);
            buffer.EnsureCapacity(count);
            for (var i = 0; i < count; i++)
                buffer.Add(new RefElement { Gun = new BlobchegReference<PatchGun>(offset), Marker = i });

            Patch();

            var patched = EM.GetBuffer<RefElement>(entity);
            Assert.That(patched.Length, Is.EqualTo(count));

            foreach (var index in new[] { 0, 1, count / 2, count - 2, count - 1 })
            {
                Assert.That(patched[index].Gun.Data.Value, Is.EqualTo(hot.AddressOf(offset)),
                    $"element {index} was left unpatched");
                Assert.That(patched[index].Marker, Is.EqualTo(index));
            }
        }

        [Test]
        public void Ten_thousand_entities_with_a_slot_are_patched_in_one_pass()
        {
            const int count = 10_000;

            var file = HotFile(ammo: 12f, rpm: 121);
            var hot = Raise(file);
            var offset = file["gun"];

            var archetype = EM.CreateArchetype(ComponentType.ReadWrite<GunRef>());
            var entities = EM.CreateEntity(archetype, count, Allocator.Temp);
            foreach (var entity in entities)
                EM.SetComponentData(entity, new GunRef { Gun = new BlobchegReference<PatchGun>(offset) });

            Patch();

            var address = hot.AddressOf(offset);
            var wrong = 0;
            foreach (var entity in entities)
                if (EM.GetComponentData<GunRef>(entity).Gun.Data.Value != address)
                    wrong++;

            entities.Dispose();

            Assert.That(wrong, Is.Zero, "the patch is obliged to cover every chunk of the archetype and not the first one");
        }

        [Test]
        public void A_slot_in_a_component_of_a_disabled_entity_is_patched_too()
        {
            var file = HotFile();
            var hot = Raise(file);
            var offset = file["gun"];

            var entity = Gun(offset);
            EM.SetEnabled(entity, false);

            Patch();

            // A disabled entity reaches its enabling already in the game, and by that moment the offset in
            // it must be an address: there will be no second patch.
            Assert.That(EM.GetComponentData<GunRef>(entity).Gun.Data.Value, Is.EqualTo(hot.AddressOf(offset)));
        }
    }
}
