<#
.SYNOPSIS
Вендорит com.unity.entities в Packages/ проекта и накатывает на него патч Blobcheg.

.DESCRIPTION
Патч ссылок Blobcheg требует правленого com.unity.entities: в него добавлена точка расширения
BlobchegPatchHook и её вызовы. Пакет из реестра править нельзя, поэтому он переезжает в Packages/
проекта — с этого момента он обычный embedded-пакет, который Unity берёт вместо реестрового.

Скрипт делает три вещи и ничего больше: копирует чистый пакет из кеша, снимает с файлов
read-only (из кеша они приезжают только для чтения) и накатывает .patch из этой же папки.

.EXAMPLE
./vendor.ps1 -Project C:\path\to\UnityProject

.EXAMPLE
# заново, поверх уже вендорнутого — снесёт папку и соберёт с нуля
./vendor.ps1 -Project C:\path\to\UnityProject -Force
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $Project,

    [string] $Package = "com.unity.entities",

    # Пусто — взять единственную версию, найденную в кешах. Иначе точная версия, например 1.4.8.
    [string] $Version = "",

    [switch] $Force
)

$ErrorActionPreference = "Stop"

$patch = Join-Path $PSScriptRoot "$Package@$(if ($Version) { $Version } else { '1.4.8' }).patch"
if (-not (Test-Path $patch)) {
    throw "Нет патча '$patch'. Рядом со скриптом должен лежать .patch на ту же версию пакета."
}

if (-not (Test-Path (Join-Path $Project "ProjectSettings/ProjectVersion.txt"))) {
    throw "'$Project' — не Unity-проект: нет ProjectSettings/ProjectVersion.txt"
}

$target = Join-Path $Project "Packages/$Package"
if (Test-Path $target) {
    if (-not $Force) {
        throw "'$target' уже существует. Перевендорить с нуля — ключ -Force (папка будет снесена)."
    }

    Write-Host "сношу прежний '$target'"
    Remove-Item -Recurse -Force $target
}

# Чистый пакет ищем сначала в кеше самого проекта, потом в глобальном кеше Unity. В кеше проекта
# папка называется с хешем (com.unity.entities@6ce362df365b), в глобальном — с версией.
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
Чистого '$Package' нет ни в Library/PackageCache проекта, ни в глобальном кеше Unity.
Кеш проекта Unity вычищает, как только пакет становится embedded, — поэтому вендорить надо ДО того,
как папка Packages/$Package появилась. Если её уже нет: временно убери embedded-копию, дай Unity
разрешить пакет из реестра, и запусти скрипт снова.
"@
}

$source = $candidates[0].FullName
$sourceVersion = (Get-Content (Join-Path $source "package.json") -Raw | ConvertFrom-Json).version
Write-Host "беру чистый пакет: $source (версия $sourceVersion)"

robocopy $source $target /E /NFL /NDL /NJH /NJS /NP /R:1 /W:1 | Out-Null
if ($LASTEXITCODE -ge 8) { throw "robocopy сорвался с кодом $LASTEXITCODE" }

# Из кеша файлы приезжают read-only — патч на них не ляжет, да и править их потом не выйдет.
Get-ChildItem $target -Recurse -File | Where-Object { $_.IsReadOnly } | ForEach-Object { $_.IsReadOnly = $false }

Push-Location $Project
try {
    Write-Host "накатываю $([System.IO.Path]::GetFileName($patch))"
    git apply --3way --verbose $patch
    if ($LASTEXITCODE -ne 0) { throw "git apply отказался — патч не сошёлся с версией $sourceVersion" }
}
finally {
    Pop-Location
}

Write-Host ""
Write-Host "готово: '$target' вендорнут и пропатчен"
Write-Host "дальше: убедись, что дефайн BLOBCHEG_ENTITIES_PATCH стоит в Player Settings проекта —"
Write-Host "без него сборка Blobcheg.Entities.Patch не собирается и патч не встаёт."
