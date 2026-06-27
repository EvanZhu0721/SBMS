$ErrorActionPreference = "Stop"

$Script = Join-Path $PSScriptRoot "check-displays.py"

if (-not (Test-Path $Script)) {
    throw "Missing checker: $Script"
}

$Python = $null

try {
    $PyLauncher = Get-Command py -ErrorAction Stop
    $Python = @($PyLauncher.Source, "-3")
} catch {
    try {
        $PythonExe = Get-Command python -ErrorAction Stop
        $Python = @($PythonExe.Source)
    } catch {
        throw "Python was not found. Run python .\check-displays.py on a machine with Python installed."
    }
}

if ($Python.Count -gt 1) {
    & $Python[0] $Python[1] $Script
} else {
    & $Python[0] $Script
}
exit $LASTEXITCODE
