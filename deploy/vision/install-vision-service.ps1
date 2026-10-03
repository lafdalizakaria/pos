<#
.SYNOPSIS
  Installs the local vision service of a register as a Windows service (NSSM), listening on 127.0.0.1 only.

.DESCRIPTION
  - Copies vision/ to the install folder and creates its virtual environment with uv (Python 3.12, locked deps).
  - Registers "NewrestPosVision" with NSSM: automatic start, restart on failure, logs rotated in <DataFolder>\logs.
  - The Gemini key is never stored in clear: it is passed with -GeminiApiKey and written to <DataFolder>\gemini.key
    encrypted with DPAPI (machine scope; folder readable by Administrators/SYSTEM only). Omit it for mock/yolo.
  - Runtime switch (mock -> gemini -> yolo -> hybrid) without redeploying: edit <DataFolder>\vision.json.

.EXAMPLE
  .\install-vision-service.ps1 -Source ..\..\vision -Provider gemini -GeminiApiKey (Read-Host -AsSecureString)
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Source,
    [string] $InstallFolder = "C:\Program Files\Newrest\POS\Vision",
    [string] $DataFolder = "C:\ProgramData\Newrest\POS\Vision",
    [ValidateSet("mock", "gemini", "yolo", "hybrid")] [string] $Provider = "mock",
    [int] $Port = 8765,
    [securestring] $GeminiApiKey,
    [string] $Nssm = "nssm.exe",
    [string] $Uv = "uv.exe",
    [string] $ServiceName = "NewrestPosVision"
)
$ErrorActionPreference = "Stop"

if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw "Exécuter ce script en tant qu'administrateur."
}

New-Item -ItemType Directory -Force -Path $InstallFolder, $DataFolder, "$DataFolder\logs", "$DataFolder\dataset" | Out-Null
robocopy $Source $InstallFolder /MIR /XD .venv data .pytest_cache .ruff_cache __pycache__ tests /NFL /NDL /NJH /NJS | Out-Null
if ($LASTEXITCODE -ge 8) { throw "Copie impossible ($LASTEXITCODE)." }

Push-Location $InstallFolder
try {
    & $Uv sync --locked --no-dev --python 3.12
    if ($LASTEXITCODE -ne 0) { throw "uv sync a échoué." }
} finally { Pop-Location }

# Runtime overrides (hot reloaded). Created once: never overwrite the site's settings on upgrade.
$overrides = Join-Path $DataFolder "vision.json"
if (-not (Test-Path $overrides)) {
    @{ provider = $Provider } | ConvertTo-Json | Set-Content -Encoding UTF8 $overrides
}
# Only Administrators and SYSTEM may change the provider or read the data (dataset images).
icacls $DataFolder /inheritance:r /grant:r "Administrators:(OI)(CI)F" "SYSTEM:(OI)(CI)F" | Out-Null

if (Get-Service $ServiceName -ErrorAction SilentlyContinue) {
    & $Nssm stop $ServiceName | Out-Null
    & $Nssm remove $ServiceName confirm | Out-Null
}
$python = Join-Path $InstallFolder ".venv\Scripts\python.exe"
& $Nssm install $ServiceName $python "-m" "app.main"
& $Nssm set $ServiceName AppDirectory $InstallFolder
& $Nssm set $ServiceName DisplayName "Newrest POS - Reconnaissance des plateaux"
& $Nssm set $ServiceName Start SERVICE_AUTO_START
& $Nssm set $ServiceName AppStdout "$DataFolder\logs\vision.log"
& $Nssm set $ServiceName AppStderr "$DataFolder\logs\vision.log"
& $Nssm set $ServiceName AppRotateFiles 1
& $Nssm set $ServiceName AppRotateBytes 10485760
& $Nssm set $ServiceName AppExit Default Restart
& $Nssm set $ServiceName AppRestartDelay 2000

$keyFile = Join-Path $DataFolder "gemini.key"
if ($GeminiApiKey) {
    Add-Type -AssemblyName System.Security
    $bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($GeminiApiKey)
    try {
        $bytes = [Text.Encoding]::UTF8.GetBytes([Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr))
        $protected = [Security.Cryptography.ProtectedData]::Protect($bytes, $null, [Security.Cryptography.DataProtectionScope]::LocalMachine)
        [IO.File]::WriteAllBytes($keyFile, $protected)
        [Array]::Clear($bytes, 0, $bytes.Length)
    } finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr) }
}
$environment = @(
    "VISION_HOST=127.0.0.1",
    "VISION_PORT=$Port",
    "VISION_PROVIDER=$Provider",
    "VISION_CONFIG_FILE=$overrides",
    "VISION_DATASET_DIR=$DataFolder\dataset",
    "VISION_GEMINI_API_KEY_FILE=$keyFile"
)
& $Nssm set $ServiceName AppEnvironmentExtra @environment | Out-Null

& $Nssm start $ServiceName
Start-Sleep -Seconds 3
Invoke-RestMethod "http://127.0.0.1:$Port/health" | Format-List
