#Requires -Version 5.1
$ErrorActionPreference = 'Continue'
$hostDir = Join-Path $env:LOCALAPPDATA 'MWB-SWB-Host'
$publicHost = 'C:\Users\Public\MWB-SWB-Host'
New-Item -ItemType Directory -Force -Path $hostDir, $publicHost | Out-Null

# Keep helper scripts available
foreach ($f in @('LaunchMwbWithSwb.vbs','LaunchMwbWithSwb.cmd','MwbLaunchStub.exe','WrapGarageExe.ps1','RetargetMwbShortcuts.ps1')) {
  $src = Join-Path $PSScriptRoot $f
  if (Test-Path $src) {
    Copy-Item $src (Join-Path $hostDir $f) -Force -ErrorAction SilentlyContinue
    Copy-Item $src (Join-Path $publicHost $f) -Force -ErrorAction SilentlyContinue
  }
}

$garageDir = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Garage\Mouse without Borders'
$mwbExe = Join-Path $garageDir 'MouseWithoutBorders.exe'
$original = Join-Path $garageDir 'MouseWithoutBorders.original.exe'
if (-not (Test-Path $mwbExe)) { throw "Garage exe missing: $mwbExe" }

$icon = if (Test-Path $original) { "$original,0" } else { "$mwbExe,0" }
$bakDir = Join-Path $hostDir 'shortcut-bak'
New-Item -ItemType Directory -Force -Path $bakDir | Out-Null

function Set-MwbShortcut([string]$lnkPath) {
  $dir = Split-Path $lnkPath -Parent
  if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
  if (Test-Path $lnkPath) {
    $bakAlt = Join-Path $bakDir ([IO.Path]::GetFileName($lnkPath) + '.mwb-swb-bak')
    try { Copy-Item $lnkPath ($lnkPath + '.mwb-swb-bak') -Force -ErrorAction Stop }
    catch { Copy-Item $lnkPath $bakAlt -Force -ErrorAction SilentlyContinue }
    Remove-Item $lnkPath -Force -ErrorAction SilentlyContinue
  }
  $sh = New-Object -ComObject WScript.Shell
  $s = $sh.CreateShortcut($lnkPath)
  # Point at wrapped MouseWithoutBorders.exe (stub) — works for Explorer/desktop.
  $s.TargetPath = $mwbExe
  $s.Arguments = ''
  $s.WorkingDirectory = $garageDir
  $s.WindowStyle = 1
  $s.Description = 'Mouse without Borders + Sound Synchro (MWB+SWB)'
  $s.IconLocation = $icon
  $s.Save()
  Write-Output "retargeted=$lnkPath -> $mwbExe"
}

$candidates = @(
  (Join-Path $env:PUBLIC 'Desktop\Mouse without Borders.lnk'),
  (Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs\Microsoft Garage\Mouse without Borders.lnk'),
  (Join-Path $env:USERPROFILE 'Desktop\Mouse without Borders.lnk'),
  (Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\Microsoft Garage\Mouse without Borders.lnk')
)

$shScan = New-Object -ComObject WScript.Shell
foreach ($root in @(
  (Join-Path $env:PUBLIC 'Desktop'),
  [Environment]::GetFolderPath('Desktop'),
  (Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs'),
  (Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs')
)) {
  if (-not (Test-Path $root)) { continue }
  Get-ChildItem $root -Recurse -Filter '*.lnk' -ErrorAction SilentlyContinue | ForEach-Object {
    try {
      $s = $shScan.CreateShortcut($_.FullName)
      $t = [string]$s.TargetPath
      $a = [string]$s.Arguments
      if ($_.Name -match 'Mouse without Borders' -or $t -match 'MouseWithoutBorders|MMIcon|LaunchMwbWithSwb' -or $a -match 'LaunchMwbWithSwb') {
        if ($candidates -notcontains $_.FullName) { $candidates += $_.FullName }
      }
    } catch {}
  }
}

$ok=0; $fail=0
foreach ($c in ($candidates | Select-Object -Unique)) {
  try { Set-MwbShortcut $c; $ok++ } catch { Write-Output "fail=$c err=$($_.Exception.Message)"; $fail++ }
}

$hostLnk = Join-Path $env:USERPROFILE 'AppData\Roaming\Microsoft\Windows\Start Menu\Programs\MWB-SWB Host.lnk'
$s2 = (New-Object -ComObject WScript.Shell).CreateShortcut($hostLnk)
$s2.TargetPath = Join-Path $hostDir 'MwbSwb.Host.exe'
$s2.WorkingDirectory = $hostDir
$s2.Description = 'SWB attachment Host'
$s2.Save()
Write-Output "host-shortcut=$hostLnk"
Write-Output "retarget=done ok=$ok fail=$fail"