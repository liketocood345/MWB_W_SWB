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

- 载荷：`Garage\MouseWithoutBordersSetup.msi`（官方）+ **自包含** `Host\`（`includedFrameworks`，**不**探测本机 .NET 8）；可选 `Prereqs\` 离线包保留但不作为安装前置探测。
- **依赖**：Garage MSI 自身仍要求本机有 .NET Framework 4.x（Win10/11 通常已有）。Setup **不再**运行「Checking .NET prerequisites」探测/安装 .NET 8。
- Host 安装到 `%LOCALAPPDATA%\MWB-SWB-Host` 与 `C:\Users\Public\MWB-SWB-Host`（stub 两处都能找到 `MwbSwb.Host.exe`）。
- **覆盖顺序**：**直接**结束进程后覆盖 Host → Garage MSI → wrap stub；关键拷贝/MSI/wrap 在 Defender 拦截时 **最长重试约 15 分钟**（状态栏提示 Allow），再关 OneWay/SAW、改快捷方式并拉起。wrap 后校验 `MouseWithoutBorders.exe` / `.original.exe` 存在。`msiexec` 非 0/3010 且 Garage exe 不存在 → **失败退出**。
- **覆盖 Garage 启动入口**：安装后把官方 `MouseWithoutBorders.exe` 备份为 `MouseWithoutBorders.original.exe`，再写入小型 **MWB+SWB stub**（同名 exe）。因此桌面快捷方式、开始菜单、搜索/应用列表等**任何**启动 MWB 的路径都会先开 Garage 再开 SWB Host（`/open-swb`）。
- Key **继承 / 用户在 Garage UI 输入**；禁止安装脚本「强行注入手写 key」作为默认手段。
- Garage **GeneratedKey 核验器**可在测试 VM 上打补丁：使「是否机器生成」检查恒为可接受，避免共享 key 被拒、重启刷新；与「不强行注入」同时遵守。

## Key 保留

覆盖或附件安装后须保留用户已设 SecurityKey；重启后不得因校验器误判而刷新 key（见聊天结论与 VM 补丁策略）。

易复发坑（SAW、同钥、自包含 Host、stub）：[10-BUG-HOTSPOTS.md](10-BUG-HOTSPOTS.md)。
