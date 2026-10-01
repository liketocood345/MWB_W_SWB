# 07 · Garage attachment（当前默认轨）

## 定位

SWB Host 是 **Garage Mouse without Borders 的附件**：

1. **MWB 优先** — 键鼠永远由 Garage 负责；SWB **只借用** SecurityKey/同钥发现建立旁路连接，**不占用** MWB 的 15100/15101，传声不得拖死点击/键鼠 UDP。
2. **随 MWB 启停** — Host 监视 Garage 进程；Garage 起 → 附件可用（Synchro 继承上次开关）；Garage 止 → 经启动 hold / 加长 grace 后 Host 退出（避免 stub 间隙闪退）。
3. **可关窗** — 用户可关 SWB 窗口；Host 留在托盘；可「Open SWB window」或 Garage 上 **Launch SWB** 再开。
4. **不独立当 MWB** — A 轨 Host **不**启动 `vendor/mwb-pin` 替代 Garage。
5. **不引入原版不该有的故障** — 附件启动路径若走到用户态，须关掉 Garage 默认 `OneWayControlMode`（SAW），避免远程键鼠被单向拦截；SWB HMAC **跟 Garage MyKey/SecurityKey**，不得优先另造 lab `shared-key.txt` 与 MWB 脱节。

## 桌面 / 开始菜单启动

覆盖安装后，「Mouse without Borders」桌面与开始菜单快捷方式应指向 **`LaunchMwbWithSwb.vbs`**（先 Garage，再 Host）。  
若仍只打开纯 MWB：说明快捷方式未被改写（常见于未跑完 Setup，或 MSI 广告快捷方式又被修复）——重新运行 `MWB-SWB-Setup.exe`，或手动执行 `%LOCALAPPDATA%\MWB-SWB-Host\RetargetMwbShortcuts.ps1`。

原快捷方式备份：同名 `.lnk.mwb-swb-bak`。

## Launch SWB

- Garage 主界面英文按钮 **Launch SWB**。
- 位置：**Apply** 与 **Close** 之间（不是虚构的 OK）。
- 行为：拉起/激活 Host，并 `/open-swb`（打开 SWB 窗；**Sound Synchro 开关继承**上次 `SoundSynchro.json` 的 `Enabled`，不再强制打开）。

实现：对 Garage `MouseWithoutBorders.exe` 的 UI 补丁（测试 VM）；本机 Garage 是否打补丁由用户明确决定，默认 **勿动本机**。

## 同 SecurityKey 设备名发现

目标：key 相同时自动拿到对端机器名，**尽量不改 MWB 互联逻辑**。

- 走 SWB 控制面（15200）或同 key 下的主动探测/拒连旁路获取名字。
- **自动填充计算机矩阵规则**：
  - 仅当名字与已有项 **完全相同** 时视为「已处理过一次」，避免反复填写同一内容。
  - **不覆盖**用户主动填写的名字。
  - 未获取到任何名字 → **不填任何内容**。
  - 优先启用 **尚未启用** 的槽位。
  - **不改变**已有矩阵排序。
  - 同 key 设备数超过 MWB 理论互联上限 → **拒填并提示过多**。
  - 可选：清掉矩阵里历史遗留但当前 IP/名表已不存在的项。

## Key 与 GeneratedKey 校验

现象：共享 key + Apply 后连不上；重启后 key 被刷新。

对策（测试环境）：

1. **无效化**「是否机器生成 key」核验器（无论检查什么都视为可用 New 生成类 key）。
2. **不**把「强制注入手写 key」当默认手段。

本机生产 Garage 是否应用同类补丁须单独批准。

## 音频矩阵可见性

- 无对端时也要显示 **本机扬声器/发声设备**。
- 仅 SWB 能力对端进入可传声矩阵；纯 MWB 对端不进。

## Launch SWB 解析路径

按钮按优先级查找 Host（找不到则提示）：

1. `C:\Users\Public\MWB-SWB-Host\MwbSwb.Host.exe`（机器级，推荐部署副本）
2. `%LOCALAPPDATA%\MWB-SWB-Host\MwbSwb.Host.exe`
3. `C:\Users\Public\MWB-SWB-Host\LaunchSwb.cmd` / `C:\Users\Public\LaunchSwb.cmd`

二次启动已运行的 Host 时通过命名事件 + `open-swb.pulse` 通知打开 SWB 窗。

相关易复发问题（SAW、同钥、找不到 Host exe）：[10-BUG-HOTSPOTS.md](10-BUG-HOTSPOTS.md)。
