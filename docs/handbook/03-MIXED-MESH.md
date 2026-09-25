# 03 · Mixed mesh

## Allowed

全 stock / 全 MWB+SWB / 任意混合；A 轨下为 **Garage ↔ Garage**，并可与仅 MWB 机混连。

## Behavior（输入 / 音频）

| Local \\ Remote | stock / Garage only | +Host Synchro off | +Host Synchro on |
|-----------------|---------------------|-------------------|------------------|
| stock / Garage only | MWB | MWB | MWB |
| +Host Synchro off | MWB | MWB | MWB |
| +Host Synchro on | MWB only（无音频） | MWB only | MWB + 立体声矩阵 |

- Stock / 纯 Garage **永不**解析 SWB 帧。
- Host **禁止**在 MWB socket 上塞 SWB 载荷。

## SWB 窗口观感

- **独立窗口**（不嵌进 MWB 主窗）。
- 与 **无 SWB 能力** 的对端混连时：SWB UI **灰屏/不可用音频矩阵**（键鼠仍走 MWB）。
- 多机矩阵里 **只列出具备 SWB 的电脑**；纯 MWB 对端不进发声矩阵。

## Probe

MWB 可用后（或并行）：向对端 `:15200` 发 SWB hello（HMAC SecurityKey）。失败 → `Swb=false`，继续 MWB-only。

握手可携带本机 **pose**（azimuth / elevation / radius），供对端 Upsert，**禁止**连接后按序重排角。
