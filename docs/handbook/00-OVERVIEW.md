# 00 · Overview

## Product

**MWB+SWB** = **Mouse Without Borders（MWB）优先** + 可选 **Sound Without Borders（SWB / Sound Synchro）**。

- **观感定位**：SWB 是 **MWB 附件（attachment）**——MWB 先跑；Host/SWB **随 MWB 启停**；用户可主动关掉 SWB 窗口，Host 仍可在托盘里重开（只要 MWB 还在）。
- SWB 仅在对端也有 SWB 且用户打开 Synchro 时传声；否则会话等同纯 MWB。
- 不修改 MWB 协议端口 **15100/15101**；SWB 使用 **TCP 15200 / UDP 15201**。

对外口径克制：不强调原创；关于页至多一句——社区长期喊跨机传声，PowerToys 未做（[#31463](https://github.com/microsoft/PowerToys/issues/31463)），故自行实现。

免责：仓库大量模块为 AI 辅助；仅供交流学习（见根 README）。

## 双轨（必须分清）

| 轨 | 何时用 | MWB 由谁提供 | Host 行为 |
|----|--------|--------------|-----------|
| **A · Garage 附件（当前默认 / 本机与 VM 验收轨）** | 物理机与测试 VM 使用 **Microsoft Garage Mouse without Borders** 独立版 | 系统已装的 Garage | Host **不替换、不启动 PT pin**；读 Garage key；跟随 Garage 进程生命周期 |
| **B · PowerToys pin 覆盖轨** | 需要「安装包覆盖 PT MWB」的产品路径 | 仓内钉死 pin **v0.99.1** / `184ccb75…` | Installer Path A/B + `PlatformMwbUpdateBlocker` |

本机正在跑的 Garage **禁止被测试/安装流程改坏**（见 [04](04-INSTALLER.md)、[08](08-VM-TESTBED.md)）。

仓内权威 pin 元数据：`UPSTREAM_PIN`、`THIRD_PARTY_NOTICES.md`；`vendor/` 不进 GitHub。

## Phases

| Phase | Status |
|-------|--------|
| P0 Handbook + SWB 旁路原型 | Done |
| P1 Host + SWB UI（2D/3D/Sync only + ForceSync） | Done |
| P2 Installer Path A/B + 阻断 PT 更新 | Done（B 轨） |
| P2b Garage 附件轨 + Launch SWB + 同 key 发现 | Done（A 轨，当前验收） |
| P3 托盘双喇叭电平 + 回声门控 | Done |
| P4 Audio hardening（7 档延时 / MatrixSynth / AEC 去重 / 12B 秒戳帧） | Done |
