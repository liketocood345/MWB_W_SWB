# 06 · Verification

- Updated: 2026-09-24（单向连通修复 + 日志节流）
- 远程源码：https://github.com/liketocood345/MWB_W_SWB
- B 轨包名历史：`MWB-SWB-0.2.0-pin-v0.99.1-win-x64`
- A 轨验收：双 VM Host-Only + Garage + 自包含 Host

## Checklist

| ID | Title | Status | Detail |
|----|-------|--------|--------|
| I1 | Handbook present | **PASS** | docs/handbook 含 00–08 + 05b |
| I2 | Solution builds Release | **PASS** | Host/Installer/App |
| I3 | Self-contained Host publish | **PASS** | VM 无 .NET 8 可跑 |
| I4 | SWB ports ≠ MWB | **PASS** | 15200/15201 vs 15100/15101 |
| I5 | Reads Garage/MWB key | **PASS** | GarageMwbSettings / MwbSettings |
| I6 | README upstream pin | **PASS** | v0.99.1 + commit |
| M1 | Pin metadata in repo | **PASS** | UPSTREAM_PIN；vendor 不进 GH |
| G1 | Garage attachment lifecycle | **PASS** | Host 随 Garage 启停 |
| G2 | Launch SWB on Garage UI | **PASS** | Apply 与 Close 之间英文按钮 |
| G3 | Same-key name probe | **PASS** | 不改 MWB 互联核心 |
| G4 | Sync only + peer pose | **PASS** | 禁止连接后重排角 |
| G5 | Tray L=RX R=TX + echo gate | **PASS** | 代播不得双亮 |
| G6 | Tray ↑=TX ↓=RX | **PASS** | 中部上下箭头上传/下载直觉 |
| A4 | Stereo matrix dual-VM | **PARTIAL** | 需人工听感；链路 ESTABLISHED 已测 |
| A8 | 7-tier latency slider | **PASS** | TrackBar 0–6；默认 Balanced |
| A9 | AEC send dedup (fallback) | **PASS** | endpoint-fallback 时参考消回声 |
| A14 | Process loopback EXCLUDE self | **PASS** | 双 VM Server 2022：`loopback=exclude-self` + `MESH_OK`；native `SwbProcessLoopback.dll`；代播 TX 门控仍生效（exclude 时跳过 AEC） |
| A10 | 12B frame + stale ts | **PASS** | PCM16；秒级过时丢弃 |
| A11 | Latency budget doc | **PASS** | 05b 与实现同步维护 |
| A12 | Mesh retry + no beacon spam | **PASS** | 未确认对端 12s 重试；beacon 只记首见 |
| A13 | LogRare >=15s | **PASS** | 禁止 1s 级循环刷屏 |
| X3 | UI English-only | **PASS** | |
| X4 | No originality hype | **PASS** | |

## Verdict

- **A 轨（Garage 附件）**：可继续双 VM 验收与迭代。
- **B 轨（PT 覆盖包）**：历史闸门仍标记可出 `replaces_mwb` 包；与本机 Garage 验收 **分流**，勿混用。
