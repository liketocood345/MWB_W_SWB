#Requires -Version 5.1
# Wait until overwrite targets are unlocked. "Closed" = exclusive write open succeeds.
# Does not force-kill MWB UI. Optionally stops MouseWithoutBordersSvc first (releases Svc.exe).
param(
  [string[]]$Path = @(),
  [string]$GarageDir = $(Join-Path ${env:ProgramFiles(x86)} 'Microsoft Garage\Mouse without Borders'),
  [string]$HostDir = $(Join-Path $env:LOCALAPPDATA 'MWB-SWB-Host'),
  [ValidateSet('Garage','Host','Custom')]
  [string]$Preset = 'Garage',
  [string]$StatusFile = $(Join-Path $env:TEMP 'mwb-swb-setup-status.txt'),
  [string]$BusyMessage = 'Close Mouse without Borders to unlock install files. Setup continues when files are free.',
  [int]$PollMs = 800,
  [int]$MaxWaitSec = 0,
  [switch]$StopGarageService
)

$ErrorActionPreference = 'Continue'

function Write-Status([string]$msg) {
  try { Set-Content -Path $StatusFile -Value $msg -Encoding UTF8 } catch { }
  Write-Output $msg
}

function Test-FileUnlocked([string]$file) {
  if (-not (Test-Path -LiteralPath $file)) { return $true }
  try {
    $fs = [IO.File]::Open($file, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    $fs.Close()
    return $true
  } catch {
    return $false
  }
}

function Get-LockedAmong([string[]]$files) {
  $locked = New-Object System.Collections.Generic.List[string]
  $seen = @{}
  foreach ($f in $files) {
    if (-not $f) { continue }
    $full = [IO.Path]::GetFullPath($f)
    $key = $full.ToLowerInvariant()
    if ($seen.ContainsKey($key)) { continue }
    $seen[$key] = $true
    if (-not (Test-FileUnlocked $full)) { [void]$locked.Add($full) }
  }
  return ,$locked.ToArray()
}

if ($Preset -eq 'Garage' -and $Path.Count -eq 0) {
  $Path = @(
    (Join-Path $GarageDir 'MouseWithoutBorders.exe'),
    (Join-Path $GarageDir 'MouseWithoutBorders.original.exe'),
    (Join-Path $GarageDir 'MouseWithoutBordersHelper.exe')
  )
} elseif ($Preset -eq 'Host' -and $Path.Count -eq 0) {
  $Path = @(
    (Join-Path $HostDir 'MwbSwb.Host.exe'),
    (Join-Path $HostDir 'MwbSwb.Host.dll'),
    (Join-Path $HostDir 'MwbSwb.Core.dll'),
    (Join-Path $HostDir 'MwbSwb.Audio.dll')
  )
}

$targets = @($Path | Where-Object { $_ })
if ($targets.Count -eq 0) {
  Write-Output 'wait-targets=none'
  exit 0
}

# Stop service first so Svc.exe can unlock; UI files still wait for user close.
if ($StopGarageService) {
  Write-Status 'Stopping Mouse without Borders service...'
  Stop-Service -Name 'MouseWithoutBordersSvc' -Force -ErrorAction SilentlyContinue
  $svcDeadline = (Get-Date).AddSeconds(30)
  while ((Get-Service -Name 'MouseWithoutBordersSvc' -ErrorAction SilentlyContinue).Status -eq 'Running' -and (Get-Date) -lt $svcDeadline) {
    Start-Sleep -Milliseconds 500
  }
}

$existing = @($targets | Where-Object { Test-Path -LiteralPath $_ })
Write-Output ("wait-targets={0} existing={1}" -f $targets.Count, $existing.Count)

$locked = @(Get-LockedAmong $targets)
if ($locked.Count -eq 0) {
  Write-Output 'files-unlocked=already'
  Write-Output 'ready=1'
  exit 0
}

Write-Status $BusyMessage
Write-Output ("locked={0}" -f (($locked | ForEach-Object { [IO.Path]::GetFileName($_) }) -join ','))

$deadline = if ($MaxWaitSec -gt 0) { (Get-Date).AddSeconds($MaxWaitSec) } else { [datetime]::MaxValue }
while ($true) {
  $locked = @(Get-LockedAmong $targets)
  if ($locked.Count -eq 0) { break }
  if ((Get-Date) -ge $deadline) {
    Write-Status ('ERROR: still locked: ' + (($locked | ForEach-Object { [IO.Path]::GetFileName($_) }) -join ', '))
    Write-Output 'files-unlocked=timeout'
    exit 1
  }
  Start-Sleep -Milliseconds $PollMs
}

Write-Output 'files-unlocked=ok'
Write-Output 'ready=1'
exit 0