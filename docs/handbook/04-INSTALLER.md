# 04 · Installer

安装 UI **强制英文同意勾选**（pin + 阻断 PT 更新 + 替换/降级备份）后才可安装。

1. Elevate；备份现有 MWB 相关文件与 settings。
2. 停止 PowerToys / MWB / Host 进程。
3. 检测：
   - **Path A**：已有且版本为 pin（v0.99.1）→ 替换关联 `*MouseWithoutBorders*` 文件，继承 `settings.json`。
   - **Path B**：无 MWB 或版本不对 → 部署 pin 载荷（必要时对现有文件做自动降级替换）+ 全新 Host。
4. 写入 `InstallState.json`（`replaces_mwb=true`、`blocks_platform_mwb_update=true`）。
5. `PlatformMwbUpdateBlocker`：禁用 PT MWB 模块 + `block-pt-mwb-update` 标记。
6. `SoundSynchro.json` **默认 Enabled=false**。

回滚 A5：从 backup 还原 MWB 文件、恢复 PT 模块、清除阻断标记与 InstallState。
