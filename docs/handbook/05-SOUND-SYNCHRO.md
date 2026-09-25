# 05 · Sound Synchro

## 流程

1. 读取本地 Garage / MWB SecurityKey（及可选机器表提示）。
2. SWB 握手仅面向同密钥对端；失败 **静默纯 MWB**。
3. 成功对端进入发声矩阵（可勾选包含/排除）。
4. 可选：本机 WASAPI loopback → 对端；对端流 → 本机 **MatrixSynth 单总线** 混音后输出。
5. **本机发声设备**即使尚无对端，也必须出现在矩阵/列表（至少显示本机扬声器）。

传输：UDP **15201**（控制 TCP **15200**），与 MWB **15100/15101** 分离。

音频帧：固定 **12 字节头**（magic `SW` / tier / rate / plc / seq / **Unix 秒戳** / CRC16）+ **PCM16 立体声**。身份靠 UDP 源 IP。过时丢弃默认 age &gt; **30s**（兼容 VM 时钟偏斜；物理 LAN 可更严）。

## 7 档延时滑块（Latency）

UI：英文 TrackBar 0–6，左偏同步、右偏低延；默认 **3 Balanced**。持久化 `AudioTier`（旧 `ForceSoundSync=true` → tier 0）。**界面字符串纯英文**（档位下拉只显示 EnglishName；中文名仅手册用）。

| 档 | UI 英文 | 手册中文 | 帧长 T | RTT 水位对齐 | PLC / 24k |
|----|---------|----------|--------|--------------|-----------|
| 0 | Force sync | 强制同步 | 20 ms | 满校准 | 否 |
| 1 | Firm sync | 稳同步 | 16 ms | ×0.7 | 否 |
| 2 | Soft sync | 柔同步 | 12 ms | ×0.35 | 否 |
| 3 | Balanced | 平衡 | 10 ms | 否 | 否 |
| 4 | Responsive | 迅响 | 8 ms | 否 | 否 |
| 5 | Near realtime | 近实时 | 6 ms | 否 | 轻 PLC |
| 6 | Ultra-low | 超低延时 | 4 ms | 否 | PLC / 可 24k |

### 本机播放设备（独立下拉 · Local playback）

与延时档、Send/Recv **并列独立**，不影响矩阵勾选。

| UI（英文） | 持久化 `LocalPlaybackDeviceId` | 行为 |
|------------|--------------------------------|------|
| **All devices**（首项） | `*` 或空 | 对所有 **Active** 渲染端点各开一路 Shared `WasapiOut`；主设备拉 `MatrixSynth`，其余由 fan-out 缓冲跟播 |
| 具体设备 FriendlyName | 该端点 `MMDevice.ID` | 仅在该设备播放（Shared；t5/t6 仍可对该设备尝试 Exclusive） |

枚举：`MMDeviceEnumerator` · `DataFlow.Render` · `DeviceState.Active`。设备插拔后可重开 SWB / 重选下拉以刷新列表。JSON：`SoundSynchro.json`。

两端 `tier` 不一致时仍接收（日志提示对齐滑块）；对齐用水位偏移，**禁止**每包 `Task.Delay`。

### 强制同步（t0）真阻断

- **Sticky 启动门**：`JitterRing` 在水位（40–60ms）+ 校准 Δ 填满前输出静音；填满一次后 `_primed`，之后才按 underrun/PLC 出声。
- **对端屏障**：`MatrixSync` 在 `SyncAlignStrength≥1` 时，所有已收到数据的 peer 均 primed 才混音出声（超时 1.5s 放弃，避免永久静音）。
- **Δ**：`ForceSyncCalibrator` = `MaxRTT − RTT(peer)`；`|Δ| &lt; ToleranceMs` 则视为 0。探针失败 → 仅本地水位阻断，日志说明无 Δ。

t5/t6 播放优先 **WASAPI Exclusive**（失败回退 Shared）。阶段耗时见 `C:\Users\Public\swb-latency-stages.log`（QPC：cap→rx→first audible）。

全链路耗时权威表见 **[05b-LATENCY-BUDGET.md](05b-LATENCY-BUDGET.md)**（技术改动必须同步更新）。

## 回声去除

Send+Recv 同时开时，采集路径按优先级：

1. **主路径（Win10 Build ≥20348 / Server 2022）**：进程内录 `PROCESS_LOOPBACK_MODE_EXCLUDE_TARGET_PROCESS_TREE`，`TargetProcessId` = Host PID。内录仍为其它进程系统声，但**不含**本进程 `WasapiOut` 混出的对端声，电气环路在源头断开。日志：`loopback=exclude-self`。此模式下跳过参考减/整帧去重（避免误伤本机游戏声）。
2. **回退**：全量 endpoint `WasapiLoopbackCapture`；播放总线写入参考环；loopback 减去延迟参考；残差低则 **整帧不发送**。日志：`loopback=endpoint-fallback`。

托盘 TX 读**实际上行**电平（exclude 下为本机其它进程声；fallback 下去重后残差）。

参考：Microsoft [ApplicationLoopback](https://github.com/microsoft/Windows-classic-samples/tree/main/Samples/ApplicationLoopback)。

## 布局模式（三选一）

| 模式 | UI | 行为 |
|------|-----|------|
| **Sync only（单纯同步声音）** | 无环绕舞台（提示文案） | 各路立体声 **等增益中置**；不做方位 pan / 距离衰减 |
| **2D ring** | 环 + 环上设备点 | **拖动点**改变方位角 |
| **3D sphere** | 球面点 + **参考法平面** | **拖空白**改观察视角；**选中点再拖**改方位/仰角 |

布局写入 `%LOCALAPPDATA%\Microsoft\MWB-SWB\SoundSynchro.json`（含 `AudioTier` / pose / `SpatialMode`）。

### 初始位置：各机自报，禁止连接后重排

- 每机本地 JSON 保存 **本机** pose（`HostName`）。
- 握手 / name-probe 携带 `azimuth` / `elevation` / `radius`。
- 对端 **Upsert**；**禁止**按「连接顺序 × 60°」重分配。

MVP：方位 pan + 可选距离衰减；完整 HRTF 不进本阶段。

## 托盘图标（双喇叭 + 上下箭头）

开源自绘（Win10 音量喇叭造型，**非**微软资源）：两喇叭 **背对背**；中部 **上↑=发送/上传(TX)**、**下↓=接收/下载(RX)**。

| 元素 | 含义 | 点亮 |
|------|------|------|
| **左**喇叭弧 | 仅接收 | RX 电平 |
| **右**喇叭弧 | 仅发送 | 去重后 TX 电平 |
| **↑** | 上传 / TX | 同右 |
| **↓** | 下载 / RX | 同左 |

硬规则：只代播他机、本机未另发声时，**右弧与 ↑ 必须灭**（左/↓ 可亮）。约 200ms 刷新。

悬停：`SWB  RX↓: n/3  |  TX↑: n/3`。
