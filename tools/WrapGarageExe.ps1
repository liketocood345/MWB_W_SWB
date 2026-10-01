#Requires -Version 5.1
# After Garage MSI: install patched UI as .original.exe (Launch SWB), stub as MouseWithoutBorders.exe
$ErrorActionPreference = 'Stop'
$garageDir = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Garage\Mouse without Borders'
$exe = Join-Path $garageDir 'MouseWithoutBorders.exe'
$original = Join-Path $garageDir 'MouseWithoutBorders.original.exe'
$marker = Join-Path $garageDir 'MWB-SWB-WRAPPED.txt'

$stubCandidates = @(
  (Join-Path $env:LOCALAPPDATA 'MWB-SWB-Host\MwbLaunchStub.exe'),
  'C:\Users\Public\MWB-SWB-Host\MwbLaunchStub.exe',
  (Join-Path $PSScriptRoot 'MwbLaunchStub.exe')
)
$patchedCandidates = @(
  (Join-Path $PSScriptRoot 'MouseWithoutBorders.patched.exe'),
  (Join-Path $env:LOCALAPPDATA 'MWB-SWB-Host\MouseWithoutBorders.patched.exe'),
  'C:\Users\Public\MWB-SWB-Host\MouseWithoutBorders.patched.exe',
  (Join-Path (Split-Path $PSScriptRoot -Parent) 'Garage\MouseWithoutBorders.patched.exe')
)
$stub = $stubCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1
$patched = $patchedCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $stub) { throw 'MwbLaunchStub.exe not found' }
if (-not (Test-Path $exe) -and -not (Test-Path $original) -and -not $patched) {
  throw "Garage exe missing in $garageDir"
}

# Direct install: best-effort kill/stop, no unlock wait
Get-Process -Name 'MouseWithoutBorders','MouseWithoutBorders.original','MouseWithoutBordersHelper','MousewithoutBordersHelper' -ErrorAction SilentlyContinue |
  Stop-Process -Force -ErrorAction SilentlyContinue
Stop-Service -Name 'MouseWithoutBordersSvc' -Force -ErrorAction SilentlyContinue
Start-Sleep -Seconds 1

function Copy-Retry([string]$From, [string]$To, [int]$Tries = 8, [int]$SleepSec = 2) {
  for ($i = 1; $i -le $Tries; $i++) {
    try {
      Copy-Item -LiteralPath $From -Destination $To -Force -ErrorAction Stop
      return
    } catch {
      Write-LogSoft "copy-retry $i/$Tries $($_.Exception.Message)"
      if ($i -eq $Tries) { throw }
      Start-Sleep -Seconds $SleepSec
    }
  }
}
function Write-LogSoft([string]$msg) {
  try {
    Add-Content -Path (Join-Path $env:TEMP 'mwb-swb-setup.log') -Value ("[{0}] wrap {1}" -f (Get-Date -Format 'HH:mm:ss'), $msg) -Encoding UTF8
  } catch { }
}

try {
  $ow = 'HKLM:\SOFTWARE\Microsoft\MouseWithoutBorders'
  if (-not (Test-Path $ow)) { New-Item $ow -Force | Out-Null }
  New-ItemProperty -Path $ow -Name 'OneWayControlMode' -Value 0 -PropertyType DWord -Force | Out-Null
  New-ItemProperty -Path $ow -Name 'OneWayClipboardMode' -Value 0 -PropertyType DWord -Force | Out-Null
  Write-Output 'OneWayControlMode=0 (HKLM) — keep MWB remote control usable under SWB attach'
} catch {
  Write-Warning "Could not write HKLM OneWay*=0 (need elevation once): $($_.Exception.Message)"
}

if ($patched) {
  Copy-Retry $patched $original
  Write-Output "seeded-original-from-patched=$patched"
} elseif (-not (Test-Path $original)) {
  if (-not (Test-Path $exe)) { throw "Cannot seed original: $exe missing" }
  Copy-Retry $exe $original
  Write-Output "seeded-original-from-msi=$exe"
} else {
  Write-Output "kept-existing-original=$original"
}

Copy-Retry $stub $exe
$hasLaunch = $false
try {
  $bytes = [IO.File]::ReadAllBytes($original)
  $uni = [Text.Encoding]::Unicode.GetString($bytes)
  $hasLaunch = $uni.Contains('Launch SWB')
} catch {}
$stamp = @(
  "wrapped=$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')",
  "stubSha=$((Get-FileHash $stub -Algorithm SHA256).Hash)",
  "originalSha=$((Get-FileHash $original -Algorithm SHA256).Hash)",
  "launchSwbButton=$hasLaunch"
) -join "`r`n"
Set-Content -Path $marker -Value $stamp -Encoding UTF8
Write-Output "wrapped-exe=$exe size=$((Get-Item $exe).Length)"
Write-Output "original-exe=$original size=$((Get-Item $original).Length) LaunchSWB=$hasLaunch"
if (-not $hasLaunch) { Write-Warning 'Patched UI missing Launch SWB string' }