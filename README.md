# Blobcheg

A database for Unity on top of binary files: the same mechanics as the blob assets of Entities, but
without subscenes. Burst-compatible, `BlobAssetReference` is not used.

---

## Contents

1. [Why](#why)
2. [What you get](#what-you-get)
3. [How to get the data](#how-to-get-the-data)
4. [What it looks like](#what-it-looks-like)
5. [Live reload](#live-reload)
6. [The binary in the build](#the-binary-in-the-build)
7. [The model](#the-model)
8. [Installation](#installation)
9. [Quick start](#quick-start)
10. [Nodes and records](#nodes-and-records)
11. [Arrays in a record](#arrays-in-a-record)
12. [References in authoring](#references-in-authoring)
13. [Loading a base](#loading-a-base)
14. [The router and BlobchegId](#the-router-and-blobchegid)
15. [The name hash: an address that outlives a rebuild](#the-name-hash-an-address-that-outlives-a-rebuild)
16. [BlobchegReference: a pointer instead of an offset](#blobchegreference-a-pointer-instead-of-an-offset)
17. [The rebuild](#the-rebuild)
18. [Checks and errors](#checks-and-errors)
19. [API reference](#api-reference)
20. [The assemblies of the package](#the-assemblies-of-the-package)
21. [Developing the package](#developing-the-package)

---

## Why

A game needs a database: stats, curves, configs — data that is assembled in the editor, lives from the
start to the exit and is read from Burst without copies. Unity's stock answer is blob assets, but out of
the box `BlobAssetReference` is bound to a subscene: a blob is baked by a baker, lives with the entity
scene and dies when it is unloaded. A game database does not need that control of lifetime — it is not
unloaded together with a scene — and there is little point in making a blob past a subscene: baking,
deduplication and the reference patch stay on the other side.

Blobcheg is the same blobs minus the subscene. The data is baked in the editor into binary files, at
runtime the file lies in memory whole, and reading a record is a reinterpretation of bytes. Entities are
optional: without them only the automatic loading of bases and the reference patch in components are
lost.

---

## What you get

The same read speed on the hot path and — if the patch is applied — the same mapping on entity import as
blobs have: the component holds an offset, on the loading of a subscene it is remapped into a pointer to
the record in the resident buffer, and `.Value` reads the memory directly, without a base at hand and
without an addition. Without the patch the ordinary road stays, "an offset plus `Read<T>`" — the
resident view of the base (`Resident`) and one addition.

On top of that, what blobs do not have:

- **several routers** — independent databases, each with its own set of files and its own id space;
- **several bases** in every router (up to 64) — one per domain, each in its own file;
- **different record types** inside one base — any `unmanaged` structs of its domain, rather than one
  type per file.

Together this gives the main trick: the data of one entity is spread across bases by the character of
its access — the hot fields apart from the icons and the descriptions — and what binds them is a common
index in the router; see [the example](#what-it-looks-like).

If a relational dictionary is more familiar: a router is a database, a base-domain is a table, a record
is a row. From here on the README calls them by their own names.

It fits everything that lives permanently in the game as data: unit stats, progression curves, loot
tables, recipes, dialogues. It does not fit heavy streaming buffers — mesh vertices, textures, audio:
those are assets, and Unity has its own pipeline for them.

---

## How to get the data

There are three addresses, and the choice between them is not taste but the lifetime of the address and
the number of bases at hand.

**An offset** is the direct and the fastest road: if you know the record at bake time, keep a `uint` and
read `db.Read<T>(offset)`. With the patch the same slot is declared a `BlobchegReference<T>`: before the
import it holds an offset, after it an address, and `.Value` reads without a base and without an
addition; see [BlobchegReference](#blobchegreference-a-pointer-instead-of-an-offset).

**A BlobchegId** — when what you have is the name of a node and it has several records: one `uint`
instead of a bunch of offsets. The router hands out by it the row with the offsets of the node in all of
its bases; see [The router](#the-router-and-blobchegid).

**A name hash** is the address for a save. An offset and an id are stable within one build of the base,
a compaction hands them out anew, while the hash is computed from the name of the node and outlives any
rebuild; a table unfolds it back into an id. See
[The name hash](#the-name-hash-an-address-that-outlives-a-rebuild).

---

## What it looks like

A project with combat and a meta layer. Two routers, each with its own bases, and in every base records
of several types:

```
GameRouter — combat: the entity is spread across bases by the character of access
├─ CombatDb        IHotPathCombatData   WeaponHotData, UnitHotData, ProjectileHotData        ← the hot path
├─ ProgressionDb   IProgressionData     WeaponProgressionData, UnitProgressionData, TalentData
└─ PresentationDb  IPresentationData    WeaponPresentationData, UnitPresentationData, ProjectileVfxData

MetaRouter — economy and narrative: thematic tables
├─ EconomyDb       IEconomyData         ItemData, RecipeData, LootTableData, VendorData
├─ QuestDb         IQuestData           QuestData, QuestStageData, RewardData
└─ DialogueDb      IDialogueData        SpeakerData, DialogueLineData, ChoiceData
```

`GameRouter` shows the main trick: the data of one entity is **spread across bases while their index is
common**. A weapon is `WeaponHotData` in the hot base, `WeaponProgressionData` and
`WeaponPresentationData` in the others; all three are records of one node, tied together by one
`BlobchegId`. A combat job loads only the hot base into the cache and does not pay for dialogue lines
and icons, the UI reads the presentation, and the row of the router they share is one. `MetaRouter` is
the other pole of the same mechanism: the bases are cut thematically, and a quest node writes only into
`QuestDb`.

The declaration is an attribute on a partial, the body is written by the generator:

```csharp
// the Game.Combat assembly
[BlobchegRouter] public partial struct GameRouter { }

[Blobcheg(typeof(IHotPathCombatData), "combatData")]   public partial struct CombatDb { }
[Blobcheg(typeof(IProgressionData), "progression")]    public partial struct ProgressionDb { }
[Blobcheg(typeof(IPresentationData), "presentation")]  public partial struct PresentationDb { }

// the Game.Meta assembly — the second router is declared in exactly the same way. Two routers in one
// assembly are fine too, and then the bases name theirs explicitly: Router = typeof(MetaRouter)
[BlobchegRouter] public partial struct MetaRouter { }
```

The data is filled in by nodes — `ScriptableObject` assets. A weapon node lays its entity out across the
bases in one `Write` — a record into each declared domain (the node class lives in any assembly that
references `Blobcheg.Authoring`, where `BlobchegNodeSo` lies):

```csharp
[CreateAssetMenu(menuName = "Game/Weapon")]
public sealed class WeaponNodeSo : BlobchegNodeSo
{
    public int rpm = 600;
    public float damage = 12f;
    public float upgradeStep = 1.15f;
    public uint muzzleVfx;
    public uint icon;
    public BlobchegNodeSo projectile;   // the projectile node: its id travels into the record

    public override Type[] OutTypes => new[]
        { typeof(IHotPathCombatData), typeof(IProgressionData), typeof(IPresentationData) };

    public override void Write(ref BlobchegNodeWriter w)
    {
        w.Add(new WeaponHotData
        {
            rpm        = rpm,
            damage     = damage,

            // everything below is optional: it goes in only if the record needs it
            id         = w.Id,                        // its own BlobchegId
            saveKey    = this.HashIn<GameRouter>(),   // its own name hash
            projectile = w.IdOf(projectile),          // the id of another node — a record → record reference
        });
        w.Add(new WeaponProgressionData { upgradeStep = upgradeStep });
        w.Add(new WeaponPresentationData { muzzleVfx = muzzleVfx, icon = icon });
    }
}
```

A node knows its own id and hash **before** the write — the ids are handed out by `OutTypes` earlier
than `Write`, and the hash is a pure function of the name — so both its own and other nodes' (`IdOf`)
go into the record in one pass, and the consumer gets them as ordinary fields. `HashIn` lives in
`Blobcheg.Hashes.Authoring`; the full set — `Id`, `IdIn<TRouter>`, `IdOf` — is in
[the writer's table](#what-blobchegnodewriter-can-do).

Different types in one base appear by themselves: next to `WeaponHotData` in `CombatDb` lie the
`UnitHotData` of units and the `ProjectileHotData` of projectiles. The file is one, the types are
different, and `Read<T>` will not let them be mixed up.

Next come all the ways to reach a record, in ascending order.

**An offset without the patch.** The record is picked in the inspector with a typed field, the baker
puts a bare `uint` into the component, and the read is the resident view of the base plus one addition:

```csharp
public sealed class TurretAuthoring : MonoBehaviour
{
    public BlobchegRef<WeaponHotData> weapon;   // the picker will show only WeaponHotData records

    sealed class Baker : Baker<TurretAuthoring>
    {
        public override void Bake(TurretAuthoring a)
        {
            DependsOn(a.weapon.Asset);
            AddComponent(GetEntity(TransformUsageFlags.None),
                new TurretWeapon { weapon = a.weapon.Offset });
        }
    }
}
```

```csharp
ref readonly var hot = ref combatDb.Read<WeaponHotData>(turret.weapon);
```

**An offset with the patch.** The same slot is declared a `BlobchegReference<T>`, the baker puts
`a.weapon.ToReference()` in, and on the import of the subscene the offset is remapped into an address —
exactly as with `BlobAssetReference`. The read is without a base and without an addition:

```csharp
public struct TurretWeapon : IComponentData
{
    public BlobchegReference<WeaponHotData> weapon;
}

ref readonly var hot = ref turret.weapon.Value;
```

**A BlobchegId.** One id is unfolded by the router into the row with the offsets of the entity in all of
its bases:

```csharp
var row = gameRouter.Get(weapon.id);   // one id — every aspect of the weapon
ref readonly var hot      = ref combatDb.Read<WeaponHotData>(row.combatData);
ref readonly var progress = ref progressionDb.Read<WeaponProgressionData>(row.progression);

if (row.HasPresentation)   // not every node writes into every base: a talent lives only in ProgressionDb
{
    ref readonly var look = ref presentationDb.Read<WeaponPresentationData>(row.presentation);
}

// an id that Write put into the record itself is unfolded the same way — records reference one
// another without a single managed object:
var projRow = gameRouter.Get(hot.projectile);
ref readonly var proj = ref combatDb.Read<ProjectileHotData>(projRow.combatData);
```

**A name hash.** The address for a save:

```csharp
save.weapon = hot.saveKey;                             // the hash is already in the record — Write put it there
save.other  = gameHashes.HashOf(other.id);             // or through the table, from any id
if (gameHashes.TryGetId(save.weapon, out var id)) ...  // on loading — back into an id
```

---

## Live reload

Editing a node in the editor rebuilds the file of the base, the boot system notices that, re-reads the
file and moves the already loaded entities onto the new buffer. The numbers change right inside a
running PlayMode, without a restart. How that is arranged is
[in the section about loading](#in-the-editor-the-base-is-re-read).

---

## The binary in the build

Into a build the base travels as binary files in `StreamingAssets/Blobcheg` — a file per base, per
router and per hash table, without recompiling the data into resources. On the desktop that is an
ordinary folder, and a file rebuilt in the editor can be slipped into a finished build. Editing the
values does not move the addresses of the records, so baked subscenes and saves will not notice the
substitution — the numbers can be balanced in a distributed build without rebuilding the player.

The substitution outlives an edit of the numbers, but not an edit of an array length: a record that grew
or shrank moves, while the address in the consumers of the build stays the old one. If a length changed,
the player is rebuilt.

---

## The model

Five notions, everything else is derived.

**A domain** is a marker interface, for example `IHotPathCombatData`. One domain = one base = one
`{Domain}.bcheg` file.

**A base** is a `partial struct` with the `[Blobcheg(typeof(IDomain))]` attribute. The generator writes
its constructor, `Read<T>`, `Dispose` and the file name. At runtime a base is the resident buffer of the
file.

**A record** is an `unmanaged` struct implementing the marker interface of the domain. Its bytes lie in
the file.

**A node** is a `ScriptableObject`, a descendant of `BlobchegNodeSo`. The unit of data in the editor: it
declares which domains it writes into and fills the records. Into every domain a node gives exactly one
record.

**An offset** is the only address of a record in a base. There are no tables in the file: the meaning of
a record is given by the offset alone, and it is the consumer who keeps it. The storage of an offset is
`BlobchegRefSo`, a sub-asset that the rebuild creates per (node × domain) pair and re-issues.

There is a second address too — the `BlobchegId`, the name of the node, common to all the bases of one
router. By it the router hands out the offsets of the node in all of its bases at once; see
[The router](#the-router-and-blobchegid).

And a third one — the hash of the node's name. It addresses nothing directly and is needed for exactly
what the first two are bad at: an offset and an id live for one build of the base, while a hash outlives
it. A separate table unfolds it into an id; see
[The name hash](#the-name-hash-an-address-that-outlives-a-rebuild).

The package does not check the content of a record: `Read<T>` reinterprets the bytes. What is checked is
the integrity of the file and its identity; in the editor and in a development build, additionally, the
bounds and the type of the record.

---

## Installation

Requirements: Unity 6000.3+, the Burst, Collections and Mathematics packages. Entities are optional,
they are needed only for the automatic loading of bases and for the reference patch in components.

The `com.xacce.blobcheg` package goes into the project's `Packages/` — as a submodule or as a dependency
in `Packages/manifest.json`. Nothing else has to be set up: the package finds the domains, the routers
and the nodes by attributes and types, and it hangs the rebuild on asset import itself.

Only two things are switched on separately:

| What | How | Why |
|---|---|---|
| The automatic loading of bases in ECS | `AutoLoad = true` in the attribute + reference the `Blobcheg.Entities` assembly | the codegen will emit a boot system |
| The `BlobchegReference<T>` patch | a fork of `com.unity.entities` + the `BLOBCHEG_ENTITIES_PATCH` define | a reference in a component becomes a pointer |
| Name hashes | reference the `Blobcheg.Hashes` assembly | `[BlobchegHashes]` and its file appear |

---

## Quick start

### 1. The domain, the record and the base — in a runtime assembly

```csharp
public interface IHotPathCombatData { }

public struct GunData : IHotPathCombatData
{
    public float ammoMax;
    public int rpm;
}

[Blobcheg(typeof(IHotPathCombatData))]
public partial struct CombatDb { }   // the ctor, Read<T>, Dispose and FileName are written by the generator
```

### 2. The node — in an assembly that references `Blobcheg.Authoring`

`BlobchegNodeSo` lives in `Blobcheg.Authoring`, a runtime assembly that holds only the node contract; the
rebuild and the editor tooling are in the Editor-only `Blobcheg.Authoring.Editor`. A node class can
therefore lie in a runtime assembly, and every assembly that touches a node type has to reference
`Blobcheg.Authoring` (otherwise `CS0012`).

```csharp
[CreateAssetMenu(menuName = "Combat/Gun")]
public sealed class GunNodeSo : BlobchegNodeSo
{
    public float ammoMax = 30f;
    public int rpm = 600;

    public override Type[] OutTypes => new[] { typeof(IHotPathCombatData) };

    public override void Write(ref BlobchegNodeWriter w)
        => w.Add(new GunData { ammoMax = ammoMax, rpm = rpm });
}
```

Create the asset through `Assets → Create → Combat/Gun`. The rebuild starts by itself on the import.

### 3. A reference to a record — a typed field

```csharp
public sealed class WeaponAuthoring : MonoBehaviour
{
    public BlobchegRef<GunData> gun;   // the picker will show only GunData records

    sealed class Baker : Baker<WeaponAuthoring>
    {
        public override void Bake(WeaponAuthoring a)
        {
            DependsOn(a.gun.Asset);
            AddComponent(GetEntity(TransformUsageFlags.None), new WeaponRef { gun = a.gun.Offset });
        }
    }
}
```

### 4. Loading the base

On Entities it is enough to set `AutoLoad = true` — the system is emitted by the codegen:

```csharp
[Blobcheg(typeof(IHotPathCombatData), AutoLoad = true)]
public partial struct CombatDb { }
```

Without Entities the loading is written by hand, see [Loading a base](#loading-a-base). A base is not
a world citizen: declaring it `IComponentData` is the compilation error `BCHG011`; the loaded base is
reached through `Resident` instead.

### 5. Reading

```csharp
var db = CombatDb.Resident;   // the loaded base, from any thread and from Burst
ref readonly var gun = ref db.Read<GunData>(weapon.gun);
```

A record of another domain will not compile in `Read<T>`: the method has the constraint
`where T : unmanaged, IHotPathCombatData`.

---

## Nodes and records

### The contract of a node

```csharp
public abstract class BlobchegNodeSo : ScriptableObject
{
    public string BlobchegName { get; }     // a stable name; an empty one is filled in by the rebuild
    public abstract Type[] OutTypes { get; }
    public abstract void Write(ref BlobchegNodeWriter writer);
}
```

`BlobchegName` is a field in the inspector, separate from the name of the asset. An empty one is filled
in by the rebuild once with the name of the asset and is never touched again: the name of a file is
changed by a human with a mouse, and on that name stands the
[hash](#the-name-hash-an-address-that-outlives-a-rebuild) that has travelled into other people's saves.

`OutTypes` is a declaration: the domains the node promises to write into. It is read **before** `Write`,
and out of it come the routers of the node and its id. A divergence between the declaration and the fact
is an error of the rebuild: it declared a domain and did not write into it, or wrote into an undeclared
one.

One node gives a domain exactly one record. A second one is an error.

A node of a router with `FixedIndex` implements `IBlobchegIndexed` on top of that — see
[a declared index](#a-declared-index).

### What `BlobchegNodeWriter` can do

| Method | What it does |
|---|---|
| `Add<T>(in T record)` | a typed record; the domain is derived from the marker interface of `T` |
| `Begin<T>()` | a builder of a record with arrays — see [Arrays in a record](#arrays-in-a-record) |
| `AddBytes<TDomain>(ReadOnlySpan<byte> bytes)` | a raw block: there is no type, so there are no checks by it either |
| `Id` | its own `BlobchegId`; zero or several routers is an exception |
| `IdIn<TRouter>()` | its own id in a particular router — for a node that belongs to several at once |
| `IdOf(node)` / `IdOf<TRouter>(node)` | the id of another node: that is how one record references another |

### The requirements on a record type

- `unmanaged` — held by the compiler;
- no pointers inside (`T*`, `IntPtr`, `UIntPtr` at any depth of nesting) — checked by the rebuild. The
  `unmanaged` constraint lets a pointer through, and the address of someone else's memory outlives a
  write into a file but not a restart of the process;
- exactly one marker interface of a domain. Two is an error: a record is obliged to belong to one base;
- a type with a `BlobchegArray<T>` at any depth is written only through `Begin<T>()`. `Add` rejects such
  a type: the size of the record is known only after all the `Allocate` calls, and a struct literal
  would silently give arrays of zero length.

---

## Arrays in a record

`BlobchegArray<T>` is a typed array of variable length inside a record. Eight bytes in the struct
itself: a self-relative offset and a length; the elements lie as a tail inside the byte block of the
same record. The record stays an opaque block that travels through the file whole — the file format
knows nothing about an array.

### The declaration

```csharp
public struct CityData : ICityData
{
    public int Population;
    public BlobchegArray<QuarterData> Quarters;   // the length is assigned by the node, not by the type
}
```

An element is an ordinary `unmanaged` struct and may carry a `BlobchegArray<T>` of its own: that is how
nesting of any depth is built, including recursive types (a `TreeNode` with an array of `TreeNode`).

### Writing — only with a builder

```csharp
public override void Write(ref BlobchegNodeWriter w)
{
    var b = w.Begin<CityData>();
    b.Root.Population = population;

    var q = b.Allocate(ref b.Root.Quarters, quarters.Length);
    for (var i = 0; i < q.Length; i++)
        q[i] = quarters[i];

    b.End();
}
```

`Root` is the head of the record, its fields are filled in as usual. `Allocate(ref field, length)`
reserves the space and hands out a writing window; a length of zero is legal — the field stays an
emptiness. A field untouched by any `Allocate` also reads as an empty array and not as garbage. `End` is
obligatory: without it the rebuild fails with the name of the node. The window lives until `End` and not
a second longer — access after `End` throws.

### Reading

```csharp
ref readonly var city = ref db.Read<CityData>(reference.Offset);
for (var i = 0; i < city.Quarters.Length; i++)
    Use(city.Quarters[i]);
```

A record is held as `ref readonly` — copying a record into a local variable breaks the self-relative
offset, and a read from the copy throws (in the editor and in a development build) instead of handing
out garbage.

For a hot loop there is `GetUnsafePtr()`: the address is checked once, and after that the loop is pure
arithmetic. An empty array has a `null` pointer.

### The price

The elements lie inside the record, and it is its domain that pays for them: an array in a hot record is
the weight of every read of the neighbouring fields. There is no deduplication — two identical arrays in
two nodes lie as two copies. Editing a length moves the record and leaves a hole in the file; the holes
are reused by the following records, and they are brought to zero by the compaction, which stands on the
pre-build anyway.

---

## References in authoring

An offset and an id travel from the editor into the build through sub-assets that the rebuild hangs on a
node. They do not have to be created by hand, but they do have to be referenced.

| The carrier | The pair | What it carries |
|---|---|---|
| `BlobchegRefSo` | node × domain | the `offset` of the record |
| `BlobchegIdSo` | node × router | the `BlobchegId` of the node |

The consumer's field is typed, the asset is not.

| The field | What it hands out | What the picker shows |
|---|---|---|
| `BlobchegRef<T>` | `Offset`, `ToReference()` | only records of the type `T` |
| `BlobchegRawRef` | `Offset` | records from `AddBytes` |
| `BlobchegIdRef<TRouter>` | `Id` | only the nodes of this router |

All three reject a foreign asset three times over: by the compiler (the type parameter), by the drawer
in the inspector (the picker and drag-and-drop) and by an exception on the read at bake time. An empty
field throws instead of handing out a zero.

Every field has an `Asset` — for `DependsOn` in a baker. Without it the subscene will not be re-baked
when the address of the record moves.

---

## Loading a base

### On Entities — by codegen

Set `AutoLoad = true` on the attribute of the base, the router or the table and reference the
`Blobcheg.Entities` assembly. The generator will emit a `{Name}BootSystem` in the `BlobchegBootGroup`
group; the loaded blob is held by that system, not by the world — there is no singleton and no
component.

```csharp
[Blobcheg(typeof(IHotPathCombatData), "combatData", AutoLoad = true)]
public partial struct CombatDb { }   // CombatDbBootSystem is emitted by the codegen
```

The door to the loaded data is `Resident`. On a router it is the only door its bases need: the row by
id, and the typed view of every member base as a property. The view is assembled from the process
registry (`BlobchegBases`) by keys the generator bakes as constants (`DomainKey` on a base, `RouterKey`
on a router, `HashesKey` on a table) — there is no world in the road, so it works from managed code and
from a Burst job alike:

```csharp
var router = GameRouter.Resident;
var row = router.Get(id);
ref readonly var hot = ref router.CombatData.Read<WeaponHotData>(row.combatData);

var save = GameHashes.Resident;                     // the table has its own Resident
var alone = SettingsDb.Resident;                    // a base OUTSIDE any router gets its own too;
                                                    // a router member (CombatDb here) has no Resident
```

A readiness gate does not need a new API: `BlobchegBases.Has(CombatDb.DomainKey)` answers "is the file
up". The keys share one registry with the bases — its ceiling of 64 entries now counts bases, routers
and tables together.

`BlobchegBootGroup` stands at the beginning of `InitializationSystemGroup` (`OrderFirst`) and **before**
`BeginInitializationEntityCommandBufferSystem`: the systems that need the base are obliged to see it
earlier than their own entities.

A `[DisableAutoCreation]` on the base itself travels onto the emitted system — "the system is needed, but
who creates it is my decision". Nobody forbids a loading system of your own: put it into the same group.

There is no reference to `Blobcheg.Entities` while `AutoLoad` is set — the compilation error `BCHG008`.
A base, a router or a table declared `IComponentData` — the compilation error `BCHG011`: the world does
not hold the data, the loader does.

### By hand

```csharp
public partial struct CombatDbBootSystem : ISystem
{
    BlobchegLoad load;
    CombatDb value;
    bool created;

    public void OnCreate(ref SystemState state)
        => load = BlobchegTransport.Default.Read(CombatDb.FileName, Allocator.Persistent);

    public void OnUpdate(ref SystemState state)
    {
        // The bare road: a refusal here is obliged to switch the system off — see "A broken file is rejected once".
        if (!load.Poll()) return;

        value = new CombatDb(load.Acquire());   // the constructor registers the buffer — Resident works from here
        created = true;
        state.Enabled = false;
    }

    public void OnDestroy(ref SystemState state)
    {
        if (created) value.Dispose();
        else load.Dispose();
    }
}
```

The reading is asynchronous by construction: on Android StreamingAssets lies inside an archive, and a
blocking wait there either stalls the frame or hangs the game. `Poll()` is a method and not a property:
without a call the reading machine will not move.

`Complete()` is a blocking wait, it is for tests and editor tooling, not for the game thread.

### In the editor the base is re-read

Editing a node rebuilds the file of the base, while a live world holds the copy taken at loading — without
a re-read it would be showing yesterday's numbers until a restart. That is why in the editor (and only
there) the loading works differently:

- the base is loaded in the **editor** world too, not only in the game one: the entities of subscenes are
  always there, and without the base any pass of the patch runs into "the domain is not loaded";
- after the loading the boot system does not switch itself off, it watches the number of its file in
  `BlobchegFileVersions`;
- the file was rewritten — it re-reads it, swaps its held blob for the new one and runs
  `BlobchegSweep.Run`, which moves the slots of the entities from the previous buffer onto the new one;
- "the domain is not loaded" does not throw on the live road in the editor: the slot stays an offset and
  will reach its address with the very first pass after the base is loaded. In the player it is still an
  error.

The codegen boot system does all of this by itself. A handwritten one has to be told the same thing by
hand — to live in the editor world
(`[WorldSystemFilter(WorldSystemFilterFlags.Default | WorldSystemFilterFlags.Editor)]`), not to switch
itself off in the editor and to add the re-read:

```csharp
#if UNITY_EDITOR
    int seen;   // the number of the file this system has already read

    void Reraise(ref SystemState state)
    {
        if (!BlobchegFileVersions.Changed(CombatDb.FileName, ref seen)) return;

        var reload = BlobchegTransport.Default.Read(CombatDb.FileName, Allocator.Persistent);
        reload.Complete();

        // The new buffer goes onto the register first: the previous one leaves into the retired
        // generations, and only through them will the slots with the old addresses reach the new ones.
        var fresh = new CombatDb(reload.Acquire());

        state.EntityManager.CompleteAllTrackedJobs();   // the jobs have finished reading the previous buffer
        value.Dispose();                                // and only now may it be freed
        value = fresh;

        BlobchegSweep.Run(state.EntityManager);
    }
#endif
```

The order here is not a matter of taste: freeing the previous buffer before the new one went onto the
register means leaving the slots looking into freed memory.

### A broken file is rejected once

The file did not read or did not pass the check on loading — the refusal travels upwards exactly once,
and the reading ends there. In the player the boot system switches off: there is nobody there to fix the
file. In the editor it waits for a rebuild — that one will rewrite the file, and the loading will go
again, without a domain reload.

### A transient refusal is a warning, not an error

Two refusals from that list mean "not yet" in the editor and not "broken":

- **there is no file of the base** — the domain arrived with the pool earlier than the rebuild wrote its
  file;
- **truncated or appended to** — the reader learns the length before the body, and between those two
  reads the rebuild managed to substitute the file.

Both throw a `BlobchegTransientException` (a descendant of `InvalidOperationException`), and the codegen
boot system in the editor does not raise an exception because of them: a warning goes into the console
saying in plain text "this is a notification and not a problem", and the loading repeats by itself when
the rebuild rewrites the file. One warning per streak, not per frame.

In the player the same refusal is terminal: there is nobody there to rewrite the file, and the system
switches off, as on any other error. A handwritten boot system will have to set that rule up itself — to
catch `BlobchegTransientException` apart from the others and not to count it as a breakage.

Repeating the loading every frame is not allowed: the file does not change within a frame, while the log
grows into gigabytes over minutes, and the real cause can no longer be found in it. A handwritten boot
system will have to set that rule up itself — otherwise the very first stale `.bcheg` (assembled by a
previous version of the package, say) turns the world into an endless stream of one and the same
exception.

The ownership of the buffer leaves the reading at the moment of `Acquire()`. The constructor of the base
rejected the file — whoever took the buffer is obliged to free it:

```csharp
var buffer = load.Acquire();
CombatDb db;
try { db = new CombatDb(buffer); }
catch { buffer.Dispose(); throw; }
```

### The transport

By default it is the `StreamingAssets/Blobcheg` of this project. It is replaced whole:

```csharp
BlobchegTransport.Default = new BlobchegFileTransport(myDirectory);
// or an implementation of IBlobchegTransport of your own
```

---

## The router and BlobchegId

An offset is the direct road: if you know the record at bake time, keep the offset. A router is needed
when all you have is the name of a node: one `uint` instead of a bunch of offsets in every base.

### The declaration

```csharp
[BlobchegRouter]
public partial struct GameRouter { }                        // the body is written by the generator

[Blobcheg(typeof(IHotPathCombatData), "combatData")]        // the second argument is the name of the member in the row
public partial struct CombatDb { }

[Blobcheg(typeof(IProgressionData), "progression")]
public partial struct ProgressionDb { }
```

The name of the member IS the joining of the router. Not given — the base lives on its own.

The rules:

- the router is not named → the only router **in the assembly of this base** is taken; if there are zero
  or several of them it is a compilation error, removed with `Router = typeof(...)` in the attribute;
- **a router and its bases are obliged to lie in one assembly** — the generator of the router sees only
  its own compilation;
- a domain belongs to at most one router;
- there are no more than 64 bases in a router.

### A reference and a read

```csharp
public BlobchegIdRef<GameRouter> gun;      // the field in authoring
...
AddComponent(entity, new GunRef { id = a.gun.Id });   // in the component it is a uint
```

```csharp
var row = router.Get(id);                              // an unknown id throws
ref readonly var hot = ref combatDb.Read<GunData>(row.combatData);
if (row.HasProgression) { ... }

uint offset = router.GetCombatData(id);                // throws both on the id and on a missing record
if (router.TryGetCombatData(id, out offset)) { ... }   // never throws
```

A router is reached through `GameRouter.Resident`, exactly like a base outside a router — and its
member bases through the typed view properties on it (`router.CombatData`).

### How `BlobchegId` is arranged

One `uint`: the high byte is the tag of the router, the low three are the number of the row.

```csharp
id.Tag       // the tag of the router; zero means the id is not assigned
id.Index     // the number of the row, 0 .. 16 777 215
id.IsValid   // the tag is not zero
router.IdAt(i)   // the id of a row by its number — that is how a router is walked whole
router.Count     // how many rows there are
```

By the tag an id of another router is caught, and `default(BlobchegId)` is "not assigned" and not the
first node of the router. The price is a ceiling of 16 777 216 nodes per router.

### The stability of an id

An id is the position of a row and not a hash. Editing the values does not move it. Neither do additions
and deletions: an id handed out once lies on the carrier of the node and is inherited by the next
rebuild, a new node sits down at the tail, a deleted one leaves an empty row behind it. Only the
[compaction](#compaction) removes the holes.

The row is a field of the id carrier (`row` in the node `.asset`), so it travels in git and a fresh
checkout hands out the same ids with nothing in `Library`. The carrier also names its `owner` — the GUID
the row was handed to — so a duplicated asset, which copies the carrier, sits down at the tail as a
newcomer and the original keeps its row. Two branches that each added a node onto the same row are
settled by GUID: the lower one keeps it, the other one moves to the tail and the log says `moved`. Nothing remembers the rows past the last held one: when
the node at the very tail is deleted, the next newcomer takes its row.

A node learns its id **before the write**, so it can put it right into the record in one pass:

```csharp
public override void Write(ref BlobchegNodeWriter w)
    => w.Add(new GunData { id = w.Id, twin = w.IdOf(twinNode) });
```

### A declared index

The number of a row can be declared instead of received. That is needed where an id travels outside — into
a save, over the wire, into a table kept outside Unity: such a number is obliged to depend neither on the
journal of the carriers nor on the compaction.

```csharp
[BlobchegRouter(FixedIndex = true)]
public partial struct GameRouter { }

public sealed class BuildingSo : BlobchegNodeSo, IBlobchegIndexed
{
    [SerializeField] uint index;
    public uint Index => index;
}
```

Where the node takes the number from is its own business: a serialised field, a `const`, an `enum`, a row
of a table. The rules of such a router:

- **every** node of it is obliged to implement `IBlobchegIndexed`; one that does not makes the rebuild
  throw, because there is nowhere for it to take the number from;
- two identical numbers are a refusal as well: a row belongs to one node;
- the `BlobchegIdSo` carrier keeps being written and stays what `BlobchegIdRef<TRouter>` reads at bake
  time, but it stops being the source of truth. Delete every carrier, rebuild — the ids come back the
  same;
- the compaction does not touch the numbers of such a router: it did not hand them out. The offsets it
  squeezes as usual;
- the numbers are sparse at the consumer's will (buildings 0…999, weapons 1000…1999). A hole between the
  families costs an empty row in the router file — 5 bytes with eight bases, 5 KB per thousand skipped
  numbers. The ceiling of rows is the same, 16 777 215;
- a node belonging to two deterministic routers occupies one and the same row in both: it has one number.
  Its number in an ordinary router is still handed out as before.

The flag switched on for a router that has already handed out numbers **moves** them: a declaration is
stronger than a journal. The rebuild writes a line into the log for every node that moved (was → became)
and counts them in the `MovedIds` of the report. A moved id is a different node in a baked subscene and
in someone else's save, so the numbers are taken from the current manifest of the router
(`BlobchegManifests.Of("<Router>")`, where the nodes lie in the order of their ids) and exactly those
are declared.

### LayoutHash

A file assembled for a different set of bases will not load; the error on the rebuild will say which base
the router does not see.

---

## The name hash: an address that outlives a rebuild

An offset and a `BlobchegId` are stable within one build of the base and no further:
[the compaction](#compaction) hands out both the addresses and the numbers of the rows anew, and a record
that grew pushes its neighbour into the tail. A player's save lives longer, so it needs an address of a
different kind — the hash of the node's name. It addresses nothing by itself: it is unfolded into a
`BlobchegId` by a table that is rebuilt together with everything else.

The key is the string `"{Router}:{Name}"` folded into a `ulong`. There is no domain in the key: the hash
leads to a row of the router, and there is one row per node, no matter how many domains it writes into.
The router is there for the same reason the tag lives in a `BlobchegId`: without it identical names in
two routers would give one hash for two different rows. From that it also follows that bases outside a
router have no hashes — there is nothing to unfold into.

### The declaration

```csharp
[BlobchegHashes(typeof(GameRouter), AutoLoad = true)]
public partial struct GameHashes { }   // the body and the boot system are written by the generator
```

A router, its bases and its table are obliged to lie in one assembly — the generator sees only its own
compilation. The file lands next to the router and is called `{Router}Hashes.bcheg`.

### The hash in a record

The hash is a pure function of the name, so a node knows its hash before the write and can put it into
the record the way it puts an id:

```csharp
public override void Write(ref BlobchegNodeWriter w)
    => w.Add(new GunData
    {
        hash = this.HashIn<GameRouter>(),
        twin = twinNode.HashIn<GameRouter>(),
    });
```

`HashIn` lives in `Blobcheg.Hashes.Authoring` and not in `BlobchegNodeWriter`: the main road of the
package knows nothing about hashes, and a project that does not need saves does not pay for them.

### Saving and loading

```csharp
save.weapon = hashes.HashOf(weapon.id);              // BlobchegId  -> ulong
save.armor  = hashes.HashOfCombatData(armor.offset); // uint offset -> ulong, a method per base

if (!hashes.TryGetId(save.weapon, out var id))
    return;                                          // there is no node with that name in the project any more

ref readonly var gun = ref combat.Read<GunData>(router.Get(id).combatData);
```

The table is computed on the rebuild and baked into the file ready: at runtime it is not built but read,
so the road `hash → id` is cheap and fits the hot path too. `HashOf*` by an offset is the road of a save,
it is not hot.

### What breaks a hash

Renaming the asset does not break it: the hash is computed from `BlobchegName` and not from the name of
the file. The compaction does not break it: the table is rebuilt together with the addresses. Deleting a
node makes the hash stop being found, and that is an answer of `TryGetId` and not an error.

Exactly one thing breaks it: editing `BlobchegName` itself. A node has no list of its previous names on
purpose — the name is declared eternal, like a GUID.

Two identical names in one router, and two different names that met on one `ulong`, fail the rebuild with
the paths of both assets in the text: both mean two things with one address in a save.

---

## BlobchegReference: a pointer instead of an offset

An ordinary read costs the registry lookup of the base (`Resident`) and an addition. If that is not enough, a reference can
be held so that by the moment of the read it already holds the address of the record. That is exactly
what Unity does with its own `BlobAssetReference`, and the patch is built into the very place where those
are patched.

`BlobchegReference<T>` is eight bytes in which two things live in turn: before the patch an offset, after
the patch an address. Zero means "not assigned" without a sentinel: the offsets start at 32 and are
aligned to 16.

```csharp
public struct WeaponRef : IComponentData
{
    public BlobchegReference<GunData> gun;
}

// in the baker
AddComponent(entity, new WeaponRef { gun = a.gun.ToReference() });

// in a job — without a base and without an addition
ref readonly var gun = ref weapon.gun.Value;
```

### What has to be switched on

The `Blobcheg.Entities.Patch` assembly is switched on by the `BLOBCHEG_ENTITIES_PATCH` define and requires
a **forked** `com.unity.entities`: the extension point `BlobchegPatchHook` and its calls were added to it.
The logic of the patch did not move into the fork — there are only the calls there, four lines.

The fork does not have to be assembled by hand: `tools~/entities-patch/` holds the `.patch` for a
particular version of the package, `vendor.ps1` (vendors the clean package from the cache and applies the
patch), `regen.sh` (rebuilds the patch from the current fork) and a README with the order of a version
bump.

### When it fires

- the loading of a subscene section;
- the reverse pass before writing a world — what travels into the file is obliged to be an offset and not
  a process address;
- the live road of the editor: an open subscene, where entities arrive with a change set past the
  serialisation.

### The rules

**The order of loading is the consumer's concern.** The patch does not wait for a base. Entities that
arrived earlier than their domain are an explicit error with the name of the component and of the domain
in the text, and not zeroes in the fields. Gate the subscene loading on base readiness
(`BlobchegBases.Has(CombatDb.DomainKey)`).

The patch is idempotent and outlives a rebuild under a live editor: the previous addresses are moved onto
the new buffer.

In the editor and in a development build the patch additionally checks against the debug contour that a
record of the declared type begins at the address it got.

### What the patch cannot do

| The case | Why | What to do |
|---|---|---|
| a slot in an `ISharedComponentData` | a shared component lies as one value per index, a chunk does not carry it | move it into an ordinary component |
| a record from `AddBytes` (`BlobchegRawReference`) | it has no type, and so no domain either | read through "an offset plus `Read`" |
| the field is declared as `BlobchegReferenceData` | that is the innards of a slot, the domain cannot be derived from it | declare a `BlobchegReference<T>` |

Such a type is simply not registered, nothing is poured into the log; the reason stays a line in
`BlobchegPatchTableBuilder.Diagnostics`. The developer learns about an unpatched slot on the read:
`Value` throws "is not patched" with the name of the record type instead of handing out garbage.

---

## The rebuild

There is no Save button on purpose: a blob assembled an hour ago looks working next to fresh assets and
lies.

### When it happens

Nothing happens on an import. The bases are brought up to date by whoever reads them, and "are they
current" is answered by a hash of what they were built from, not by an event:

| The reader | What it does |
|---|---|
| a base is opened — PlayMode, a test, a live world | checks the key, rebuilds what diverged |
| an address or an id is asked for — the bake, a picker | the same check |
| `Tools/Blobcheg/Inspector` gets the focus | the same check |
| the pre-build (`callbackOrder = -10000`) | a full rebuild, the debug contour off for a release player |
| `Tools → Blobcheg → Rebuild bases` | a full rebuild at a human's demand |
| `Tools → Blobcheg → Why would it rebuild` | the reason and the nodes, into the log, changing nothing |
| `Tools → Blobcheg → Work as in a player` | the player path in the editor: everything anew without the contour |

### The key

```
key = H( the version of the index,
         the debug contour,
         the hash of every assembly of Library/ScriptAssemblies,
         for every node by guid: the hash of its asset + the hash of every asset it reads )
```

What a node reads is `AssetDatabase.GetDependencies(path, recursive)`, and that list lies in the index
next to the key: walking it costs 2.4 s on 117 nodes, and a check that costs that is a check nobody
runs. The list of a node is refreshed by the rebuild that wrote it. Scripts are left out — a code edit
is a domain reload, and the hash of the assemblies has it.

The check costs 55-80 ms on 117 nodes, and 370 ms the first time in a fresh domain — there it also
reads the index and hashes the assemblies. Between two imports it costs nothing at all: only an import
voids the memo of the last answer. Typing into a field, dragging an object in a scene or any other edit
in memory costs the package 0 ms: the bases follow Ctrl+S, not the screen (measured: 1-3 ms a frame
while a scene object moves and a node field changes every frame, against 72-1500 ms before).

### What the check decides

| What moved | What is rebuilt |
|---|---|
| the hash of a node or of something it reads | that node |
| the set of nodes, the code, the contour, the version of the index | everything |
| a file of the output is not on disk | everything |

### Every case a dependency can change in

| The case | What catches it | The test |
|---|---|---|
| a node is edited in the inspector and not saved | nothing until Ctrl+S; a rebuild neither writes nor saves it | `An_unsaved_node_rebuilds_nothing_and_stays_unsaved` |
| a node asset is created while the search index still lags | the `.asset` paths the import wrote down | `A_new_node_gets_its_record` |
| an asset the node reads is edited and not saved | nothing until Ctrl+S, then its dependency hash | `An_unsaved_edit_of_a_dependency_waits_for_the_save` |
| an address moves while a node behind it is unsaved | that node is not reimported (a reimport would save it); its own save declares the new numbers | `A_moved_address_saves_no_unsaved_node_and_catches_up_on_its_save` |
| an asset the node reads is saved, reimported, checked out or merged | its dependency hash | `Editing_a_foreign_asset_reaches_the_record`, `A_reader_of_the_base_picks_up_the_foreign_edit` |
| an asset the node reads is deleted | the same hash, gone to zero | `Deleting_a_foreign_asset_reaches_the_record` |
| a node is deleted | its hash goes to zero and the set of guids moves | `A_deleted_node_takes_its_record_away` |
| a node is renamed or moved | the path inside the hash of the node, and no address moves | `A_rename_breaks_no_reader_and_moves_no_address` |
| a node reads through another asset, two steps deep | the walk of dependencies is recursive | `A_dependency_of_a_dependency_reaches_the_record` |
| a node reads a path no reference points at | `CollectExtraDependencies` | `A_path_the_node_reads_past_a_reference_reaches_the_record` |
| the files are wiped past the assets | the list of the output files | `A_wiped_file_is_assembled_again` |
| a domain reload: nothing is remembered any more | the index outlives the domain | `A_foreign_edit_lands_when_nothing_is_remembered` |
| nobody touched anything | the key agrees and there is no work | `Nothing_changed_means_no_work_at_all` |

The fixtures of the whole pipeline — dependencies, the rebuild, routers, fixed indices, hashes, the boot
system — run twice, as `Editor` and as `AsInPlayer`: the second pass builds the files without the debug
contour, the way a release player gets them, and every case above is obliged to hold there too.
`BlobchegModeTests` adds what only the switch can break: the player mode moves no address and no id, and
a reader flipping the mode rebuilds by itself in both directions.

What is edited and not saved is none of the package's business: the check reads hashes of what lies
on disk, and the reimport of moved numbers skips an unsaved node (an import of a dirty asset writes it).
One hole stays: a rebuild that wrote carriers or names (a new node, a rename) has to save, and Unity
has no save of a single file — `SaveAssetIfDirty` writes every dirty asset, measured. The price of the
whole rule is a live PlayMode tweak of a node: it reaches the world on Ctrl+S.

Four cases have no test and are closed by construction: an edit of the code (`Write`, the ENV of the
rift compiler, a record struct, the codegen, the package itself) and the appearance of a domain or a
router both move the hash of the assemblies; a branch switch is a mass import, that is the hashes of
the nodes; an asset in `Packages/` instead of `Assets/` is what `GetDependencies` returns anyway.
The first of them is seen in the log of every editor start after a recompilation: `full rebuild (the
code changed, a base is being read)`.

### The API

```csharp
BlobchegFreshness.Ensure(trigger);   // the entry point of a reader: the check, and a rebuild if it diverged
BlobchegFreshness.Explain();         // why a rebuild would run right now, node by node
BlobchegFreshness.Key;               // the build key, the one RegisterCustomDependency is given
BlobchegBuild.RebuildAll(trigger);   // the same dirty set, but the rebuild runs in any case
BlobchegBuild.RebuildFull(trigger);  // everything anew: Write is called on every node
```

The `trigger` is what asked for the rebuild — it goes into the log line and answers the second question
a human asks of a stall: it was blobcheg, and who called it.

All of them return a `BlobchegBuildReport` — domains, routers, records, how many files, manifests and
carriers were rewritten.

### The layout

The layout is a pure function of the assets: the records of a base lie in the order of (record type,
node guid), the rows of a router in the order of the ids, and the ids lie on the carriers. There is no
journal of handed-out addresses, so every machine computes the same numbers and there is nothing to
share through git beyond the assets themselves. A record that
changed size moves everyone behind it — and everything baked against those numbers is rebaked, which is
what the dependency below is for.

### The dependency of a bake on the numbers

The addresses left the assets, so a record that moves changes no byte a baker can depend on. The node
asset is made to depend on them instead: `BlobchegNodeDependency`, an `AssetPostprocessor`, declares
`DependsOnCustomDependency(BlobchegDependencies.NameOf(guid))` on every node asset the index knows, and
the rebuild registers that name with a hash of what a bake of the node can read — its offsets, record
types and ids, never the record bytes. When the hash moves, the rebuild reimports that node: its file is
the same, its artifact is new, and every import that loaded it through `DependsOn` — a subscene bake
included — is redone by Unity itself. Nothing is asked of a baker beyond the `DependsOn(ref.Asset)` it
already makes, and nothing of Entities.

A value edit moves no number and rebakes nothing that did not read the edited node. A node the index
does not know yet (a new asset, a fresh `Library`) is reimported once after its first rebuild, and the
hashes of the index are taken after that reimport, so it costs no second rebuild.

### The output

| The path | What |
|---|---|
| `Library/Blobcheg/{Domain}.bcheg` | the file of a base |
| `Library/Blobcheg/{Router}.bcheg` | the file of a router |
| `Library/Blobcheg/{Router}Hashes.bcheg` | the table of name hashes |
| `Library/BlobchegEditor/manifests.json` | the manifests of every file at once: the name, the number of records, the hash, the composition, the build time |
| `Library/BlobchegEditor/stamps.json` | the address, the record type and the revision of every record, the id of every node |
| `Library/BlobchegEditor/index.json` | what the bases were built from: the key, the nodes, their dependencies |
| the sub-assets on the nodes | `BlobchegRefSo` and `BlobchegIdSo` — the anchors of identity, a domain or a router name and nothing else |

Everything derived lies in `Library` and never reaches git; the pre-build carries the folder of the
bases into the `StreamingAssets` of the player. The manifests, the stamps and the index stand in a
folder of their own because that copy takes the folder of the bases as it is.

### What it says about itself

`Tools → Blobcheg → Say what it does` (on by default, remembered per machine) is the package's only
channel to the log. It exists for one question asked of the editor log after a stall: was that
half-second mine, or somebody else's.

One line per unit of work the package started on its own, with its price in milliseconds:

| The line | When |
|---|---|
| `rebuild (2 node(s) changed, a base is being read) — 340 ms — domains 12, …` | every rebuild, with its trigger and its report |
| `candidates for a reference field — 900 ms` | a picker walks the project |
| `a patch pass over the world — 40 ms — 8 types, 1200 entities` | after every apply of a change set and every load of a base |

Under the line of a rebuild goes its breakdown by section, from the expensive to the cheap — the
freshness check, the walk over the project, `node.Write`, the flush, the batch of carriers. A unit of
work cheaper than a millisecond with nothing to show says nothing.

Switching the channel off silences the rebuild report too, and that is deliberate: there is one channel,
and a package that keeps talking after being told to be quiet is worth nothing.

### When the rebuild refuses to work

- **a node fell out of the walk while its file is on disk** — that is what a just-renamed asset looks
  like; repeat it once the editor has finished importing;
- **the rebuild entered itself** — a node called `RebuildAll` from its own `Write`;
- **an asset is declared a node but does not load** — it will not be skipped silently.

---

## Checks and errors

An error is thrown, not returned. No file, a broken header, the integrity did not match, two records of
one node into a domain, an access to an offset before `Flush`, an empty or foreign `BlobchegRef<T>` /
`BlobchegIdRef<T>`, an unknown id, a missing record in a base — an exception.

The only exception from the rule is `TryGet*` and `Has*` of a router: there a missing record IS the
normal answer, and they never throw.

Two refusals of the loading differ by type: "there is no file" and "truncated or appended to" throw a
`BlobchegTransientException`. Their cause is in time and not in the bytes, and in the editor that is a
notification and not a breakage — see
[a transient refusal](#a-transient-refusal-is-a-warning-not-an-error).

### What is checked and when

| When | What |
|---|---|
| always, once at loading | the magic, the format version, whether it is a base or a router, the length of the file, the integrity (`ContentHash`), the identity of the file (the hash of the domain name) |
| always, on every `router.Get` | the tag of the id and the range of the row — two comparisons |
| `ENABLE_UNITY_COLLECTIONS_CHECKS` | the alignment of the offset, the bounds of the buffer, **the type of the record** in `Read<T>` |
| `ENABLE_UNITY_COLLECTIONS_CHECKS` | that the `BlobchegReference<T>` slot holds an address and not an offset left in it |
| the rebuild, a router with `FixedIndex` | the node implements `IBlobchegIndexed`, the number is within the ceiling and is not taken by another node |

### The debug contour

The check of the record type leans on a section in the file that holds the types and the names of the
nodes. In the editor and in a development build it is there, in a release player it is not — `Read<T>` is
a pure `AsRef` there.

`Describe` works off the same contour:

```csharp
if (db.HasDebug)
    db.Describe(offset, out var typeName, out var nodeName);

router.Describe(id);   // the name of the node
```

### The diagnostics of the codegen

| The code | About what |
|---|---|
| `BCHG001` | a base is marked `[Blobcheg]` but is not `partial` |
| `BCHG002` | a base is nested in another type |
| `BCHG003` | something that is not an interface was passed to `[Blobcheg]` |
| `BCHG004` | the name of a router member is given, but the router is not chosen (zero, several, or in another assembly) |
| `BCHG005` | the router is not `partial` or is nested |
| `BCHG006` | the router is assembled out of contradictory bases: a domain or a member name twice |
| `BCHG007` | there are more than 64 bases in the router |
| `BCHG008` | a base sets `AutoLoad = true` while the assembly does not reference `Blobcheg.Entities` |
| `BCHG009` | the hash table is not `partial` or is nested |
| `BCHG010` | something that is not a router of this assembly was passed to `[BlobchegHashes]` |
| `BCHG011` | a base, a router or a hash table is declared `IComponentData` — the world does not hold the data |

---

## API reference

### A base (written by the generator)

```csharp
const string DomainName;                  // the name of the marker interface
const ulong DomainKey;                    // the registry key — fnv1a-64 of the domain name
static string FileName { get; }           // "{Domain}.bcheg"
static Db Resident { get; }               // a base OUTSIDE a router only; router members are reached through the router
Db(BlobchegBuffer buffer);                // takes the ownership of the buffer, validates the file
bool IsCreated { get; }
int Length { get; }
bool HasDebug { get; }
ref readonly T Read<T>(uint offset) where T : unmanaged, IDomain;
void Describe(uint offset, out string typeName, out string nodeName);
void Dispose();
```

### A router (written by the generator)

```csharp
const string RouterName;
const ulong LayoutHash;
const ulong RouterKey;                    // the registry key — fnv1a-64 of the router name
const byte RouterTag;                     // mirrors BlobchegNaming.TagOf
const int DomainCount;
static string FileName { get; }
static Router Resident { get; }           // the loaded router — the single door to it and its bases
Router(BlobchegBuffer buffer);
int Count { get; }                        // rows, which are also nodes
byte Tag { get; }
{Db} {Member} { get; }                    // the typed view of each member base, one property per base
BlobchegId IdAt(uint index);
RouterRow Get(BlobchegId id);             // an unknown id throws
bool TryGet(BlobchegId id, out RouterRow row);
uint Get{Member}(BlobchegId id);          // one per base
bool TryGet{Member}(BlobchegId id, out uint offset);
bool Has{Member}(BlobchegId id);
string Describe(BlobchegId id);           // the name of the node; needs the debug contour
void Dispose();
```

Plus an `enum {Router}Db` — the flags of the bases — and a `struct {Router}Row` with `Mask`,
`Has{Member}` and `{member}`.

### A hash table (written by the generator)

```csharp
const string RouterName;
const string FileIdentity;                // "{Router}Hashes"
const ulong LayoutHash;                   // the same as the router's
const ulong HashesKey;                    // the registry key — fnv1a-64 of the file identity
const byte RouterTag;                     // mirrors BlobchegNaming.TagOf
const int DomainCount;
static string FileName { get; }
static Hashes Resident { get; }           // the loaded table
Hashes(BlobchegBuffer buffer);
int Count { get; }                        // the rows of the router, holes included
byte Tag { get; }
BlobchegId GetId(ulong hash);             // an unknown hash throws
bool TryGetId(ulong hash, out BlobchegId id);
ulong HashOf(BlobchegId id);              // a hole from a deleted node is 0
ulong HashOf{Member}(uint offset);        // one per base; a missing record throws
bool TryHashOf{Member}(uint offset, out ulong hash);
void Dispose();
```

The key is computed without the table: `BlobchegHashKey.Of<TRouter>(name)` at runtime,
`node.HashIn<TRouter>()` at bake time.

### Reading a file

```csharp
BlobchegLoad load = BlobchegTransport.Default.Read(fileName, Allocator.Persistent);
bool ready = load.Poll();          // moves the machine; without a call it will not go
load.Complete();                   // a blocking wait — tests and tooling
BlobchegBuffer buffer = load.Acquire();   // hands over the ownership; before it is ready — an error
load.Dispose();
```

`BlobchegTransientException` is a refusal with an expiry date: the file is not there yet, or the reading
caught it in the middle of a rewrite. A descendant of `InvalidOperationException`, caught apart from it.

### The rebuild (Editor)

```csharp
void BlobchegFreshness.Ensure(string trigger);   // the check, and a rebuild if the key diverged
string BlobchegFreshness.Explain();              // why a rebuild would run right now
Hash128 BlobchegFreshness.Key;                   // the build key
void BlobchegFreshness.Invalidate();             // void the memo of the last check
BlobchegBuildReport BlobchegBuild.RebuildAll();
BlobchegBuildReport BlobchegBuild.RebuildFull();
bool BlobchegBuild.AsInPlayer;                   // the player path in the editor, remembered per machine
IEnumerable<BlobchegRefSo> BlobchegBuild.RefsOf(BlobchegNodeSo node);
IEnumerable<BlobchegIdSo> BlobchegBuild.IdsOf(BlobchegNodeSo node);
List<BlobchegNodeSo> BlobchegBuild.FindNodes();
string BlobchegDependencies.NameOf(string guid);  // the custom dependency of a node asset
```

---

## The assemblies of the package

| asmdef | What is inside | Platforms |
|---|---|---|
| `Blobcheg.Core` | the file format, the transport, the writer, the hashes | all |
| `Blobcheg.Runtime` | `[Blobcheg]`, `[BlobchegRouter]`, `BlobchegBlob`, `BlobchegRouterBlob`, `BlobchegId`, the reference fields, the generator | all |
| `Blobcheg.Entities` | `BlobchegBootGroup`, `BlobchegSweep` | all, only with Entities |
| `Blobcheg.Entities.Patch` | the `BlobchegReference<T>` patch on import | all, with the Entities fork and the `BLOBCHEG_ENTITIES_PATCH` define |
| `Blobcheg.Hashes` | `[BlobchegHashes]`, `BlobchegHashKey`, the format and the resident table | all |
| `Blobcheg.Authoring` | the node contract: `BlobchegNodeSo`, `BlobchegNodeWriter`, `BlobchegBuilder`, the registries of domains and routers | all |
| `Blobcheg.Authoring.Editor` | the rebuild (`BlobchegBuild`), the hooks, the carriers, the cache, the menu, the inspector window, the field pickers | Editor |
| `Blobcheg.Hashes.Authoring` | `HashIn` (`BlobchegNodeHash`) | all |
| `Blobcheg.Hashes.Authoring.Editor` | the writer of the table, the post-pass of the rebuild | Editor |

`Blobcheg.Entities` and `Blobcheg.Entities.Patch` switch themselves off through `defineConstraints` if
the Entities package or the define is missing.

---

## Developing the package

### The generator

The source is `Authoring/CodeGen~/BlobchegGenerator.cs`, the assembled `Blobcheg.CodeGen.dll` lies in `Runtime/` with the
labels `RoslynAnalyzer` and `RunOnlyOnAssembliesWithReference`, so it is applied to the assemblies that
reference `Blobcheg.Runtime`.

To rebuild: `dotnet build -c Release` of a `Blobcheg.CodeGen.csproj` in `Authoring/CodeGen~/`, then copy
the DLL into `Runtime/`. The `.csproj` is not in the repository — the root `.gitignore` drops `*.csproj` —
so it has to be recreated locally. Do not touch the `.meta` — it holds the labels and the GUID.

### The tests

```
unity test <project> --mode EditMode --filter Blobcheg
```

The filter is `Blobcheg` and not `Blobcheg.Tests`: the tests of the boot group, the patch and the hashes
lie in separate assemblies (`Blobcheg.Entities.Tests`, `Blobcheg.EntitiesPatch.Tests`,
`Blobcheg.Hashes.Tests`), and they switch themselves off without Entities and without the define. Every
test assembly has `defineConstraints` `UNITY_INCLUDE_TESTS` + `BLOBCHEG_TESTS`: the project needs the
`BLOBCHEG_TESTS` scripting define and `com.xacce.blobcheg` in the `testables` of `Packages/manifest.json`.

In the RTC project the four test assemblies under `Packages/blobcheg/Tests/` (including the tests of the
`BlobchegReference` patch) are additionally gated by `defineConstraints: BLOBCHEG_TESTS`. To run them, add
`BLOBCHEG_TESTS` to `scriptingDefineSymbols` and **remove it after the run**: otherwise the package's test
domains (`IPatch*`, `ITest*`, `Test*Router*`) travel into the built bases under
`Assets/StreamingAssets/Blobcheg` and into the game build.

### The destructive set

`Samples~/AdvancedTests` is a separate set that breaks the package from an asset down to a byte in a
file: the boundaries of an address, corruption of a file, foreign ids and offsets, reentrancy of a
rebuild, volume, the human factor.

It is **not** part of the delivery: `Samples~` is invisible to Unity, so it does not cost a consumer a
second of compilation until they import it. It is installed through Package Manager → Samples → Import or
run from the CLI:

```
./tools~/run-advanced-tests.ps1 -Project <the path to the Unity project>
```

The details are in `Samples~/AdvancedTests/README.md`.
