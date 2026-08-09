# The destructive Blobcheg tests

A set whose job is to **break the package**, not to confirm that it works. The tests were written
blind: first the abuse scenarios and the expectations of "how a correct system is obliged to behave",
and only then the code of the package — for the names of the types, not for the expectations.

The first run gave 11 reds. Every red one was a finding, and the account of the findings is below: what
was closed in the package, what turned out to be removed by other work, and what stayed an accepted
limit. Right now the set is green all the way through — not because the expectations were fitted to the
code, but because the code caught up with the expectations.

## Why this is a separate set

`Tests/` of the package holds what is obliged to be green always. Here is a tool for the developer of
the package ITSELF: it drops a rebuild on purpose, corrupts files on disk, enters a rebuild from the
middle of a rebuild. A consumer of the package does not need this and it would cost them seconds of
import.

That is why the set lives in `Samples~` — a folder with a tilde is invisible to Unity, it does not enter
the consumer's compilation and does not cost them a single second. It is installed deliberately.

## How to install

Package Manager → **Blobcheg** → **Samples** → *Destructive tests (core dev)* → **Import**.
It lands in `Assets/Samples/Blobcheg/<version>/…` and appears in the Test Runner (EditMode).

From the CLI, without the Package Manager — `tools~/run-advanced-tests.ps1` in the root of the package:
it copies the set into the given project, runs `unity test --filter Blobcheg.AdvancedTests` and cleans
up after itself.

```powershell
./tools~/run-advanced-tests.ps1 -Project C:/Projects/Evuck/EvuckServer
```

## What is covered

Two roads, both end to end — from the entrance to a byte in the binary, without a single look into the
internals:

- **the editor cycle** — node assets → `RebuildAll` → files in StreamingAssets → ref/id assets →
  reading by reinterpretation;
- **the file cycle** — `BlobchegWriter`/`BlobchegRouterWriter` → bytes on disk → `BlobchegBlob`/
  `BlobchegRouterBlob`. Needed where assets cannot depict it: 64 bases, a hundred thousand router rows.

| File | About what |
|---|---|
| `EmptyAndLifecycleTests` | an empty domain, a record of zero length, a missing file, reading before the base is up and after `Dispose`, every way of lying in a node's declaration |
| `BoundaryAndTypeTests` | past the end of the file, into the header, past the alignment, exactly on the last record, 0/64/65 bases of a router, reading with a twin type |
| `IdentityTests` | an id and an offset of another router / another base, a substituted file, `default(BlobchegId)`, the stability of an id across an edit, a rename and identical names |
| `CorruptionTests` | truncated, appended to, a byte flipped, the magic/version/flags substituted, a lie in the router prolog |
| `ConcurrencyAndVolumeTests` | reading from a Burst job, reentrancy of a rebuild, 100k rows, writing into megabytes |
| `HumanFactorTests` | a saved id, a cached offset, a compaction, "the id is not zero so it exists", a manifest as proof, editing the binary by hand |
| `SemanticAndAbsurdTests` | two facades over one file, circular references between records, a node pointing at itself, an assembled blob at the entrance, a pointer inside a record |
| `ArrayDestructiveTests` | arrays in a record: a million elements, a forgotten Allocate, a literal with an array at depth, the window after End, a Write that threw, another builder, a recursive tree, ten edits of the length — the plan and the findings are in `PLAN-arrays.md` |

## What the set found and what was done about it

Three findings had one common root: **an address in this format had no identity**. Neither an offset nor
a `BlobchegId` carried a mark of its base/router or of the generation of the layout, and the header and
the prolog held anything except "whose file is this".

| The finding | What was done |
|---|---|
| an `id` of another router resolved silently | The high byte of `BlobchegId` is the **router tag**, the low three are the row number. Another's tag is rejected on the lookup. The uniqueness of the tags is proven by the router registry on the rebuild |
| `default(BlobchegId)` was a valid row 0 | By the same tag: zero is reserved, so a zero-initialised field is "not assigned", and the habitual `if (id != 0)` finally checks something |
| an offset from another base read silently | The **file** got an identity — the hash of the domain name in the header, checked when it comes up. The offset itself cannot have one, it is a position; another's address is caught by the debug contour — see below |
| a record read with a twin type silently | The type check sat behind `BLOBCHEG_DEBUG`, which nobody set — it existed on paper. Now the debug contour is written in the editor always, and the check lives under `ENABLE_UNITY_COLLECTIONS_CHECKS`, next to the bounds check. Only the pre-build gate removes the contour, and only for a release player |
| a record with a raw pointer drove into the file | `where T : unmanaged` lets `T*` and `IntPtr` through. The pipeline now checks the record type recursively and names the field |
| two empty raw records got ONE address | A record of zero length occupies a byte in the layout, not zero |
| a rebuild entered itself | The reentrancy guard moved from the import hook onto the rebuild itself |
| a renamed node fell out of the rebuild silently | The walk cannot be fixed: it was measured that in that pass of the editor the asset does not come up under the old path nor under the new one, and `ImportAsset(ForceSynchronousImport)` with a `Refresh` do not change that. So the rebuild now REFUSES: the walk remembers the GUIDs it has already seen, and a node whose file lies on disk while its type is not asked for is a loss, not a deletion. There are two walks now on top of that (the full walk lags behind a rename, the search index lags behind an import), and the incremental path now simply moves a node in the cache instead of re-reading it |
| a saved id after a neighbour was deleted led to another node | Removed by the work on address stability: an id is inherited from its carrier, a deleted node leaves an empty row |
| a cached offset lied after a neighbour appeared | Removed there as well: the previous address arrives in the layout as a claim. The test was rewritten — now it checks that the address does NOT move, and its pair checks that a compaction moves it and rewrites the carrier |

### The accepted limit

`A_copy_of_a_base_is_a_view_and_not_an_owner` is the only test that pins down a limitation rather than a
victory. A base is a value struct with an owning pointer, and it is made that way on purpose: it is put
into an `IComponentData` and copied by every `GetSingleton`. A version with ownership (a safety handle)
demands a cell that outlives the freeing of the memory itself — that is, either a leak or a registry
unreachable from Burst. The contract is plain: `Dispose` is called by whoever brought the base up, and
exactly once; the other instances are views.

The test exists so that the limit looks like a decision and not an oversight.

## The rules of the set

- The test judges the API and not the other way round. Having to fit an expectation to the code means
  the problem is in the code, and that is recorded with a `// BUG:` and not with an edit of the
  expectation. There is one exception: an expectation built on a premise that no longer exists (a
  layout that "is obliged to move") is rewritten together with the premise.
- No test has the right to bring the editor down: a double free, a read of freed memory and an infinite
  recursion are not executed, they are staged so that their presence is VISIBLE.
- The domains and the routers of the set live in its own assembly, so a consumer does not see them and
  they do not join their routers.
