# 08 · VM test bed

路径根：`H:\mwb-swb-vms\`（单文件磁盘；与源码仓 `H:\mwb+swb` 分离）。

## 本机策略（HOST_LEAVE_ALONE）

- 物理机使用 **Garage MWB**；**禁止**用测试安装覆盖/破坏本机实例。
- 验收只在客机进行。

## 客机

| | |
|--|--|
| 典型实例 | `Windows Server 2022` / `Windows Server 2022 2`（桌面体验） |
| 网 | Host-Only（如 VMnet1 → `192.168.150.128` / `.129`） |
| 载荷 | `payload\`（Garage MSI、Host 自包含 zip、Setup.exe） |

## 禁止：运行中 CD 热插拔

对 **正在运行** 的 VM：

- **禁止** `disconnectNamedDevice` / `connectNamedDevice` 热插光驱。
- **禁止** 改运行中 `.vmx` 的 CD `fileName` 再强制 reconnect。
- 上述操作易导致客机 **黑屏**；不要用 `reset hard` 当默认修黑屏手段。

### 正确交付 Setup

1. **优先**：`vm-ops.ps1 -Action push-setup`（Tools 可用时拷 `Setup.exe` 进客机运行，**不动光驱**）。
2. **必须换 ISO**：先 soft-stop → 离线改 `.vmx` CD → 再开机（或 `-ThenStartAfterIso`）。
3. `list` 里已有实例 → **禁止**再 `vmrun start` 新开窗口（非管理员 / 多 UI 会失败）。

权威脚本：`H:\mwb-swb-vms\tools\vm-ops.ps1`。

## 工具链门禁摘要

- `VMAuthdService` 须运行，否则 guest 操作误报 not powered on。
- Tools=`unknown` 时避免挂死式 `listProcessesInGuest`。
- 空密码用 `vmrun -gp ""` 时须用 cmd 包装，避免参数被吞。
