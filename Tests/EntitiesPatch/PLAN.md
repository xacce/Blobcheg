# The plan of the destructive set: the reference patch on scene import

Written **blind**: before a single line of `BlobchegReference.cs`, `BlobchegBases.cs`,
`EntitiesPatch/*` and the edits in `SerializeUtility.cs`. Only the package README, `Core/*`,
`Tests/*` and `Samples~/AdvancedTests` were read — for the shape of the format and the style, not for
the expectations.

The expectations are worded platform-neutrally: "an explicit error", "a deterministic result". The
concrete exception type is a detail of the phase-3 adaptation, not a part of the expectation.

Never acceptable: a silent no-op, silent corruption, a hang, garbage in a field, non-determinism. And
separately: **no test has the right to bring the editor down** — reading freed memory and dereferencing
a wild pointer are not executed, they are staged so that their presence is visible (we ask the registry
instead of dereferencing).

## What exactly is being broken

Six promises of the feature. Each is covered by at least two tests, and both of them try to BREAK it.

| # | The promise | Tests |
|---|---|---|
| 1 | The patch is idempotent | 13, 14, 16, 51 |
| 2 | The domain is not loaded — an explicit error | 12, 18, 37 |
| 3 | A rebuild translates the pointers already handed out | 23, 24, 25, 38, 39, 51 |
| 4 | What travels to disk is an offset and not a process address | 34, 35, 36, 37, 50 |
| 5 | Buffers element by element, a nested slot is found | 26, 27, 28, 29, 30, 31, 32 |
| 6 | Outside any domain and in two domains — an error | 20, 21, 49 |

## The categories

Seven were taken from the skill's table (at least 3 are required): empty/zero, boundaries, wrong order,
abuse of identity, state corruption, volume, semantic abuse, human factor, absurd.

## The bench

The files of a domain are assembled **by the writer, not by assets**:
`BlobchegWriter.Open(temp, "IPatchHot")` → `Flush` → bytes → `BlobchegBuffer.From` →
`new PatchHotDb(buffer)`. That way the test controls the layout down to the byte (needed for "an offset
exactly on the last record", "a generation moved the record") and does not pay for an asset rebuild. The
second road — `BlobchegBuild.RebuildAll()` — is taken only where the test is about the editor cycle
(test 44).

The world is its own (`new World(...)`), not the default one: what has to be proven is the patch, not
the order in which the editor creates its systems.

The key operations of the bench, the names are preliminary and get pinned down in phase 3:

- `Resolve(world)` — the forward patch (what the loading of a subscene section does);
- `Unresolve(world)` — the reverse pass (what the writing of a world does);
- `SaveLoad(world)` — serialising the world into memory and back into a new world.

## The model

Domains: `IPatchHot`, `IPatchCold`, `IPatchGhost` (declared with a base, but never loaded).

Records: `PatchGun : IPatchHot`, `PatchArmor : IPatchHot`, `PatchNote : IPatchCold`,
`PatchLoose` (without a marker at all), `PatchBoth : IPatchHot, IPatchCold`,
`PatchRefRecord : IPatchHot` — a record with a `BlobchegReference<PatchGun>` INSIDE it.

Components: a lone slot; a slot as the second field under `Pack = 1`; a slot at the second and at the
third level of nesting; a pair of slots of different types; a buffer element; the same slot in a shared
component.

---

## 1. Empty and zero

| # | The test | What we do | What we expect |
|---|---|---|---|
| 1 | `Zero_in_a_slot_is_not_a_record_at_address_zero` | a component with `default(BlobchegReference<PatchGun>)`, we patch the world | the slot stays exactly `default`; `IsSet == false`; `IsResolved == false`; `Value` throws. NOT the address `base+0`, that is, not the start of the header |
| 2 | `Patching_a_world_without_a_single_reference_neither_throws_nor_touches_anything` | a world of entities without slots, we patch | quietly and without an error; the components are the same byte for byte |
| 3 | `A_buffer_of_zero_length_is_patched_without_a_single_touch` | an entity with a `DynamicBuffer<PatchRefElement>` of length 0; the patch and the reverse pass | it does not throw, the length is still 0 |
| 4 | `A_reference_into_a_loaded_but_empty_base_is_obliged_to_be_rejected` | a domain of zero records (the file is a lone header), a reference with the offset `HeaderSize` | an explicit error: past the end of the file. NOT a pointer at the first byte after the header |
| 5 | `A_zero_slot_outlives_the_patch_and_the_reverse_pass_as_zero` | `default` → the patch → the reverse pass | exactly `default`. Catches the subtraction of the base address from zero (a trip into `ulong.MaxValue`) |

## 2. Boundaries

| # | The test | What we do | What we expect |
|---|---|---|---|
| 6 | `An_offset_past_the_end_of_the_file_is_obliged_to_be_rejected_by_the_patch` | offset = the file length + 16 | an explicit error, with the domain and the offset in the message |
| 7 | `An_offset_of_uint_MaxValue_does_not_turn_into_a_wild_address` | offset = `uint.MaxValue` | an explicit error. NOT `base + 4 GB` |
| 8 | `An_offset_into_the_header_is_obliged_to_be_rejected` | offset = 8 | an explicit error: the records begin at `HeaderSize` |
| 9 | `An_offset_exactly_on_the_last_record_is_obliged_to_pass` | the offset of the last record of the file | it patches, `Value` equals what was written. The boundary that is obliged NOT to be rejected |
| 10 | `An_offset_off_the_alignment_is_obliged_to_be_rejected` | the offset of the last record + 1 | an explicit error: the start of a record is aligned to 16 |
| 11 | `An_offset_exactly_equal_to_the_file_length_is_obliged_to_be_rejected` | offset = `Length` | an explicit error. The off-by-one on the other side of test 9 |

## 3. Wrong order and the life cycle

| # | The test | What we do | What we expect |
|---|---|---|---|
| 12 | `A_patch_without_a_loaded_base_names_the_domain_in_the_message` | the domain `IPatchGhost` was never registered, we patch a reference into it | an explicit error, and the name of the domain in its text. Not zeroes in the field, not "nothing happened" |
| 13 | `A_double_patch_does_not_add_the_address_twice` | patch, remember the address, patch once more | the address and `Value` are the same. A second pass over a patched field is a no-op and not `base+base+offset` |
| 14 | `A_triple_patch_and_the_reverse_pass_return_the_original_offset` | patch ×3 → the reverse pass | the very offset we started from |
| 15 | `The_reverse_pass_over_an_unpatched_world_does_not_send_the_offset_negative` | a world that was NEVER patched → the reverse pass | the offsets did not change. Catches a blind subtraction of the base address (an underflow) |
| 16 | `A_double_reverse_pass_does_not_subtract_the_base_twice` | patch → the reverse pass → the reverse pass | one and the same offset both times |
| 17 | `Taking_a_base_off_the_register_while_pointers_are_live_is_obliged_to_be_visible` | registered, patched, took it off the register and freed the buffer | the registry no longer recognises that address as its own; the reverse pass over such a reference refuses explicitly (the domain can no longer be named from the address). We do not touch `Value` — that is a read of freed memory |
| 18 | `The_registry_cannot_tell_a_freed_but_unregistered_buffer_an_accepted_limit` | the buffer was freed, it was NOT taken off the register (the typical mistake), we patch | an explicit error. If the package cannot do it — the test is either a `// BUG:` or an accepted limit with an explanation |

## 4. Identity and the domain

| # | The test | What we do | What we expect |
|---|---|---|---|
| 19 | `An_offset_of_a_foreign_base_outside_its_own_is_obliged_to_be_rejected` | an offset valid only in `IPatchCold`, put into a `BlobchegReference<PatchGun>` | out of bounds — an error; in bounds — a rejection by the record type (the debug contour). Never a silent read of someone else's bytes |
| 20 | `A_record_outside_any_domain_is_obliged_to_be_an_error_and_not_a_guess` | `BlobchegReference<PatchLoose>`, `PatchLoose` implements no marker at all | an explicit error naming the type. Compilation or the assembly of the patch table — both are fine, silence is not |
| 21 | `A_record_in_two_domains_at_once_is_obliged_to_name_both` | `BlobchegReference<PatchBoth>`, the type is in `IPatchHot` and in `IPatchCold` | an explicit error, with both domains in the message |
| 22 | `One_offset_in_two_components_gives_one_address_and_one_offset_back` | the same offset in two DIFFERENT component types on two entities | both point at one address; the reverse pass returns one and the same original offset to both |
| 23 | `Registering_a_domain_again_has_no_right_to_leave_pointers_looking_at_the_old_one` | `Register(hash, A)` → patch → `Register(hash, B)` | EITHER the second registration throws explicitly, OR the pointers already handed out moved into B. The middle is not allowed: the registry shows B while the pointers look into A |
| 24 | `Two_rebuilds_in_a_row_are_obliged_to_bring_the_pointer_to_the_third_generation` | gen1 → patch → gen2 → gen3, the content of the record is its own in every generation | `Value` reads gen3. Neither gen1, nor gen2, nor garbage |
| 25 | `A_generation_that_moved_a_record_has_no_right_to_hand_out_the_neighbouring_one` | gen2 is laid out so that the record moved (a new type appeared in front of it) | EITHER the reference travels after the record, OR an explicit error. A neighbour handed out silently is corruption |

## 5. Component layout and buffers

| # | The test | What we do | What we expect |
|---|---|---|---|
| 26 | `A_slot_as_the_second_field_after_an_unaligned_byte` | `[StructLayout(Sequential, Pack = 1)]`, `byte Head` then the slot (byte offset 1) | the slot is found and patched; `Head` is untouched; the reverse pass returns the offset |
| 27 | `A_slot_at_the_second_level_of_nesting` | a component `{ int, Inner }`, `Inner { int, slot }` | found |
| 28 | `A_slot_at_the_third_level_of_nesting` | one more level of nesting | found. The walk is obliged to be recursive and not "the fields of the first level" |
| 29 | `Two_slots_of_different_record_types_in_one_component_cannot_be_mixed_up` | `{ BlobchegReference<PatchGun>, BlobchegReference<PatchArmor> }` | both are patched, each onto ITS OWN record; swapping them is impossible |
| 30 | `A_buffer_of_three_elements_is_patched_element_by_element` | a `DynamicBuffer<PatchRefElement>` with three different offsets | all three are resolved, each into its own record |
| 31 | `A_broken_element_in_the_middle_of_a_buffer_does_not_leave_the_neighbours_half_patched` | a buffer `[good, past the end of the file, good]` | an explicit error AND a consistent state: after it the reverse pass returns their original offsets to the untouched elements. Not a single element was left a raw pointer that would travel to disk as an address |
| 32 | `A_buffer_of_a_hundred_thousand_elements_is_patched_whole` | volume | the first, the middle and the last one resolve correctly |
| 33 | `Ten_thousand_entities_with_a_slot_are_patched_in_one_pass` | volume | all resolve; a spot check of the values |

## 6. World serialisation

| # | The test | What we do | What we expect |
|---|---|---|---|
| 34 | `A_saved_world_contains_an_offset_and_not_a_process_address` | patch → serialisation into memory → **we scan the raw bytes of the stream** | not a single 8-byte word equal to the resolved pointer. And: reading into a new world where the base stands at a DIFFERENT address gives the right value |
| 35 | `After_a_save_the_live_world_stays_patched` | patch → serialisation → we read `Value` in the original world | still resolved. The reverse pass is obliged to restore and not to leave the world taken apart |
| 36 | `A_world_that_was_never_patched_is_saved_and_read_correctly` | no patch → serialisation → loading → patch → reading | the right value. The reverse pass over raw offsets is a no-op |
| 37 | `Saving_after_the_domain_was_taken_off_the_register_is_obliged_to_be_rejected` | patch → took the domain off the register → serialisation | an explicit error. Never a process address in the file |
| 38 | `A_world_saved_in_one_generation_is_read_in_another` | patch gen1 → serialisation → gen1 freed, gen2 loaded at a different address → loading | it resolves into gen2 |

## 7. The human factor

| # | The test | What we do | What we expect |
|---|---|---|---|
| 39 | `A_reference_copied_into_an_ordinary_field_does_not_move_with_a_rebuild` | a developer puts a resolved reference into a static field "so as not to look it up every frame", then a rebuild translates the base | the stale copy is obliged to be RECOGNISABLE: the registry does not know its address, `IsResolved` does not lie "yes". Promise 3 covers slots in components but not copies — so a copy is obliged to be visible as dead rather than to read silently |
| 40 | `IsSet_does_not_promise_that_Value_can_be_read` | `IsSet == true` before the patch, the developer calls `Value` | an explicit error, "is not resolved". Mixing up the two similar properties is the most common mistake on this API |
| 41 | `A_copy_pasted_baker_with_an_offset_of_a_foreign_domain_is_obliged_to_be_rejected` | a line of the baker was copied and only the type changed: `new BlobchegReference<PatchGun>(coldOffset)` | a rejection (bounds or type). Never a silent read |
| 42 | `The_BlobAssetReference_habit_of_reading_Value_right_after_AddComponent` | in Unity `BlobAssetReference.Value` works right after baking; here it is before the patch | an explicit error and not zeroes. The developer brings a habit from the neighbouring API |
| 43 | `Two_references_to_one_record_are_equal_both_before_and_after_the_patch` | `a == b` on two references made from one offset | deterministic and identical in both states. Different answers before and after the patch are a trap: an `if (a == b)` in game code would start lying after a scene load |
| 44 | `An_unassigned_editor_field_does_not_turn_into_a_record_at_zero` | `default(BlobchegRef<PatchGun>).ToReference()` | equals `default(BlobchegReference<PatchGun>)`, `IsSet == false`. Two ways of doing one and the same thing are obliged to agree; zero is obliged to mean "not assigned" and not "the first record" |

## 8. The absurd

| # | The test | What we do | What we expect |
|---|---|---|---|
| 46 | `The_patch_has_no_right_to_touch_a_single_byte_inside_the_base_itself` | the FILE holds `PatchRefRecord { BlobchegReference<PatchGun> Inner; }`; a component holds a reference to it; we patch the world | the bytes of the file are identical before and after the patch. The patch walks the memory of components and not the content of a base; otherwise it corrupts the base itself — and corrupts it for everyone who reads it the old way through `db.Read<T>` |
| 47 | `A_base_registered_at_the_address_of_a_foreign_record_answers_deterministically` | we take a resolved pointer to a record and register a second domain AT THAT address | the registration refuses (the address is inside another registered buffer), OR the registry answers about both addresses deterministically. Not allowed: the reverse pass picks the wrong domain and writes a garbage offset into the file |
| 48 | `A_slot_in_a_shared_component_is_either_patched_or_rejected_out_loud` | the same slot, but in an `ISharedComponentData` | EITHER an explicit refusal on setting/patching, OR it is patched like everything else. Skipped silently and then serialised as a process address is corruption: shared components travel into the file too |
| 49 | `A_reference_to_the_base_itself_as_a_record_is_obliged_to_be_rejected` | `BlobchegReference<PatchHotDb>` — a base is formally `unmanaged` and implements no markers | an explicit error of promise 6. Two features that were never designed together |
| 50 | `A_cloned_entity_carries_a_resolved_pointer_while_an_offset_travels_to_disk` | patch → `Instantiate` a hundred copies (the clones get an already resolved pointer, they never went through the patch) → serialisation | the clones carry an offset in the file too. If the reverse pass only walks those it patched itself, the pointers will leak into the file |
| 51 | `One_chunk_with_two_generations_at_once` | patched A over gen1 → moved the base to gen2 → added B with a raw offset into the same archetype → patch once more | both A and B read gen2. A mixed chunk (partly patched, partly not) is exactly what the live path with a change set produces |

---

## What I consider debatable in advance

- **Test 23 against promise 3.** A rebuild under a live editor IS registering the domain again. So
  either `Register` is itself the way to translate, and then "two bases over one domain" pass silently,
  or the translation is a separate call, and then a bare repeated `Register` is obliged to throw. Both
  forks are acceptable; the middle is not.
- **Tests 20, 21, 49** may turn out to be a compilation error rather than a runtime one. Then the
  offending types cannot be kept alive in the assembly: they would poison the patch table for all the
  other tests. The check would move onto reflection or into a separate assembly, and if even that is
  impossible — an `// API DESIGN:`.
- **Test 18** (a Dispose without unregistering) may be fundamentally unsolvable: the value struct of a
  base has no cell that outlives the freeing of the memory — the same accepted limit that is pinned
  down in `Samples~/AdvancedTests`. Then it is not a BUG but a limit, and the test pins it down with an
  explanation.
- **Test 34** — scanning the raw stream — is the only honest check of promise 4. The indirect one
  (loaded into another world, the value is right) passes on a file with an address inside it too, if
  the new world happened to load the base at the same address.

---

# Appendix: what phase 3 changed in the plan

The expectations were not fitted. Below is exactly what had to be rewritten, and why that is legitimate.

1. **Tests 20, 21, 49 moved onto reflection.** The worry from "the debatable" was confirmed literally:
   `BlobchegPatchTableBuilder.Build` walks ALL the component types of the process and throws on the very
   first reference into a record without a domain — that is, a live component with a reference to
   `PatchLoose` would switch the patch off for the whole project rather than for one test. There is no
   public "check one type" entry point. The check goes through the private `DomainKeyOf`, and above it
   stands an `// API DESIGN:` — this is not an adaptation of the expectation but a statement that from
   the public surface the expectation cannot be checked.

2. **Test 23 was reformulated into a disjunction, not softened.** Reality gave a third outcome the plan
   did not foresee: the pointer stays in the previous generation, but a read through it refuses
   explicitly (`IsKnownAddress` looks only at the current generation). The premise "the middle is not
   allowed: the registry shows the new one while the pointers silently read the old one" holds all the
   same. That is exactly what the test checks.

3. **Test 18 was recognised as an accepted limit and not a BUG.** The value struct of a base has no cell
   that outlives the freeing of the memory — the same limit that is already pinned down in
   `Samples~/AdvancedTests`. The test stayed, but it pins down the limitation with an explanation.

4. **Test 43 split in two.** The expectation "the comparison is deterministic" holds, but the type has no
   way to compare without boxing at all: neither `==` nor `IEquatable<>`. The behavioural half is an
   ordinary test, the second one is an `[Ignore]` with an `// API DESIGN:`.

5. **Test 44 expected `default` and got an exception.** `ToReference()` goes through
   `BlobchegRef<T>.Offset`, and that one throws on an empty asset. An explicit error does not contradict
   the contract, and the test was rewritten onto it; the expectation "zero is obliged to mean 'not
   assigned'" is checked directly in test 1 all the same.

6. **Test 25 turned out worse than assumed.** The plan allowed "either it travels after the record or an
   explicit error". Reality gave a third: `TryResolve` translates generations with the arithmetic
   `start + (value - prev)` and silently hands out the NEIGHBOURING record. That is corruption, and the
   test was left red.

7. **Added beyond the plan** (found while reading the contract, not while fitting): the order "take the
   old base off the register, then load the new one" loses every pointer handed out; writing a world with
   an unregistered domain puts a process address into the file and is not cancelled; the message about an
   unloaded domain names the FNV-64 key instead of the name; the sixty-fifth domain; unregistering with a
   foreign pointer.
