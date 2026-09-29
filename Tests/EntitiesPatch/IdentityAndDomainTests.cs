using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using Unity.Entities;

namespace Blobcheg.PatchTests
{
    public sealed unsafe class IdentityAndDomainTests : PatchFixture // can a slot hand out a foreign type, domain or generation?
    {
        // A type mismatch is rejected at patch time (WrongRecord via the debug contour), not on Value.
        [Test]
        public void A_record_read_through_a_slot_as_its_twin_is_obliged_to_be_rejected()
        {
            var file = HotFile(ammo: 42f, rpm: 7);
            var hot = Raise(file);
            var gunOffset = file["gun"];

            var carrier = EM.CreateEntity();
            EM.AddComponentData(carrier, new ArmorRef { Armor = new BlobchegReference<PatchArmor>(gunOffset) });

            Assert.Throws<InvalidOperationException>(() => Copy(hot.Blob.Read<PatchArmor>(gunOffset)),
                "a Read as the twin is obliged to be rejected — that is an already closed finding of the package");

            var error = Assert.Throws<InvalidOperationException>(() => Patch(),
                "and the slot is obliged to be rejected at the same offset: otherwise the type check exists only on " +
                "the old path and the new one lost it");

            Assert.That(error.Message, Does.Contain(nameof(ArmorRef)),
                "the component is in the message — by it the scene can at least be found");
            Assert.That(error.Message, Does.Contain(nameof(IPatchHot)),
                "and the domain is named by name, not by a key");
        }

        [Test]
        public void An_offset_of_a_foreign_base_outside_its_own_is_obliged_to_be_rejected()
        {
            var hot = Raise(HotFile());

            // The cold base is longer than the hot one, so its tail offset lies past the hot end.
            var coldFile = Domain(nameof(IPatchCold));
            for (var i = 0; i < 32; i++)
                coldFile.Add("note" + i.ToString("D2"), new PatchNote { Tier = i, Extra = i * 2 });

            coldFile.Seal();
            Raise(coldFile);

            var far = coldFile["note31"];
            Assert.That(far, Is.GreaterThan((uint)hot.Length),
                "the cold base did not outgrow the hot one — the test is checking the wrong boundary");

            Gun(far);

            Assert.Throws<InvalidOperationException>(() => Patch(),
                "an offset of a foreign base that does not fit into its own is obliged to be rejected by the bounds");
        }

        [Test]
        public void One_offset_in_two_components_gives_one_address_and_one_offset_back()
        {
            var file = HotFile();
            var hot = Raise(file);
            var offset = file["gun"];

            var a = EM.CreateEntity();
            EM.AddComponentData(a, new GunRef { Gun = new BlobchegReference<PatchGun>(offset) });

            var b = EM.CreateEntity();
            EM.AddComponentData(b, new GunRefTwin { Gun = new BlobchegReference<PatchGun>(offset) });

            Patch();

            Assert.That(EM.GetComponentData<GunRef>(a).Gun.Data.Value, Is.EqualTo(hot.AddressOf(offset)));
            Assert.That(EM.GetComponentData<GunRefTwin>(b).Gun.Data.Value, Is.EqualTo(hot.AddressOf(offset)),
                "one offset means one address, whichever component it lies in");

            var bytes = Save();
            var loaded = LoadRaw(bytes);

            Assert.That(SlotOf(loaded, Single<GunRef>(loaded)), Is.EqualTo(offset));
            Assert.That(
                loaded.EntityManager.GetComponentData<GunRefTwin>(Single<GunRefTwin>(loaded)).Gun.Data.Value,
                Is.EqualTo(offset), "and that very same offset is obliged to come back to both");
        }

        [Test]
        public void Registering_a_domain_again_has_no_right_to_leave_pointers_looking_at_the_old_one()
        {
            var first = HotFile(ammo: 1f, rpm: 11);
            var gen1 = Raise(first);
            var entity = Gun(first["gun"]);

            Patch();
            Assert.That(SlotOf(entity), Is.EqualTo(gen1.AddressOf(first["gun"])));

            var gen2 = Raise(HotFile(ammo: 2f, rpm: 22)); // new base registered, old one still alive

            var slot = EM.GetComponentData<GunRef>(entity).Gun;

            // Either the pointer already follows the new generation or the read refuses; never stale bytes.
if (slot.Data.Value == gen2.AddressOf(first["gun"]))
                Assert.Pass("the pointer was translated by the registration itself");

            Assert.That(slot.IsResolved, Is.False,
                "the pointer is still in the previous generation — then IsResolved is obliged to say \"no\"");
            Assert.Throws<InvalidOperationException>(() => Copy(slot.Value),
                "and the read is obliged to refuse instead of handing out the bytes of a buffer that is about to be freed");
        }

        [Test]
        public void A_rebuild_with_a_patch_between_generations_brings_it_to_the_new_buffer()
        {
            var first = HotFile(ammo: 1f, rpm: 11);
            Raise(first);
            var entity = Gun(first["gun"]);
            Patch();

            var gen2 = Raise(HotFile(ammo: 2f, rpm: 22));
            Patch();

            Assert.That(SlotOf(entity), Is.EqualTo(gen2.AddressOf(first["gun"])));
            Assert.That(Copy(EM.GetComponentData<GunRef>(entity).Gun.Value).Rpm, Is.EqualTo(22));
        }

        [Test]
        public void Two_rebuilds_in_a_row_are_obliged_to_bring_the_pointer_to_the_third_generation()
        {
            var first = HotFile(ammo: 1f, rpm: 11);
            Raise(first);
            var entity = Gun(first["gun"]);
            Patch();

            Raise(HotFile(ammo: 2f, rpm: 22)); // two imports in one editor frame: gen1 must survive
            var gen3 = Raise(HotFile(ammo: 3f, rpm: 33));

            Assert.DoesNotThrow(() => Patch(),
                "two imports in a row are an ordinary day in the editor, and the pointers are obliged to outlive them");

            Assert.That(SlotOf(entity), Is.EqualTo(gen3.AddressOf(first["gun"])));
            Assert.That(Copy(EM.GetComponentData<GunRef>(entity).Gun.Value).Rpm, Is.EqualTo(33));
        }

        // A moved record cannot be followed (records carry no key), so the contour fails with WrongRecord.
        [Test]
        public void A_generation_that_moved_a_record_has_no_right_to_hand_out_the_neighbouring_one()
        {
            var first = Domain(nameof(IPatchHot)).Add("gun", new PatchGun { Ammo = 1f, Rpm = 11 }).Seal();
            Raise(first);

            var entity = Gun(first["gun"]);
            Patch();

            var second = Domain(nameof(IPatchHot)) // armor sorts first by FullName and moves the gun
                .Add("armor", new PatchArmor { Hp = 500f, Plates = 9 })
                .Add("gun", new PatchGun { Ammo = 2f, Rpm = 22 })
                .Seal();

            Raise(second);
            Assert.That(second["gun"], Is.Not.EqualTo(first["gun"]), "the layout did not move — the test is checking the wrong thing");

            var error = Assert.Throws<InvalidOperationException>(() => Patch(),
                "the generation translation led the pointer to a foreign record — staying silent about that is not allowed");

            Assert.That(error.Message, Does.Contain(nameof(GunRef)),
                "the component is in the message — by it the scene can at least be found");
            Assert.That(error.Message, Does.Contain(nameof(IPatchHot))); // unmoved-layout twin passes: this rejects the move

        }

        static Exception DomainFailure(Type record) // reflection: a faulty live component would disable the whole patch
        {
            var builder = typeof(BlobchegPatchTableBuilder);

            var collect = builder.GetMethod("CollectDomains", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.That(collect, Is.Not.Null, "CollectDomains was renamed — the domain resolution test went blind");

            var resolve = builder.GetMethod("DomainKeyOf", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.That(resolve, Is.Not.Null, "DomainKeyOf was renamed — the domain resolution test went blind");

            var domains = collect.Invoke(null, null);

            try
            {
                resolve.Invoke(null, new[] { record, domains });
                return null;
            }
            catch (TargetInvocationException e)
            {
                return e.InnerException;
            }
        }

        [Test]
        public void A_record_outside_any_domain_is_obliged_to_be_an_error_and_not_a_guess()
        {
            var error = DomainFailure(typeof(PatchLoose));

            Assert.That(error, Is.Not.Null, "there is nowhere to patch a record without a marker interface from — that is an error");
            Assert.That(error.Message, Does.Contain(nameof(PatchLoose)),
                "the message is obliged to carry the record name: there is nothing else to look for it by");
        }

        [Test]
        public void A_record_in_two_domains_at_once_is_obliged_to_name_both()
        {
            var error = DomainFailure(typeof(PatchBoth));

            Assert.That(error, Is.Not.Null, "which base to take the address from is not something to be guessed");
            Assert.That(error.Message, Does.Contain(nameof(IPatchHot)));
            Assert.That(error.Message, Does.Contain(nameof(IPatchCold)),
                "both domains are obliged to be named — otherwise it is unclear which of them is the extra one");
        }

        [Test]
        public void A_reference_to_the_base_itself_as_a_record_is_obliged_to_be_rejected()
        {
            var error = DomainFailure(typeof(PatchHotDb)); // its domain is an attribute, not an interface

            Assert.That(error, Is.Not.Null,
                "a base is not a record of its own base; a reference to it is obliged to be rejected and not to invent a domain for itself");
            Assert.That(error.Message, Does.Contain(nameof(PatchHotDb)));
        }

        [Test]
        public void The_bare_innards_of_a_slot_in_a_component_field_are_obliged_to_be_an_error()
        {
            // A developer typed the field as the BlobchegReferenceData innards; no domain derives from it.
            var walk = typeof(BlobchegPatchTableBuilder).GetMethod("Walk", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.That(walk, Is.Not.Null, "Walk was renamed — the field walk test went blind");

            var collect = typeof(BlobchegPatchTableBuilder)
                .GetMethod("CollectDomains", BindingFlags.NonPublic | BindingFlags.Static);
            var domains = collect.Invoke(null, null);

            var found = new List<BlobchegFieldSlot>();
            var seen = new HashSet<Type>();

            var error = Assert.Throws<TargetInvocationException>(
                () => walk.Invoke(null, new object[] { typeof(NakedData), 0, found, seen, domains, 0 }));

            Assert.That(error.InnerException, Is.TypeOf<InvalidOperationException>());
            Assert.That(error.InnerException.Message, Does.Contain(nameof(BlobchegReferenceData)));
        }

        struct NakedData // not IComponentData: that would fail the table build at startup
        {
            public BlobchegReferenceData Slot;
        }

        [Test]
        public void The_domain_key_is_computed_from_the_marker_name_and_matches_the_file_identity()
        {
            var hot = Raise(HotFile());

            Assert.That(hot.Key, Is.EqualTo(BlobchegNaming.NameHash(nameof(IPatchHot))),
                "the registry key and the file identity are obliged to be one number — otherwise the patch looks for the base in the wrong place");
            Assert.That(BlobchegBases.IsAddressOf(hot.Key, hot.AddressOf(BlobchegFormat.HeaderSize)), Is.True);
            Assert.That(BlobchegBases.IsAddressOf(BlobchegNaming.NameHash(nameof(IPatchCold)),
                hot.AddressOf(BlobchegFormat.HeaderSize)), Is.False,
                "the address of the hot base has no right to count as an address of the cold one");
        }

        [Test]
        public void The_test_model_did_not_poison_the_patch_table()
        {
            Assert.That(BlobchegPatchTable.IsBuilt, Is.True); // else every other test here goes green on emptiness

            var registered = BlobchegPatchTableBuilder.RegisteredTypes;
            var names = new List<string>();
            foreach (var type in registered)
                names.Add(type.GetManagedType().Name);

            foreach (var expected in new[]
                     {
                         nameof(GunRef), nameof(GunRefTwin), nameof(ArmorRef), nameof(NoteRef), nameof(GhostRef),
                         nameof(PairRef), nameof(PackedRef), nameof(ShallowNestRef), nameof(DeepNestRef),
                         nameof(RefElement), nameof(RecordRef),
                     })
                Assert.That(names, Does.Contain(expected), $"the walk did not find a slot in '{expected}'");

            Assert.That(names, Does.Not.Contain(nameof(PlainData)), "a component without slots does not belong in the table");
        }

        [Test]
        public void The_domain_registry_is_cleaned_between_tests()
        {
            // The registry is a process-wide static: a base leaked by a neighbour breaks determinism.
            Assert.That(BlobchegBases.TryGet(BlobchegNaming.NameHash(nameof(IPatchHot)), out _, out _), Is.False);
            Assert.That(BlobchegPatchErrors.HasAny, Is.False);
        }

        [Test]
        public void There_is_no_such_thing_as_an_empty_enumerator_of_registered_types()
        {
            Assert.That(BlobchegPatchTableBuilder.RegisteredTypes, Is.Not.Null);
            Assert.That(BlobchegPatchTableBuilder.RegisteredTypes.Count, Is.GreaterThan(0));
        }
    }
}
