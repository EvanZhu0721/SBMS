$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

Import-Module Pester -RequiredVersion 4.10.1 -Force

$repository = [IO.Path]::GetFullPath(
    (Join-Path $PSScriptRoot '..\..')
)
$testRoots = @(
    (Join-Path $repository 'tests')
    (Join-Path $repository 'installer\tests')
)
$paths = @(
    $testRoots |
        Where-Object { Test-Path -LiteralPath $_ -PathType Container } |
        ForEach-Object {
            Get-ChildItem -LiteralPath $_ -Filter '*.Tests.ps1' -File -Recurse
        } |
        Sort-Object FullName |
        ForEach-Object { $_.FullName }
)
if ($paths.Count -eq 0) {
    throw 'No Pester test files were discovered.'
}

Write-Host "pester_engine=$($PSVersionTable.PSVersion)"
Write-Host "pester_files=$($paths.Count)"
$result = Invoke-Pester -Script $paths -PassThru
if ($result.FailedCount -ne 0) {
    throw "$($result.FailedCount) Pester test(s) failed."
}
