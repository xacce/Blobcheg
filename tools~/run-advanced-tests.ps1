<#
.SYNOPSIS
    Runs the destructive Blobcheg set (Samples~/AdvancedTests) in the given Unity project.

.DESCRIPTION
    The set lives in Samples~ and is therefore invisible to Unity. The script copies it into the
    project's Assets, runs an EditMode pass filtered by Blobcheg.AdvancedTests and removes the copy
    afterwards.

    The source of truth is Samples~/AdvancedTests. The copy is disposable: edits go into the package,
    not into it.

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
if (-not (Test-Path $source)) { throw "Set not found: $source" }

$project = (Resolve-Path $Project).Path
$assets = Join-Path $project 'Assets'
if (-not (Test-Path $assets)) { throw "This is not a Unity project: $project" }

$target = Join-Path $assets 'BlobchegAdvancedTests'
$targetMeta = "$target.meta"

if (-not $Output) { $Output = Join-Path $PSScriptRoot 'advanced-tests.xml' }

function Remove-Copy {
    if (Test-Path $target) { Remove-Item -Recurse -Force $target }
    if (Test-Path $targetMeta) { Remove-Item -Force $targetMeta }
}

Remove-Copy
Copy-Item -Recurse -Force $source $target
Write-Host "The set was copied into $target"

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
    if (-not $Keep) { Remove-Copy; Write-Host 'The copy was removed' }
    else { Write-Host "The copy was left in $target (-Keep)" }
}

if (Test-Path $Output) {
    [xml]$xml = Get-Content $Output
    $run = $xml.'test-run'
    Write-Host "Result: total $($run.total), failed $($run.failed), skipped $($run.skipped)"
}

exit $code
