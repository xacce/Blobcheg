<#
.SYNOPSIS
    Гоняет деструктивный набор Blobcheg (Samples~/AdvancedTests) в указанном Unity-проекте.

.DESCRIPTION
    Набор лежит в Samples~ и потому невидим для Unity. Скрипт копирует его в Assets проекта,
    запускает EditMode-прогон с фильтром Blobcheg.AdvancedTests и убирает копию за собой.

    Источник истины — Samples~/AdvancedTests. Копия одноразовая: правки вносить в пакет, а не в неё.

.EXAMPLE
    ./tools/run-advanced-tests.ps1 -Project C:/Projects/Evuck/EvuckServer
#>
param(
    [Parameter(Mandatory = $true)][string]$Project,
    [string]$EditorVersion = '6000.3.18f1',
    [string]$Output,
    [int]$TimeoutSeconds = 1800,
    [switch]$Keep
)

$ErrorActionPreference = 'Stop'

$source = Join-Path $PSScriptRoot '..\Samples~\AdvancedTests'
if (-not (Test-Path $source)) { throw "Не найден набор: $source" }

$project = (Resolve-Path $Project).Path
$assets = Join-Path $project 'Assets'
if (-not (Test-Path $assets)) { throw "Это не Unity-проект: $project" }

$target = Join-Path $assets 'BlobchegAdvancedTests'
$targetMeta = "$target.meta"

if (-not $Output) { $Output = Join-Path $PSScriptRoot 'advanced-tests.xml' }

function Remove-Copy {
    if (Test-Path $target) { Remove-Item -Recurse -Force $target }
    if (Test-Path $targetMeta) { Remove-Item -Force $targetMeta }
}

Remove-Copy
Copy-Item -Recurse -Force $source $target
Write-Host "Набор скопирован в $target"

try {
    unity test $project `
        --mode EditMode `
        --filter Blobcheg.AdvancedTests `
        --output $Output `
        --editor-version $EditorVersion `
        --timeout $TimeoutSeconds `
        --non-interactive --no-banner
    $code = $LASTEXITCODE
}
finally {
    if (-not $Keep) { Remove-Copy; Write-Host 'Копия убрана' }
    else { Write-Host "Копия оставлена в $target (-Keep)" }
}

if (Test-Path $Output) {
    [xml]$xml = Get-Content $Output
    $run = $xml.'test-run'
    Write-Host "Итог: всего $($run.total), провалено $($run.failed), пропущено $($run.skipped)"
}

exit $code
