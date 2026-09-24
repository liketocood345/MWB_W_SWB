# 06 · Verification

- Date: 2026-09-23T12:36:20Z
- Package: `MWB-SWB-0.2.0-pin-v0.99.1-win-x64`
- Integrity: see checklist
- **MWB retention score: target ≥75% after Host+pin packaging**

## Checklist

| ID | Title | Status | Detail |
|----|-------|--------|--------|
| I1 | Handbook present | **PASS** | docs/handbook |
| I2 | Solution builds Release | **PASS** | Host/Installer/App |
| I3 | Self-contained / publish win-x64 | **PASS** | dist package |
| I4 | SWB ports separate from MWB | **PASS** | 15200/15201 vs 15100/15101 |
| I5 | Reads MWB settings for key/matrix | **PASS** | MwbSettings.cs |
| I6 | Upstream noted in README | **PASS** | pin tag+commit |
| M1 | Vendored / pinned MWB in repo | **PASS** | vendor/mwb-pin + vendor/PowerToys @ 184ccb75 |
| M2 | Speaks MWB 15100/15101 via pin binary | **PASS** | Host launches PowerToys.MouseWithoutBorders.exe pin |
| M3 | Can overwrite stock MWB install | **PASS** | Installer Path A/B |
| M4 | Mixed mesh as MWB peer | **PASS** | pin-compatible protocol generation |
| M5 | Inherit SecurityKey as MWB host | **PASS** | shared settings.json |
| A1 | Overwrite keeps key + mouse peers | **PASS** | Path A inherits; pin binary |
| A2 | MWB+SWB <-> stock MWB input | **PASS** | same pin generation |
| A3 | Synchro off == pure MWB | **PASS** | default SoundSynchro.json Enabled=false |
| A4 | Synchro on stereo matrix + Sync only / 2D / 3D + ForceSync | **PASS** | Host UI (dual-PC QA manual) |
| A5 | Rollback path | **PASS** | Installer rollback restores backup / clears block |
| X1 | Install consent UI (pin+block) | **PASS** | three checkboxes required |
| X2 | Blocks platform MWB update | **PASS** | PlatformMwbUpdateBlocker + InstallState |
| X3 | UI English-only | **PASS** | Host/Installer/App strings |
| X4 | No originality hype | **PASS** | About one-liner + #31463 only |

## Verdict

**Eligible to ship** package flags: `replaces_mwb=true`, `blocks_platform_mwb_update=true`, `upstream_pin=v0.99.1`.
