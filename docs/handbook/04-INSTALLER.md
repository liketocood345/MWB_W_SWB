# 04 · Installer

## 分发形态

- **对外单文件**：`MWB-SWB-Setup.exe`（或 zip）可在正常 PC 直接安装；不要求用户先从 ISO 抽文件。
- **VM 测试**：可用 flat ISO / `push-setup` 拷入客机；**禁止**对开机 VM 热插拔光驱（见 [08](08-VM-TESTBED.md)）。
- 打包 ≠ 必须覆盖本机：本机 Garage 可继续只跑独立版；VM 内再装测试包。

## B 轨 · PowerToys pin 覆盖（产品路径）

安装 UI **强制英文同意勾选**（pin 版本 + 阻断 PT 更新 + 替换/降级备份）后才可安装。

1. Elevate；备份现有 MWB 相关文件与 settings。
2. 停止 PowerToys / MWB / Host。
3. 检测：
   - **Path A**：已有且为 pin **v0.99.1** → 替换关联 `*MouseWithoutBorders*`，继承 `settings.json`。
   - **Path B**：无或版本不对 → 部署 pin 载荷（可自动降级）+ Host。
4. 写入 `InstallState.json`（`replaces_mwb`、`blocks_platform_mwb_update`）。
5. `PlatformMwbUpdateBlocker`：禁用 PT MWB 模块 + 阻断标记。
6. `SoundSynchro.json` 默认 `Enabled=false`。

回滚 A5：还原 backup、恢复 PT 模块、清阻断与 InstallState。

## A 轨 · Garage 附件（当前验收）

- 载荷：`Garage\MouseWithoutBordersSetup.msi`（官方）+ 自包含 `Host\`；**不**把 PT pin 当默认运行时。
- Host 安装到用户目录（如 `%LOCALAPPDATA%\MWB-SWB-Host`），**不**覆盖物理机已装 Garage。
- Key **继承 / 用户在 Garage UI 输入**；禁止安装脚本「强行注入手写 key」作为默认手段。
- Garage **GeneratedKey 核验器**可在测试 VM 上打补丁：使「是否机器生成」检查恒为可接受，避免共享 key 被拒、重启刷新；与「不强行注入」同时遵守。

## Key 保留

覆盖或附件安装后须保留用户已设 SecurityKey；重启后不得因校验器误判而刷新 key（见聊天结论与 VM 补丁策略）。
