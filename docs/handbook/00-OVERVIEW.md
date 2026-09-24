# 00 · Overview

## Product

**MWB+SWB** = full **Mouse Without Borders (MWB)** + optional **Sound Without Borders (SWB)**。

- 优先保证：一切 MWB 功能可用，并能与 **原版 MWB** 混连。
- SWB（Sound Synchro）仅在对端也有 SWB 时生效；否则会话等同纯 MWB。

## Upstream（唯一钉死）

| | |
|--|--|
| Upstream repo | https://github.com/microsoft/PowerToys |
| Upstream module | src/modules/MouseWithoutBorders/ |
| **Pin** | **v0.99.1** / `184ccb75ec85cc799d04555f34ffae968e3ba7c4` |
| License | MIT (Copyright Microsoft Corporation) |

仓内权威：`UPSTREAM_PIN`、`vendor/mwb-pin/`、`vendor/PowerToys/`（sparse）。安装后阻断 PowerToys 对 MWB 的升级。

对外口径克制：不强调原创；关于页至多一句——社区长期喊跨机传声，PowerToys 未做（[#31463](https://github.com/microsoft/PowerToys/issues/31463)），故自行实现。

## Phases

| Phase | Status |
|-------|--------|
| P0 Handbook + SWB additive prototype | Done |
| P1 Replaceable MWB core（钉死 pin + Host） | Done |
| P2 Installer 覆盖安装 Path A/B + 阻断更新 | Done |
| P3 Audio hardening | Not started |
