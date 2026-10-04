<#
.SYNOPSIS
  Installs or upgrades a register (Newrest POS caisse + local vision service) on a Windows 10/11 x64 workstation.

.DESCRIPTION
  Run as administrator from the extracted package (newrest-pos-register-<version>.zip):
    .\install-register.ps1 -ServerUrl https://pos.newrest.ma/ -Printer EscPosSpooler -PrinterTarget "EPSON TM-T20III"
  - Upgrade: the application is replaced, the data (SQLite, receipts, backups, device key) and appsettings.local.json are kept.
    The application must be closed (the script refuses to upgrade while a sale may be in progress).
  - Data folder C:\ProgramData\Newrest\POS: modifiable by local users (the cashier session), the Vision subfolder by
    administrators only (Gemini key, dataset).
  - The register is then registered on its first start (register identifier + device key from the back-office).
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [Uri] $ServerUrl,
    [string] $InstallFolder = "C:\Program Files\Newrest\POS\Caisse",
    [string] $DataFolder = "C:\ProgramData\Newrest\POS",
    [ValidateSet("Simulated", "EscPosTcp", "EscPosSpooler", "EscPosFile")] [string] $Printer = "EscPosSpooler",
    [string] $PrinterTarget = "",
    [ValidateSet("Keyboard", "Serial", "Simulated")] [string] $BadgeReader = "Keyboard",
    [string] $BadgeReaderPort = "COM3",
    [ValidateSet("Window", "Simulated")] [string] $CustomerDisplay = "Window",
    [switch] $SkipVision,
    [string] $VisionProvider = "gemini",
    [securestring] $GeminiApiKey,
    [switch] $AutoStart
)
$ErrorActionPreference = "Stop"
$package = $PSScriptRoot

if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw "Exécuter ce script en tant qu'administrateur."
}
if ($ServerUrl.Scheme -ne "https" -and $ServerUrl.Host -notin @("localhost", "127.0.0.1")) {
    throw "Le serveur doit être en HTTPS ($ServerUrl)."
}
if (Get-Process -Name "Newrest.Pos.Caisse" -ErrorAction SilentlyContinue) {
    throw "La caisse est ouverte : faire la clôture ou fermer l'application avant la mise à jour."
}

$version = (Get-Content (Join-Path $package "VERSION")).Trim()
Write-Host "Installation de la caisse $version"

# 1. Application (data and local settings preserved on upgrade).
New-Item -ItemType Directory -Force -Path $InstallFolder | Out-Null
$local = Join-Path $InstallFolder "appsettings.local.json"
$keep = if (Test-Path $local) { Get-Content $local -Raw } else { $null }
robocopy (Join-Path $package "caisse") $InstallFolder /MIR /XF appsettings.local.json /NFL /NDL /NJH /NJS | Out-Null
if ($LASTEXITCODE -ge 8) { throw "Copie de l'application impossible ($LASTEXITCODE)." }

# 2. Data folder: users modify (the register runs in the cashier's session).
New-Item -ItemType Directory -Force -Path $DataFolder | Out-Null
icacls $DataFolder /grant "*S-1-5-32-545:(OI)(CI)M" | Out-Null   # BUILTIN\Users

# 3. Local settings (only written on first install; edit by hand afterwards).
if ($null -eq $keep) {
    @{
        Register = @{ ServerUrl = $ServerUrl.AbsoluteUri; DataFolder = $DataFolder }
        Devices  = @{ Printer = $Printer; PrinterTarget = $PrinterTarget; BadgeReader = $BadgeReader; BadgeReaderPort = $BadgeReaderPort
                      CustomerDisplay = $CustomerDisplay; Camera = "VisionService" }
    } | ConvertTo-Json -Depth 4 | Set-Content -Encoding UTF8 $local
} else {
    Set-Content -Encoding UTF8 -Path $local -Value $keep
}

# 4. Local vision service (Windows service, 127.0.0.1 only).
if (-not $SkipVision) {
    $visionArgs = @{ Source = (Join-Path $package "vision"); DataFolder = (Join-Path $DataFolder "Vision"); Provider = $VisionProvider }
    if ($GeminiApiKey) { $visionArgs.GeminiApiKey = $GeminiApiKey }
    & (Join-Path $package "install-vision-service.ps1") @visionArgs
}

# 5. Shortcuts (desktop for everyone, optional automatic start in the cashier session).
$shell = New-Object -ComObject WScript.Shell
$targets = @([Environment]::GetFolderPath("CommonDesktopDirectory"))
if ($AutoStart) { $targets += [Environment]::GetFolderPath("CommonStartup") }
foreach ($folder in $targets) {
    $shortcut = $shell.CreateShortcut((Join-Path $folder "Newrest POS.lnk"))
    $shortcut.TargetPath = Join-Path $InstallFolder "Newrest.Pos.Caisse.exe"
    $shortcut.WorkingDirectory = $InstallFolder
    $shortcut.Save()
}

Write-Host "Caisse $version installée. Au premier lancement : identifiant de caisse + clé d'appareil (back-office > Points de vente & caisses)."
