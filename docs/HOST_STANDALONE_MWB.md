# Host machine policy — DO NOT TOUCH standalone MWB

## Fact (2026-09-23 audit)

- Standalone: Microsoft Garage Mouse without Borders **2.2.1.0327**
  Path: `C:\Program Files (x86)\Microsoft Garage\Mouse without Borders`
  Status: **running**, listening **15100/15101** (healthy peer link observed)
- PowerToys-merged MWB module: **disabled** (`enabled.MouseWithoutBorders=false`) — leave disabled so it cannot fight Garage for ports
- MWB+SWB overwrite installer (**InstallState**) was **never** applied on this host
- Garage install files timestamp 2021-03-23 — not overwritten by our harvest/package work

## Rules

1. Never install/replace/stop Garage MWB on the host for MWB+SWB work.
2. Never enable PowerToys MouseWithoutBorders on the host while Garage is in use.
3. All MWB+SWB program verification happens **only inside VMs** under `H:\mwb-swb-vms\`.
4. VM guests install Garage MSI from `H:\mwb-swb-vms\payload\MouseWithoutBordersSetup.msi` + SWB Host package; do not use PowerToys MWB pin there unless a future design explicitly switches (not this track).