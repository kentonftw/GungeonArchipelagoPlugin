<#
.SYNOPSIS
    Merges the built ArchiGungeon.dll with its networking libraries into ArchipelaGun.dll (the file the mod
    actually loads), then installs it into an r2modman profile.

.DESCRIPTION
    Runs automatically after every build in Visual Studio (see the RepackAndInstall target in Mod.csproj).
    It can also be run by hand:
        powershell -ExecutionPolicy Bypass -File _DLLRepack\Repack.ps1 -Configuration Release -ProfileName Archigun

    Install is skipped with a warning (not an error) when the profile can't be found or the game has the DLL
    locked, so the build still succeeds and the merged DLL is left in _DLLRepack\ArchipelaGun.dll.

.PARAMETER Configuration
    Build configuration to merge: Release or Debug.

.PARAMETER ProfileName
    r2modman profile name. The mod is installed into
    %APPDATA%\r2modmanPlus-local\ETG\profiles\<ProfileName>\BepInEx\plugins\<folder containing "ArchipelaGun">.

.PARAMETER PluginDir
    Install into this folder instead of looking up the r2modman profile.
#>
param(
    [string]$Configuration = "Release",
    [string]$ProfileName = "Archigun",
    [string]$PluginDir = ""
)

$ErrorActionPreference = "Stop"

$repackDir = $PSScriptRoot
$repoRoot = Split-Path -Parent $repackDir
$buildDir = Join-Path $repoRoot "bin\$Configuration"
$builtDll = Join-Path $buildDir "ArchiGungeon.dll"
$mergedDll = Join-Path $repackDir "ArchipelaGun.dll"

# 1. Merge ---------------------------------------------------------------------------------------------------
if (-not (Test-Path $builtDll)) {
    throw "Build output not found: $builtDll"
}

$ilrepack = Get-ChildItem (Join-Path $repoRoot "packages") -Recurse -Filter "ILRepack.exe" -ErrorAction SilentlyContinue |
    Select-Object -First 1
if (-not $ilrepack) {
    throw "ILRepack.exe not found under packages\. Restore NuGet packages first."
}

Copy-Item $builtDll $repackDir -Force

& $ilrepack.FullName "/out:$mergedDll" `
    (Join-Path $repackDir "ArchiGungeon.dll") `
    (Join-Path $repackDir "Archipelago.MultiClient.Net.dll") `
    (Join-Path $repackDir "websocket-sharp.dll") `
    "/lib:$buildDir"

if ($LASTEXITCODE -ne 0) {
    throw "ILRepack failed with exit code $LASTEXITCODE"
}
Write-Host "Repack: merged $Configuration build into $mergedDll"

# 2. Install -------------------------------------------------------------------------------------------------
if (-not $PluginDir) {
    $pluginsRoot = Join-Path $env:APPDATA "r2modmanPlus-local\ETG\profiles\$ProfileName\BepInEx\plugins"
    if (Test-Path $pluginsRoot) {
        $modFolder = Get-ChildItem $pluginsRoot -Directory -Filter "*ArchipelaGun*" | Select-Object -First 1
        if ($modFolder) {
            $PluginDir = $modFolder.FullName
        }
    }
}

if (-not $PluginDir -or -not (Test-Path $PluginDir)) {
    Write-Warning "Repack: no ArchipelaGun folder found in r2modman profile '$ProfileName'. Skipped install; copy $mergedDll yourself or pass -PluginDir."
    exit 0
}

try {
    Copy-Item $mergedDll (Join-Path $PluginDir "ArchipelaGun.dll") -Force
}
catch {
    Write-Warning "Repack: could not copy into $PluginDir (is the game running?). $($_.Exception.Message)"
    exit 0
}

# An unmerged ArchiGungeon.dll next to ArchipelaGun.dll would load the plugin twice.
$strayDll = Join-Path $PluginDir "ArchiGungeon.dll"
if (Test-Path $strayDll) {
    Remove-Item $strayDll -Force
    Write-Host "Repack: removed unmerged ArchiGungeon.dll from the plugins folder"
}

Write-Host "Repack: installed ArchipelaGun.dll to $PluginDir"
exit 0
