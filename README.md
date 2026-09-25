# MWB+SWB

## Disclaimer / 免责声明

1. **AI-assisted / AI-written add-on modules.** Substantial parts of this repository (including Sound Synchro / SWB and related tooling) were produced or heavily assisted by AI. Use at your own risk.
2. **For exchange and learning only.** Not a production-supported product; no liability for data loss or system damage (including overwrite/replace of Mouse Without Borders).

Upstream Mouse Without Borders remains Microsoft PowerToys (MIT).

Mouse Without Borders **+** Sound Without Borders: keep full MWB behavior, then optionally add LAN stereo sync.

Design handbook lives **in this repo**: [docs/handbook/](docs/handbook/README.md) (chapters 00-08; Garage attachment = current default track).

## Upstream (pinned)

| | |
|--|--|
| Upstream | https://github.com/microsoft/PowerToys |
| Module | `src/modules/MouseWithoutBorders/` |
| **Pin tag** | **v0.99.1** |
| **Pin commit** | **184ccb75ec85cc799d04555f34ffae968e3ba7c4** |
| License | MIT — Copyright (c) Microsoft Corporation |
| Docs | https://learn.microsoft.com/windows/powertoys/mouse-without-borders |

See [`UPSTREAM_PIN`](UPSTREAM_PIN) and [`THIRD_PARTY_NOTICES.md`](THIRD_PARTY_NOTICES.md). Mixed mesh with stock MWB must stay compatible with this pin generation. After install, **platform (PowerToys) MWB updates are blocked**.

MWB+SWB is **not** an official Microsoft release.

## Standalone MWB (Garage) vs PowerToys

This track targets **Microsoft Garage Mouse without Borders** (standalone), matching the host machine. Do **not** replace or fight the host Garage install. PowerToys-merged MWB pin remains archived under endor/mwb-pin but Host no longer auto-launches it.

VM test bed: H:\mwb-swb-vms\ (see README there).

## Product contract (summary)

- Installer **replaces** stock MWB (Path A) or does **fresh pin + auto-downgrade** (Path B); **inherits** SecurityKey / machine matrix.
- Mixed mesh with **stock MWB**: MWB-only behavior; audio only when both sides have SWB and Sound Synchro is on.
- SWB: **Sync only** / **2D ring** / **3D sphere** layout + optional **Force sound sync**; peer poses advertised over handshake (not reassigned on connect).
- Details: [docs/handbook/01-REQUIREMENTS.md](docs/handbook/01-REQUIREMENTS.md).

## Code status

| Piece | Status |
|-------|--------|
| Design handbook `docs/handbook/` | In-repo |
| Upstream pin | [`UPSTREAM_PIN`](UPSTREAM_PIN) (PowerToys MWB **v0.99.1**) |
| `MwbSwb.Host` | Garage attachment Host + Sound Synchro UI |
| `MwbSwb.Installer` | Consent UI, Path A/B, block PT MWB updates |
| `MwbSwb.App` | Legacy SWB prototype (prefer Host) |

Local `vendor/` / `dist/` trees are **not** published to GitHub (see `.gitignore`).

## Build

```powershell
cd H:\mwb+swb
dotnet build -c Release
dotnet run --project src\MwbSwb.Host -c Release
dotnet run --project src\MwbSwb.Installer -c Release
```

Windows 10/11 · .NET 8. SWB: TCP 15200 / UDP 15201. MWB remains 15100 / 15101.
