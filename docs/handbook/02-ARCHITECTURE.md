# 02 · Architecture

## Planes

```
+--------------------------------------------------+
|  Garage Mouse without Borders  (15100/15101)     |  <-- A轨：已有独立版
+----------------------+---------------------------+
|  MwbSwb.Host (tray)  |  SWB window (optional UI) |
|  follows Garage      |  TCP 15200 / UDP 15201    |
+----------------------+---------------------------+

B轨（可选覆盖安装）：
+--------------------------------------------------+
|  pin PowerToys.MouseWithoutBorders + Host        |
|  Installer Path A/B + PlatformMwbUpdateBlocker   |
+--------------------------------------------------+
```

| Plane | Ports | Role |
|-------|-------|------|
| MWB | TCP/UDP **15100/15101** | 键鼠/剪贴板；与 stock / Garage 同代际协议 |
| SWB control | TCP **15200** | HMAC(SecurityKey) 握手、能力探测、pose 交换、同 key 名探测 |
| SWB audio | UDP **15201** | 立体声 IEEE float PCM 帧 |

- **MWB plane**：始终优先；失败不归因于 SWB。
- **SWB plane**：探测失败 → `Swb=false`，**静默**继续纯 MWB。
- Settings：
  - MWB / Garage：各自既有存储（Garage 注册表 / PT `settings.json`）。
  - SWB：`%LOCALAPPDATA%\Microsoft\MWB-SWB\SoundSynchro.json`（默认 Enabled=false）。
  - 安装态（B 轨）：`InstallState.json`。

## Host 进程

- 托盘常驻；默认隐藏主状态窗。
- 检测 Garage 进程：出现 → 可自动开 SWB（可关窗）；消失 → 关 SWB 并退出 Host。
- 命令行 `/open-swb`：打开 SWB 并确保 Synchro 开启（供 Garage「Launch SWB」按钮与二次实例信令）。
- 自包含 publish（无本机 .NET 8 的 VM 用）。

## 模块

| 项目 | 职责 |
|------|------|
| `MwbSwb.Host` | 托盘、附件生命周期、SWB 窗、托盘双喇叭 |
| `MwbSwb.Core` | key/矩阵读取、握手、pose、Garage 设置 |
| `MwbSwb.Audio` | loopback / 12B PCM16 帧 / MatrixSynth / AEC 去重 / 7 档延时 / 电平 |
| `MwbSwb.Installer` | B 轨同意 UI、Path A/B、阻断更新、回滚 |
| `MwbSwb.App` | 早期旁路原型（优先用 Host） |
