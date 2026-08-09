<#
.SYNOPSIS
Vendors com.unity.entities into the project's Packages/ and applies the Blobcheg patch to it.

.DESCRIPTION
The Blobcheg reference patch needs an edited com.unity.entities: the extension point BlobchegPatchHook
and its calls were added to it. A package from the registry cannot be edited, so it moves into the
project's Packages/ — from that moment it is an ordinary embedded package, which Unity takes instead of
the registry one.

The script does three things and nothing more: it copies the clean package from the cache, clears
read-only off the files (from the cache they arrive read-only) and applies the .patch from this very
folder.

.EXAMPLE
./vendor.ps1 -Project C:\path\to\UnityProject

.EXAMPLE
# again, over an already vendored one — the folder is removed and assembled from scratch
./vendor.ps1 -Project C:\path\to\UnityProject -Force
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $Project,

    [string] $Package = "com.unity.entities",

    # Empty — take the only version found in the caches. Otherwise the exact version, for example 1.4.8.
    [string] $Version = "",

    [switch] $Force
)

$ErrorActionPreference = "Stop"

$patch = Join-Path $PSScriptRoot "$Package@$(if ($Version) { $Version } else { '1.4.8' }).patch"
if (-not (Test-Path $patch)) {
    throw "No patch '$patch'. A .patch for the same version of the package must lie next to the script."
}

if (-not (Test-Path (Join-Path $Project "ProjectSettings/ProjectVersion.txt"))) {
    throw "'$Project' is not a Unity project: there is no ProjectSettings/ProjectVersion.txt"
}

$target = Join-Path $Project "Packages/$Package"
if (Test-Path $target) {
    if (-not $Force) {
        throw "'$target' already exists. To vendor from scratch use the -Force switch (the folder will be removed)."
    }

    Write-Host "removing the previous '$target'"
    Remove-Item -Recurse -Force $target
}

# The clean package is looked for first in the project's own cache, then in the global Unity cache. In the
# project cache the folder is named with a hash (com.unity.entities@6ce362df365b), in the global one with a version.
$candidates = @()

$projectCache = Join-Path $Project "Library/PackageCache"
if (Test-Path $projectCache) {
    $candidates += Get-ChildItem $projectCache -Directory | Where-Object { $_.Name -like "$Package@*" }
}

$globalCache = Join-Path $env:LOCALAPPDATA "Unity/cache/packages"
if (Test-Path $globalCache) {
    $candidates += Get-ChildItem $globalCache -Directory |
        ForEach-Object { Get-ChildItem $_.FullName -Directory -ErrorAction SilentlyContinue } |
        Where-Object { $_.Name -like "$Package@*" }
}

if ($Version) {
    $candidates = $candidates | Where-Object {
        (Get-Content (Join-Path $_.FullName "package.json") -Raw | ConvertFrom-Json).version -eq $Version
    }
}

if ($candidates.Count -eq 0) {
    throw @"
There is no clean '$Package' either in the project's Library/PackageCache or in the global Unity cache.
Unity clears the project cache as soon as the package becomes embedded — so vendoring has to happen BEFORE
the Packages/$Package folder appears. If it is already gone: remove the embedded copy for a while, let Unity
resolve the package from the registry, and run the script again.
"@
}

$source = $candidates[0].FullName
$sourceVersion = (Get-Content (Join-Path $source "package.json") -Raw | ConvertFrom-Json).version
Write-Host "taking the clean package: $source (version $sourceVersion)"

robocopy $source $target /E /NFL /NDL /NJH /NJS /NP /R:1 /W:1 | Out-Null
if ($LASTEXITCODE -ge 8) { throw "robocopy broke down with code $LASTEXITCODE" }

# From the cache the files arrive read-only — the patch will not land on them, and editing them later will not work either.
Get-ChildItem $target -Recurse -File | Where-Object { $_.IsReadOnly } | ForEach-Object { $_.IsReadOnly = $false }

Push-Location $Project
try {
    Write-Host "applying $([System.IO.Path]::GetFileName($patch))"
    git apply --3way --verbose $patch
    if ($LASTEXITCODE -ne 0) { throw "git apply refused — the patch did not match version $sourceVersion" }
}
finally {
    Pop-Location
}

Write-Host ""
Write-Host "done: '$target' is vendored and patched"
Write-Host "next: make sure the BLOBCHEG_ENTITIES_PATCH define is set in the project's Player Settings —"
Write-Host "without it the Blobcheg.Entities.Patch assembly does not compile and the patch does not install."
