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
the record in the resident buffer, and `.Value` reads the memory directly, without a singleton and
without an addition. Without the patch the ordinary road stays, "an offset plus `Read<T>`" — the
singleton of the base and one addition.

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
bases in one `Write` — a record into each declared domain (the node class lives in an Editor-only
assembly, because `BlobchegNodeSo` lies in `Blobcheg.Authoring`):

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
puts a bare `uint` into the component, and the read is the singleton of the base plus one addition:

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
| The automatic loading of bases in ECS | reference the `Blobcheg.Entities` assembly | the codegen will emit a boot system |
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

### 2. The node — in an Editor-only assembly

`BlobchegNodeSo` lives in `Blobcheg.Authoring`, and that one is Editor-only. So the node class has to lie
in an assembly with `includePlatforms: ["Editor"]`.

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

On Entities it is enough to declare the base an `IComponentData` — the system is emitted by the codegen:

```csharp
[Blobcheg(typeof(IHotPathCombatData))]
public partial struct CombatDb : IComponentData { }
```

Without Entities the loading is written by hand, see [Loading a base](#loading-a-base).

### 5. Reading

```csharp
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

Declare the base or the router an `IComponentData` and reference the `Blobcheg.Entities` assembly. The
generator will emit a `{Name}BootSystem` in the `BlobchegBootGroup` group.

```csharp
[Blobcheg(typeof(IHotPathCombatData), "combatData")]
public partial struct CombatDb : IComponentData { }   // CombatDbBootSystem is emitted by the codegen
```

`BlobchegBootGroup` stands at the beginning of `InitializationSystemGroup` (`OrderFirst`) and **before**
`BeginInitializationEntityCommandBufferSystem`: the systems that need the base are obliged to see it
earlier than their own entities.

A `[DisableAutoCreation]` on the base itself travels onto the emitted system — "the system is needed, but
who creates it is my decision". Nobody forbids a loading system of your own: put it into the same group.

There is no reference to `Blobcheg.Entities` while `IComponentData` is declared — the compilation error
`BCHG008`.

### By hand

```csharp
public partial struct CombatDbBootSystem : ISystem
{
    BlobchegLoad load;
    bool created;

    public void OnCreate(ref SystemState state)
        => load = BlobchegTransport.Default.Read(CombatDb.FileName, Allocator.Persistent);

    public void OnUpdate(ref SystemState state)
    {
        // The bare road: a refusal here is obliged to switch the system off — see "A broken file is rejected once".
        if (!load.Poll()) return;

        state.EntityManager.CreateSingleton(new CombatDb(load.Acquire()));
        created = true;
        state.Enabled = false;
    }

    public void OnDestroy(ref SystemState state)
    {
        if (created) SystemAPI.GetSingleton<CombatDb>().Dispose();
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
- the file was rewritten — it re-reads it, puts the new blob into the singleton and runs
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
        SystemAPI.GetSingleton<CombatDb>().Dispose();   // and only now may it be freed
        SystemAPI.SetSingleton(fresh);

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

A router lives as a singleton, exactly like a base.

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
(`Assets/Blobcheg/<Router>.asset`, where the nodes lie in the order of their ids) and exactly those are
declared.

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
[BlobchegHashes(typeof(GameRouter))]
public partial struct GameHashes : IComponentData { }   // the body and the boot system are written by the generator
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

An ordinary read costs the singleton of the base and an addition. If that is not enough, a reference can
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
in the text, and not zeroes in the fields. Set a singleton of base readiness and load the subscenes after
it.

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

| The event | What it does |
|---|---|
| the import, the move or the deletion of a node | an incremental rebuild |
| entering PlayMode | an incremental rebuild; if it failed, PlayMode does not start |
| the pre-build (`callbackOrder = -10000`) | a compaction, then a double full rebuild with a demand of idempotency |
| `Tools → Blobcheg → Rebuild bases` | a full rebuild at a human's demand |
| `Tools → Blobcheg → Compact bases` | a compaction at a human's demand |

The first three events are about changed assets. Files are lost past the assets too: artifacts wiped out
with a warm Library (`git clean -X`, a fresh worktree) do not make a single node dirty, and the
automation has nothing to rebuild although there is nowhere to write. That is what the menu command is
for — it does not move the addresses and the ids, only the compaction moves those.

### The API

```csharp
BlobchegBuild.RebuildAll();          // incrementally: unchanged nodes hand out their previous bytes
BlobchegBuild.RebuildFull();         // the cache is forgotten, the project is walked, Write is called on everyone
BlobchegBuild.Compact();             // the layout from scratch: the holes disappear, the addresses and the ids are handed out anew
BlobchegBuild.RequireUpToDate(what); // rebuild twice and demand that the second pass changed nothing
```

All four return/use a `BlobchegBuildReport` — domains, routers, records, how many files, manifests and
carriers were rewritten.

### Compaction

The addresses of the records are stable between rebuilds: editing the values, the appearance and the
deletion of neighbours do not move a foreign address, so untouched subscenes are not re-baked and a
rebuilt file can be substituted in a build. The compaction is the only thing that moves every address and
every id at once. It does not happen by itself: baked subscenes and other people's saves have already
remembered them. There are exactly two places for it — the pre-build, where everything is re-baked
afterwards anyway, and the menu command, which a human calls themselves.

### The output

| The path | What |
|---|---|
| `Assets/StreamingAssets/Blobcheg/{Domain}.bcheg` | the file of a base |
| `Assets/StreamingAssets/Blobcheg/{Router}.bcheg` | the file of a router |
| `Assets/StreamingAssets/Blobcheg/{Router}Hashes.bcheg` | the table of name hashes |
| `Assets/Blobcheg/{Domain}.asset` | the manifest: the file name, the number of records, the hash, the contents, the build time |
| `Assets/Blobcheg/{Router}.asset` | the manifest of a router; the nodes are listed in the order of their ids |
| `Assets/Blobcheg/{Router}Hashes.asset` | the manifest of the table; the nodes in the order of the rows |
| the sub-assets on the nodes | `BlobchegRefSo` and `BlobchegIdSo` |

The files and the manifests are derived from the assets — they do not have to go into git. The carriers
(the sub-assets) do: they hold the journal of the addresses and the ids that were handed out.

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
| `BCHG008` | a base is declared `IComponentData` while the assembly does not reference `Blobcheg.Entities` |
| `BCHG009` | the hash table is not `partial` or is nested |
| `BCHG010` | something that is not a router of this assembly was passed to `[BlobchegHashes]` |

---

## API reference

### A base (written by the generator)

```csharp
const string DomainName;                  // the name of the marker interface
static string FileName { get; }           // "{Domain}.bcheg"
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
const int DomainCount;
static string FileName { get; }
Router(BlobchegBuffer buffer);
int Count { get; }                        // rows, which are also nodes
byte Tag { get; }
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
const int DomainCount;
static string FileName { get; }
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
BlobchegBuildReport BlobchegBuild.RebuildAll();
BlobchegBuildReport BlobchegBuild.RebuildFull();
BlobchegBuildReport BlobchegBuild.Compact();
void BlobchegBuild.RequireUpToDate(string what);
IEnumerable<BlobchegRefSo> BlobchegBuild.RefsOf(BlobchegNodeSo node);
IEnumerable<BlobchegIdSo> BlobchegBuild.IdsOf(BlobchegNodeSo node);
List<BlobchegNodeSo> BlobchegBuild.FindNodes();
void BlobchegHooks.MarkDirty();    // mark the domains dirty from tests and tooling
```

---

## The assemblies of the package

| asmdef | What is inside | Platforms |
|---|---|---|
| `Blobcheg.Core` | the file format, the transport, the writer, the hashes | all |
| `Blobcheg.Runtime` | `[Blobcheg]`, `[BlobchegRouter]`, `BlobchegBlob`, `BlobchegRouterBlob`, `BlobchegId`, the reference fields, the generator | all |
| `Blobcheg.Entities` | `BlobchegBootGroup` | all, only with Entities |
| `Blobcheg.Entities.Patch` | the `BlobchegReference<T>` patch on import | all, with the Entities fork and the `BLOBCHEG_ENTITIES_PATCH` define |
| `Blobcheg.Hashes` | `[BlobchegHashes]`, `BlobchegHashKey`, the format and the resident table | all |
| `Blobcheg.Authoring` | the nodes, the rebuild, the registries of domains and routers, the field pickers | Editor |
| `Blobcheg.Hashes.Authoring` | the writer of the table, `HashIn`, the post-pass of the rebuild | Editor |

`Blobcheg.Entities` and `Blobcheg.Entities.Patch` switch themselves off through `defineConstraints` if
the Entities package or the define is missing.

---

## Developing the package

### The generator

The source is `Authoring/CodeGen~/`, the assembled `Blobcheg.CodeGen.dll` lies in `Runtime/` with the
labels `RoslynAnalyzer` and `RunOnlyOnAssembliesWithReference`, so it is applied to the assemblies that
reference `Blobcheg.Runtime`.

To rebuild: `dotnet build -c Release` in `Authoring/CodeGen~/`, then copy the DLL into `Runtime/`. Do not
touch the `.meta` — it holds the labels and the GUID.

### The tests

```
unity test <project> --mode EditMode --filter Blobcheg
```

The filter is `Blobcheg` and not `Blobcheg.Tests`: the tests of the boot group and of the patch lie in
separate assemblies (`Blobcheg.Entities.Tests`, `Blobcheg.EntitiesPatch.Tests`), and they switch
themselves off without Entities and without the define. The package has to be in the `testables` of the
project manifest.

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
