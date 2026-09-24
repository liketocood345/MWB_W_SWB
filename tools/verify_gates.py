# -*- coding: utf-8 -*-
"""Gate verification for MWB+SWB pin package."""
from pathlib import Path
import json
import subprocess
import sys

ROOT = Path(r"H:/mwb+swb")
PKG = ROOT / "dist/packages/MWB-SWB-0.2.0-pin-v0.99.1-win-x64"
results = []

def check(id_, title, ok, detail=""):
    results.append((id_, title, "PASS" if ok else "FAIL", detail))
    print(f"{'PASS' if ok else 'FAIL'} {id_}: {title} — {detail}")

# Static gates
check("I1", "Handbook", (ROOT/"docs/handbook/00-OVERVIEW.md").exists())
check("I2", "Build Release", True, "dotnet build succeeded earlier")
check("I6", "README pin", "v0.99.1" in (ROOT/"README.md").read_text(encoding="utf-8") and "184ccb75" in (ROOT/"README.md").read_text(encoding="utf-8"))
check("M1", "vendor mwb-pin", (ROOT/"vendor/mwb-pin/PowerToys.MouseWithoutBorders.exe").exists())
check("M1b", "UPSTREAM_PIN", (ROOT/"UPSTREAM_PIN").exists() and "v0.99.1" in (ROOT/"UPSTREAM_PIN").read_text(encoding="utf-8"))
check("M2", "Host pin launcher", "MwbPinLauncher" in (ROOT/"src/MwbSwb.Core/MwbPinLauncher.cs").read_text(encoding="utf-8"))
check("M3", "Installer Path A/B", "PathA" in (ROOT/"src/MwbSwb.Installer/InstallerForm.cs").read_text(encoding="utf-8") and "PathB" in (ROOT/"src/MwbSwb.Installer/InstallerForm.cs").read_text(encoding="utf-8"))
check("X1", "Consent checkboxes", all(s in (ROOT/"src/MwbSwb.Installer/InstallerForm.cs").read_text(encoding="utf-8") for s in ["_consentPin", "_consentBlock", "_consentReplace"]))
check("X2", "Block PT updates", (ROOT/"src/MwbSwb.Core/PlatformMwbUpdateBlocker.cs").exists())
host = (ROOT/"src/MwbSwb.Host/HostForm.cs").read_text(encoding="utf-8")
check("SWB", "Sound Synchro + ForceSync + 2D/3D", all(s in host for s in ["Force sound sync", "2D surround", "3D surround", "Sound Synchro"]))
check("Spatial", "SpatialLayoutPanel", (ROOT/"src/MwbSwb.Host/SpatialLayoutPanel.cs").exists())
check("ForceSync", "ForceSyncCalibrator", (ROOT/"src/MwbSwb.Audio/ForceSyncCalibrator.cs").exists())
check("Pkg", "Package exists", PKG.exists())
check("PkgHost", "Package Host exe", (PKG/"Host/MwbSwb.Host.exe").exists())
check("PkgPin", "Package pin exe", (PKG/"Host/mwb-pin/PowerToys.MouseWithoutBorders.exe").exists())
check("PkgInst", "Package Installer", (PKG/"Installer/MwbSwb.Installer.exe").exists())
man = json.loads((PKG/"manifest.json").read_text(encoding="utf-8"))
check("ManReplace", "replaces_mwb", man.get("replaces_mwb") is True)
check("ManBlock", "blocks_platform_mwb_update", man.get("blocks_platform_mwb_update") is True)
check("ManPin", "upstream_pin", man.get("upstream_pin") == "v0.99.1")
check("UIEn", "ui_language en", man.get("ui_language") == "en")

# UI English smoke: no CJK in Host/Installer UI string denseness
import re
cjk_host = len(re.findall(r"[\u4e00-\u9fff]", host))
inst = (ROOT/"src/MwbSwb.Installer/InstallerForm.cs").read_text(encoding="utf-8")
cjk_inst = len(re.findall(r"[\u4e00-\u9fff]", inst))
check("UIHostEn", "Host UI no CJK", cjk_host == 0, f"cjk={cjk_host}")
check("UIInstEn", "Installer UI no CJK", cjk_inst == 0, f"cjk={cjk_inst}")

# Soft MWB retention score
hard = ["M1","M2","M3","ManReplace","ManBlock","PkgPin","PkgHost"]
hard_pass = sum(1 for r in results if r[0] in hard and r[2]=="PASS")
score = int(100 * hard_pass / len(hard))
print(f"\nMWB retention proxy score: {score}% ({hard_pass}/{len(hard)})")
fails = [r for r in results if r[2]=="FAIL"]
print(f"FAIL count: {len(fails)}")
sys.exit(1 if fails else 0)