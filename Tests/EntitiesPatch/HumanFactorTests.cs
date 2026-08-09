using System;
using NUnit.Framework;
using UnityEngine;

namespace Blobcheg.PatchTests
{
    /// <summary>
    /// The mistakes made not by attackers but by developers on their first working day with this API.
    /// Every one of them feels right — because in a neighbouring API that is exactly how it works.
    /// </summary>
    public sealed unsafe class HumanFactorTests : PatchFixture
    {
        /// <summary>The famous "let's cache it so we do not look it up every frame".</summary>
        static BlobchegReference<PatchGun> s_Cached;

        [SetUp]
        public void ForgetCache() => s_Cached = default;

        [Test]
        public void A_reference_copied_into_an_ordinary_field_does_not_move_with_a_rebuild()
        {
            var first = HotFile(ammo: 1f, rpm: 11);
            Raise(first);
            var entity = Gun(first["gun"]);

            Patch();

            // "I already have the slot, I will put it into a static — why climb into the component every time."
            s_Cached = EM.GetComponentData<GunRef>(entity).Gun;
            Assert.That(s_Cached.IsResolved, Is.True);
            Assert.That(Copy(s_Cached.Value).Rpm, Is.EqualTo(11));

            // A rebuild of the domain under a live editor. The promise of the feature covers slots in
            // components, while a copy in an ordinary field is left looking at the previous generation.
            Raise(HotFile(ammo: 2f, rpm: 22));
            Patch();

            Assert.That(EM.GetComponentData<GunRef>(entity).Gun.IsResolved, Is.True, "the slot in the component moved over");

            Assert.That(s_Cached.IsResolved, Is.False,
                "while the copy did not, and it is obliged to be recognisable as dead: otherwise the next Value " +
                "reads a buffer that is about to be freed");
            Assert.Throws<InvalidOperationException>(() => Copy(s_Cached.Value),
                "reading a stale copy is an error, not the bytes of the previous generation");
        }

        [Test]
        public void IsSet_does_not_promise_that_Value_can_be_read()
        {
            var file = HotFile();
            Raise(file);
            var entity = Gun(file["gun"]);

            var slot = EM.GetComponentData<GunRef>(entity).Gun;

            // Two similar properties side by side — the most common mistake with such an API.
            Assert.That(slot.IsSet, Is.True, "the offset is assigned");
            Assert.That(slot.IsResolved, Is.False, "but there has been no patch yet");

            Assert.Throws<InvalidOperationException>(() => Copy(slot.Value),
                "IsSet means \"a record is assigned\" and not \"it can be read\"; confusing them is obliged to hurt, not to be quiet");
        }

        [Test]
        public void The_BlobAssetReference_habit_of_reading_Value_right_after_AddComponent()
        {
            var file = HotFile();
            Raise(file);

            // In Unity BlobAssetReference<T>.Value works right after the bake. Here the import patch
            // stands between the bake and the read, and before it the slot holds an offset.
            var entity = EM.CreateEntity();
            EM.AddComponentData(entity, new GunRef { Gun = new BlobchegReference<PatchGun>(file["gun"]) });

            var slot = EM.GetComponentData<GunRef>(entity).Gun;

            var error = Assert.Throws<InvalidOperationException>(() => Copy(slot.Value),
                "a habit from a neighbouring API is obliged to run into an explicit error and not into zeroes");

            Assert.That(error.Message, Does.Contain("is not patched"),
                "the message is obliged to explain exactly THIS and not \"something went wrong\"");
        }

        [Test]
        public void A_copy_pasted_baker_with_an_offset_of_a_foreign_domain_is_obliged_to_be_rejected()
        {
            var hot = Raise(HotFile());

            var coldFile = Domain(nameof(IPatchCold));
            for (var i = 0; i < 32; i++)
                coldFile.Add("note" + i.ToString("D2"), new PatchNote { Tier = i });

            coldFile.Seal();
            Raise(coldFile);

            // The line was copied, the type was changed, the offset was forgotten: an address from the
            // cold base travelled into a hot reference.
            var strayOffset = coldFile["note31"];
            Assert.That(strayOffset, Is.GreaterThan((uint)hot.Length));

            Gun(strayOffset);

            Assert.Throws<InvalidOperationException>(() => Patch(),
                "a foreign offset is obliged to be rejected and not to turn into a pointer to somewhere");
        }

        [Test]
        public void Two_references_to_one_record_are_equal_both_before_and_after_the_patch()
        {
            var file = HotFile();
            Raise(file);
            var offset = file["gun"];

            var a = new BlobchegReference<PatchGun>(offset);
            var b = new BlobchegReference<PatchGun>(offset);

            Assert.That(a.Data.Value, Is.EqualTo(b.Data.Value), "equal before the patch");

            var first = Gun(offset);
            var second = Gun(offset);
            Patch();

            Assert.That(SlotOf(first), Is.EqualTo(SlotOf(second)),
                "equal after the patch too: otherwise \"if (a == b)\" starts lying right after a scene load");
        }

        // A finding of this set, closed inside the package: the slot had neither an equality operator nor
        // IEquatable<>, and the only working comparison left was ValueType.Equals — boxing and field
        // reflection, unavailable in a job at all. Now the comparison is our own and answers the same way
        // before and after the patch.
        [Test]
        public void Comparing_references_must_not_go_through_boxing()
        {
            var file = HotFile();
            var hot = Raise(file);
            var offset = file["gun"];

            var a = new BlobchegReference<PatchGun>(offset);
            var b = new BlobchegReference<PatchGun>(offset);
            var other = new BlobchegReference<PatchGun>(file["armor"]);

            Assert.That(a == b, Is.True, "two references from one offset are obliged to be equal before the patch");
            Assert.That(a.Equals(b), Is.True);
            Assert.That(a.GetHashCode(), Is.EqualTo(b.GetHashCode()));
            Assert.That(a != other, Is.True, "different offsets mean different references");

            // And the same after the patch: if the comparison starts answering differently, "if (a == b)"
            // in game code starts lying right after a scene load.
            var first = Gun(offset);
            var second = Gun(offset);
            Patch();

            var pa = EM.GetComponentData<GunRef>(first).Gun;
            var pb = EM.GetComponentData<GunRef>(second).Gun;

            Assert.That(pa == pb, Is.True, "after the patch the answer is obliged to stay the same");
            Assert.That(pa.Data.Value, Is.EqualTo(hot.AddressOf(offset)), "it was the patched slots that were compared");
        }

        [Test]
        public void An_unassigned_editor_field_does_not_turn_into_a_record_at_zero()
        {
            Raise(HotFile());

            var empty = default(BlobchegRef<PatchGun>);
            Assert.That(empty.IsSet, Is.False);

            // The optimistic path — "let's just call ToReference()" — is obliged to run into an error and
            // not to hand out a slot that later quietly points into the header.
            var error = Assert.Throws<InvalidOperationException>(() => empty.ToReference(),
                "an empty editor field has no right to become a reference to a record at offset zero");

            Assert.That(error.Message, Does.Contain(nameof(PatchGun)));
        }

        [Test]
        public void ToReference_and_the_constructor_give_one_and_the_same_slot()
        {
            var file = HotFile();
            var hot = Raise(file);
            var offset = file["gun"];

            var carrier = ScriptableObject.CreateInstance<BlobchegRefSo>();
            try
            {
                carrier.offset = offset;
                carrier.recordType = typeof(PatchGun).FullName;
                carrier.domainName = nameof(IPatchHot);

                var field = new BlobchegRef<PatchGun>(carrier);

                var viaField = field.ToReference();
                var viaCtor = new BlobchegReference<PatchGun>(offset);

                Assert.That(viaField.Data.Value, Is.EqualTo(viaCtor.Data.Value),
                    "two ways of doing the same thing are obliged to agree — otherwise the address has two truths");

                var entity = EM.CreateEntity();
                EM.AddComponentData(entity, new GunRef { Gun = viaField });
                Patch();

                Assert.That(SlotOf(entity), Is.EqualTo(hot.AddressOf(offset)));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(carrier);
            }
        }

        [Test]
        public void ToReference_with_the_asset_of_a_foreign_record_is_obliged_to_be_rejected()
        {
            var file = HotFile();
            Raise(file);

            var carrier = ScriptableObject.CreateInstance<BlobchegRefSo>();
            try
            {
                // The picker put the ARMOR asset into a field typed with the gun.
                carrier.offset = file["armor"];
                carrier.recordType = typeof(PatchArmor).FullName;
                carrier.domainName = nameof(IPatchHot);

                var field = new BlobchegRef<PatchGun>(carrier);

                var error = Assert.Throws<InvalidOperationException>(() => field.ToReference(),
                    "the record type check is obliged to stand on the new path as well");

                Assert.That(error.Message, Does.Contain(typeof(PatchArmor).FullName));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(carrier);
            }
        }

        [Test]
        public void Installing_the_patch_again_does_not_rebuild_the_table()
        {
            // A developer calls Install from their own bootstrap "just in case" — and does it before or
            // after the editor called it.
            var before = BlobchegPatchTableBuilder.RegisteredTypes.Count;

            BlobchegPatchInstall.Install();
            BlobchegPatchInstall.Install();

            Assert.That(BlobchegPatchTable.IsBuilt, Is.True);
            Assert.That(BlobchegPatchTableBuilder.RegisteredTypes.Count, Is.EqualTo(before),
                "installing again has no right either to double the list of types or to lose the table");

            // And the patch is obliged to work afterwards.
            var file = HotFile();
            var hot = Raise(file);
            var entity = Gun(file["gun"]);

            Patch();
            Assert.That(SlotOf(entity), Is.EqualTo(hot.AddressOf(file["gun"])));
        }
    }
}
