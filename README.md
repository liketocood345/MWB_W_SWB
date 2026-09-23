# MWB + SWB

External companion: Mouse Without Borders (MWB) + Sound Without Borders (SWB).

Does not modify PowerToys/MWB. Reads MWB peer matrix + SecurityKey, handshakes on LAN, then folds peer PCs into a stereo playback matrix over UDP.

## Requirements

- Windows 10 / 11
- .NET 8 Desktop Runtime
- PowerToys Mouse Without Borders already paired
- Firewall: TCP 15200, UDP 15201

## Use

1. Run this app on each PC.
2. Check **Sound Synchro**.
3. App loads `%LOCALAPPDATA%\Microsoft\PowerToys\MouseWithoutBorders\settings.json`.
4. HMAC handshake with the same SecurityKey (does not use MWB ports 15100/15101).
5. Toggle peers in the matrix; optionally send local loopback and/or mix remote stereo to local speakers.

## Build

```powershell
cd H:\mwb+swb
dotnet build -c Release
dotnet run --project src\MwbSwb.App -c Release
```

## Layout

| Project | Role |
|---------|------|
| MwbSwb.Core | MWB settings + SWB control handshake |
| MwbSwb.Audio | WASAPI loopback / mix / UDP stereo frames |
| MwbSwb.App | Sound Synchro UI + matrix |

## Notes

- Reuses MWB machine identity and shared key only; does not speak MWB input protocol.
- Audio is uncompressed stereo IEEE float (Opus later).

## License

MIT (this repo). Independent companion; not a Microsoft product.
