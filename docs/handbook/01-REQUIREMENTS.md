# 01 · Requirements

## 硬需求（跨轨）

1. **MWB 完整性优先** — 键鼠/剪贴板/小文件、布局与热键必须可用；SWB 不得打断 MWB。
2. **继承 SecurityKey / 机器表** — 不为 SWB 强迫用户重配对；不默认改写 SecurityKey。
3. **混连** — 任意混合 {stock MWB, MWB+SWB / Garage+Host}；无 SWB 对端 → 纯 MWB。
4. **SWB 叠加** — 仅双方有 SWB 且 Synchro 开启时传声；**禁止**占用/污染 MWB **15100/15101**。
5. **立体声** — 矩阵内对端以立体声 float PCM 播放/混音。
6. **UI 英文** — Host / Installer / Garage 上补丁按钮文案英文；手册可中文。
7. **附件生命周期（A 轨）** — MWB 优先；Host 随 Garage 启停；SWB 窗口可关、可由托盘或「Launch SWB」重开。

## Garage 附件轨补充

8. **不伤害本机 Garage** — 验收与打包不得覆盖物理机正在使用的 MWB 独立版。
9. **同 key 发现设备名** — 尽量不改 MWB 互联逻辑；用同 SecurityKey 下的主动探测/拒连获取对端主机名（见 [07](07-GARAGE-ATTACHMENT.md)）。
10. **自动填充矩阵** — 仅对「完全相同的名字」生效一次；不覆盖用户手填；优先启用未启用槽位；不改已有排序；过多同 key 设备 → **拒填并提示**；可清理矩阵中 IP 表已无的历史名。

## PowerToys 覆盖轨补充

11. **Installer 覆盖 MWB** — Path A 同 pin 替换关联文件；Path B 无/版本不对则部署 pin（可自动降级）+ Host；安装 UI 强制同意（pin + 阻断更新 + 备份）。
12. **安装后阻断** PowerToys 对 MWB 的平台升级。

## Acceptance

| ID | Check |
|----|--------|
| A1 | 覆盖/继承后 SecurityKey 仍可用；键鼠达旧对端 |
| A2 | MWB+SWB ↔ stock MWB：输入/剪贴板 OK；无强制音频 |
| A3 | Synchro 关 ≡ 纯 MWB |
| A4 | 双方 Synchro 开：握手 + 立体声矩阵（含 Sync only / 2D / 3D） |
| A5 | 回滚/卸载可恢复（B 轨） |
| A6 | A 轨：Garage 运行时 Host 跟随；Garage 退出 Host 退出 |
| A7 | 仅代播他机时托盘左侧可亮、右侧必须灭 |
