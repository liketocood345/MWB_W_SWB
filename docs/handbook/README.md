# MWB+SWB 设计手册

**只在本仓库内维护**，不另开设计仓。权威优先级：**本手册 > 口头讨论 / 聊天**。改玩法、安装契约、生命周期、托盘语义 → **先改手册再改代码**。

UI 字符串（Host / Installer / 补丁按钮）**纯英文**；手册与说明文档可用中文。

| 文档 | 内容 |
|------|------|
| [00-OVERVIEW.md](00-OVERVIEW.md) | 产品定位、双轨（Garage / PowerToys pin）、阶段 |
| [01-REQUIREMENTS.md](01-REQUIREMENTS.md) | 需求口径与验收表 |
| [02-ARCHITECTURE.md](02-ARCHITECTURE.md) | 分层、端口、附件生命周期 |
| [03-MIXED-MESH.md](03-MIXED-MESH.md) | 混连、灰屏、仅 SWB 进矩阵 |
| [04-INSTALLER.md](04-INSTALLER.md) | 安装包、同意、Path A/B、本机勿动 |
| [05-SOUND-SYNCHRO.md](05-SOUND-SYNCHRO.md) | Sound Synchro、7 档延时、pose、托盘箭头 |
| [05b-LATENCY-BUDGET.md](05b-LATENCY-BUDGET.md) | 发声→播放全链路耗时预算（技术改动必同步） |
| [06-VERIFICATION.md](06-VERIFICATION.md) | 闸门与保留度 |
| [07-GARAGE-ATTACHMENT.md](07-GARAGE-ATTACHMENT.md) | **当前默认轨**：Garage MWB 附件、同 key 发现、Launch SWB |
| [08-VM-TESTBED.md](08-VM-TESTBED.md) | 双 VM 测试床、禁止 CD 热插、交付方式 |
| [09-TEST-REPORT-20260925-17h.md](09-TEST-REPORT-20260925-17h.md) | 本机播放设备 + 双 VM 功能/延时报告（小时戳；隐私代称） |

远程仓（源码）：https://github.com/liketocood345/MWB_W_SWB  
上游 MWB（MIT）：Microsoft PowerToys / Garage Mouse without Borders。

