using System;
using NUnit.Framework;
using Unity.Entities;

namespace Blobcheg.PatchTests
{
    /// <summary>
    /// The empty and the zero. The main question of the section is whether the patch confuses "not
    /// assigned" with "a record at address zero": as an offset zero would mean the start of the header,
    /// as an address a null pointer.
    /// </summary>
    public sealed unsafe class EmptyAndZeroTests : PatchFixture
    {
        [Test]
        public void Zero_in_a_slot_is_not_a_record_at_address_zero()
        {
            var hot = Raise(HotFile());

            var entity = EM.CreateEntity();
            EM.AddComponentData(entity, new GunRef());

            Patch();

            var slot = EM.GetComponentData<GunRef>(entity).Gun;

            Assert.That(slot.Data.Value, Is.Zero,
                "zero is obliged to stay zero: the address \"base plus zero\" is the start of the header, not a record");
            Assert.That(slot.Data.Value, Is.Not.EqualTo(hot.Ptr));
            Assert.That(slot.IsSet, Is.False);
            Assert.That(slot.IsResolved, Is.False);

            Assert.Throws<InvalidOperationException>(() => Copy(slot.Value),
                "reading an unassigned reference is an error, not a zeroed struct");
        }

        [Test]
        public void Patching_a_world_without_a_single_reference_neither_throws_nor_touches_anything()
        {
            Raise(HotFile());

            var entity = EM.CreateEntity();
            EM.AddComponentData(entity, new PlainData { Value = 4242 });

            Assert.DoesNotThrow(() => Patch(), "a world without slots is of no interest to the patch");
            Assert.That(EM.GetComponentData<PlainData>(entity).Value, Is.EqualTo(4242),
                "the patch has no right to touch a component without slots");
        }

        [Test]
        public void A_buffer_of_zero_length_is_patched_without_a_single_touch()
        {
            Raise(HotFile());

            var entity = EM.CreateEntity();
            EM.AddBuffer<RefElement>(entity);

            Assert.DoesNotThrow(() => Patch(), "a buffer of zero elements is a normal state, not a failure");
            Assert.That(EM.GetBuffer<RefElement>(entity).Length, Is.Zero);

            // And the reverse pass too: an empty buffer has nothing to fold, but the pass is obliged to walk it.
            byte[] saved = null;
            Assert.DoesNotThrow(() => saved = Save());
            Assert.That(saved, Is.Not.Null);
            Assert.That(BlobchegPatchErrors.HasAny, Is.False, "an empty buffer has no right to drop a failure into the box");
        }

        [Test]
        public void A_reference_into_a_loaded_but_empty_base_is_obliged_to_be_rejected()
        {
            // A base without a single record is a file of exactly one header. The first possible record
            // offset (HeaderSize) in such a base is already past the end of the file.
            var empty = Raise(Domain(nameof(IPatchHot)).Seal());
            Assert.That(empty.Length, Is.EqualTo(BlobchegFormat.HeaderSize));

            Gun(BlobchegFormat.HeaderSize);

            var error = Assert.Throws<InvalidOperationException>(() => Patch(),
                "an offset past the end of an empty base is obliged to be an error, not a pointer at the first byte after the header");

            Assert.That(error.Message, Does.Contain(nameof(GunRef)));
        }

        [Test]
        public void A_zero_slot_outlives_the_patch_and_the_reverse_pass_as_zero()
        {
            Raise(HotFile());

            var entity = EM.CreateEntity();
            EM.AddComponentData(entity, new GunRef());

            Patch();
            var bytes = Save();

            using (var loaded = LoadRaw(bytes))
            {
                var slot = SlotOf(loaded, Single<GunRef>(loaded));

                // Blindly subtracting the base address from zero would give ulong.MaxValue minus the
                // address — an absurd number the next patch could no longer tell from anything.
                Assert.That(slot, Is.Zero, "zero is obliged to travel into the file as zero");
            }
        }

        [Test]
        public void A_buffer_of_one_zero_element_does_not_become_the_base_address()
        {
            var hot = Raise(HotFile());

            var entity = EM.CreateEntity();
            var buffer = EM.AddBuffer<RefElement>(entity);
            buffer.Add(new RefElement { Marker = 1 });

            Patch();

            var element = EM.GetBuffer<RefElement>(entity)[0];
            Assert.That(element.Gun.Data.Value, Is.Zero);
            Assert.That(element.Gun.Data.Value, Is.Not.EqualTo(hot.Ptr));
            Assert.That(element.Marker, Is.EqualTo(1), "the patch has no right to touch the neighbouring field of the element");
        }

        [Test]
        public void A_world_without_a_single_entity_is_saved_and_read()
        {
            Raise(HotFile());

            var bytes = Save();
            using (var loaded = Load(bytes))
            {
                var query = loaded.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<GunRef>());
                Assert.That(query.CalculateEntityCount(), Is.Zero);
            }

            Assert.That(BlobchegPatchErrors.HasAny, Is.False);
        }
    }
}
