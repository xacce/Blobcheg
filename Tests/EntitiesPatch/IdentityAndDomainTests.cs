using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using Unity.Entities;

namespace Blobcheg.PatchTests
{
    /// <summary>
    /// The identity of a record and the identity of a domain. The section asks one thing: can a slot
    /// quietly hand out the wrong record — of a foreign type, of a foreign domain or of a foreign
    /// generation of the buffer.
    /// </summary>
    public sealed unsafe class IdentityAndDomainTests : PatchFixture
    {
        // ------------------------------------------------------------- a foreign type and a foreign domain

        // The plan (line 19) demanded: "inside the bounds — a rejection by record type (the debug
        // contour). Never a silent read of someone else's bytes." The plan did not specify the MOMENT of
        // the rejection, and the implementation chose the early one: the record type now reaches the slot
        // table (BlobchegFieldSlot.RecordTypeHash), and having got the address the patch asks the contour
        // straight away whether a record of the declared type starts there. If it does not — WrongRecord
        // right at the patch.
        //
        // That is stricter than the test expected (a rejection on reading Value) and closes the promise
        // earlier: it simply never comes to Value, a scene with such a slot cannot be imported. The check
        // that existed only on the old path made it to the new one — exactly what the plan demanded.
        [Test]
        public void A_record_read_through_a_slot_as_its_twin_is_obliged_to_be_rejected()
        {
            var file = HotFile(ammo: 42f, rpm: 7);
            var hot = Raise(file);
            var gunOffset = file["gun"];

            var carrier = EM.CreateEntity();
            EM.AddComponentData(carrier, new ArmorRef { Armor = new BlobchegReference<PatchArmor>(gunOffset) });

            // The old path refuses at the same offset — so the check does exist in the package.
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

            // The cold base is deliberately longer than the hot one: then its tail offset is past the end of the hot one.
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

        // ------------------------------------------------------------- one address, two consumers

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

        // ------------------------------------------------------------- buffer generations

        [Test]
        public void Registering_a_domain_again_has_no_right_to_leave_pointers_looking_at_the_old_one()
        {
            var first = HotFile(ammo: 1f, rpm: 11);
            var gen1 = Raise(first);
            var entity = Gun(first["gun"]);

            Patch();
            Assert.That(SlotOf(entity), Is.EqualTo(gen1.AddressOf(first["gun"])));

            // A rebuild in the "right" order: the new base is on the register, the old one is still alive.
            var gen2 = Raise(HotFile(ammo: 2f, rpm: 22));

            var slot = EM.GetComponentData<GunRef>(entity).Gun;

            // There must be no middle: either the pointer already looks at the new generation, or the read
            // refuses honestly. Quietly handing out the bytes of the old buffer is not allowed.
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

        // BUG: two rebuilds in a row without a patch between them lose the pointer
        // What happens: gen1 → gen2 → gen3 with no patch in between. The patch after the third
        //   registration fails with OutOfRange: the address of the first generation is found neither in
        //   the current one nor in the previous one.
        // What should happen: the promise of the feature — a rebuild translates the already handed-out
        //   pointers onto the new buffer. Two imports of an asset in one editor frame produce exactly two
        //   registrations in a row.
        // Root cause: BlobchegBases.Table holds EXACTLY ONE previous generation (PrevPtrs[slot]), and the
        //   repeated-Register branch overwrites it: PrevPtrs[slot] = Ptrs[slot]. After the third
        //   registration the address of the first buffer does not exist in the registry, and TryResolve
        //   goes into the last branch, where a heap address is certainly >= length.
        [Test]
        public void Two_rebuilds_in_a_row_are_obliged_to_bring_the_pointer_to_the_third_generation()
        {
            var first = HotFile(ammo: 1f, rpm: 11);
            Raise(first);
            var entity = Gun(first["gun"]);
            Patch();

            Raise(HotFile(ammo: 2f, rpm: 22));
            var gen3 = Raise(HotFile(ammo: 3f, rpm: 33));

            Assert.DoesNotThrow(() => Patch(),
                "two imports in a row are an ordinary day in the editor, and the pointers are obliged to outlive them");

            Assert.That(SlotOf(entity), Is.EqualTo(gen3.AddressOf(first["gun"])));
            Assert.That(Copy(EM.GetComponentData<GunRef>(entity).Gun.Value).Rpm, Is.EqualTo(33));
        }

        // The plan (line 25) allowed two outcomes: "EITHER the reference travels after the record, OR an
        // explicit error. A neighbour handed out silently is corruption." The implementation chose the
        // second half.
        //
        // Travelling after the record it cannot do and will not be able to: translating a generation is
        // the arithmetic `new base + previous shift`, and there is nothing to match the records of two
        // layouts with — the record itself carries neither a key nor a content hash. The check against
        // the debug contour, however, sees that the ARMOR and not the gun starts at the resulting address
        // and fails the patch with the WrongRecord code. That is exactly what the plan called the second
        // admissible outcome; the inadmissible one — silence — is closed.
        [Test]
        public void A_generation_that_moved_a_record_has_no_right_to_hand_out_the_neighbouring_one()
        {
            // gen1: the gun only.
            var first = Domain(nameof(IPatchHot)).Add("gun", new PatchGun { Ammo = 1f, Rpm = 11 }).Seal();
            Raise(first);

            var entity = Gun(first["gun"]);
            Patch();

            // gen2: the armor appeared before the gun — by FullName it comes first and moves the gun.
            var second = Domain(nameof(IPatchHot))
                .Add("armor", new PatchArmor { Hp = 500f, Plates = 9 })
                .Add("gun", new PatchGun { Ammo = 2f, Rpm = 22 })
                .Seal();

            Raise(second);
            Assert.That(second["gun"], Is.Not.EqualTo(first["gun"]), "the layout did not move — the test is checking the wrong thing");

            var error = Assert.Throws<InvalidOperationException>(() => Patch(),
                "the generation translation led the pointer to a foreign record — staying silent about that is not allowed");

            Assert.That(error.Message, Does.Contain(nameof(GunRef)),
                "the component is in the message — by it the scene can at least be found");
            Assert.That(error.Message, Does.Contain(nameof(IPatchHot)));

            // That the rejection fired on the record mismatch and not on every rebuild in general is held
            // by the neighbouring test: A_rebuild_with_a_patch_between_generations_brings_it_to_the_new_buffer
            // runs the same pair of generations with a layout that did NOT move and passes silently.
        }

        // ------------------------------------------------------------- the domain of a record
        //
        // Records "outside any domain" and "in two domains at once" cannot be put into a live component
        // of this assembly: BlobchegPatchTableBuilder.Build walks ALL the component types of the process
        // and fails entirely on the first such reference — that is, it would switch the patch off for the
        // whole project and not for one test. That is why the check goes straight through the domain
        // resolution.
        //
        // API DESIGN: the table build has no mode "check one type and say what is wrong". There is one
        // Build button for the whole process, and its refusal is a refusal of the patch as a whole, from
        // [InitializeOnLoadMethod], with one type in the text. There is nothing to diagnose "which other
        // components are declared wrongly" with, and a test for it cannot be written from the public
        // surface — below is reflection over the private DomainKeyOf.

        static Exception DomainFailure(Type record)
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
            // A base is formally unmanaged and squeezes into a BlobchegReference<T>. It has no domain —
            // its domain is in the attribute and not in an interface, and those are different things.
            var error = DomainFailure(typeof(PatchHotDb));

            Assert.That(error, Is.Not.Null,
                "a base is not a record of its own base; a reference to it is obliged to be rejected and not to invent a domain for itself");
            Assert.That(error.Message, Does.Contain(nameof(PatchHotDb)));
        }

        [Test]
        public void The_bare_innards_of_a_slot_in_a_component_field_are_obliged_to_be_an_error()
        {
            // The human factor: a developer looked inside BlobchegReference<T>, saw a
            // BlobchegReferenceData there and declared the "real" type as the field. No domain is derived
            // from it.
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

        /// <summary>
        /// Not a component: the type lives only for the sake of the test above. Declare it
        /// <c>IComponentData</c> and the table build fails at editor startup, switching the patch off for
        /// the whole project.
        /// </summary>
        struct NakedData
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
            // If a component with a reference to a record without a domain were found in the assembly,
            // Build would fail and ALL the other tests of the set would be checking emptiness while going
            // green.
            Assert.That(BlobchegPatchTable.IsBuilt, Is.True);

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
            // Insurance for the rig itself: the registry is a process-wide static, and a base left open by
            // a neighbouring test would make this set non-deterministic.
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
