# The com.unity.entities fork

The Blobcheg reference patch lives inside scene deserialisation — in the same place where Unity patches
its own `BlobAssetReference`. There is nothing to reach in there from the outside: `SerializeUtility`
offers neither an event nor a virtual method. That is why the package is vendored into the project's
`Packages/` and edited.

The edits are minimal and are deliberately kept that way: only the extension point and its calls went
into the fork, all the logic of the patch lives in `Blobcheg.Entities.Patch`. The smaller the diff, the
cheaper a version bump.

| file | what is in it |
|---|---|
| `Unity.Entities/Serialization/BlobchegPatchHook.cs` | new: the holder of three hooks |
| `Unity.Entities/Serialization/SerializeUtility.cs` | the chunk walk `PatchBlobchegRefsInChunk` and three calls; plus a fix for an upstream hole on an empty world |
| `Unity.Scenes/LiveConversionPatcher.cs` | the call after a change set is applied |

## Vendoring from scratch

```powershell
./vendor.ps1 -Project <path to the Unity project>
```

The script takes the clean package from the cache, clears read-only and applies the `.patch`.

**Important about the cache.** Unity throws a package out of the project's `Library/PackageCache` as
soon as it becomes embedded. That is, vendoring has to happen BEFORE the `Packages/com.unity.entities`
folder appears. If it is already gone from the global cache too — remove the embedded copy, let Unity
resolve the package from the registry, and run again.

After vendoring, the project's Player Settings must carry the `BLOBCHEG_ENTITIES_PATCH` define — it is
what switches the `Blobcheg.Entities.Patch` assembly on. Without it the patch does not install, and
everything else in the package keeps working the old way.

## Applying the patch by hand

From the root of the project, once the package is vendored:

```
git apply --3way Packages/Blobcheg/tools~/entities-patch/com.unity.entities@1.4.8.patch
git apply --check <the same path>     # check without applying
```

## Bumping the entities version

1. Rebuild the patch from the current fork if it drifted apart from the file: `./regen.sh <project>`.
2. Pour the new version of the package over the folder.
3. `git apply --3way` with the old patch. `--3way` sorts out line shifts by itself; real conflicts it
   leaves as markers in the files.
4. Sort out what is left and rebuild the patch for the new version:
   `./regen.sh <project> <the commit with the clean new version>`.

There is almost nothing to conflict over: `PatchBlobchegRefsInChunk` is a standalone method rather than
a wedge into someone else's, and there are only four calls, one line each.

## Rebuilding the patch

```bash
./regen.sh <path to the Unity project> [baseline-commit]
```

The baseline is the commit that holds the **clean** package. Without the second argument it is taken
from the header of the existing patch.

Bash and not PowerShell on purpose: the diff has to be written byte for byte, and PowerShell re-encodes
the stream.
