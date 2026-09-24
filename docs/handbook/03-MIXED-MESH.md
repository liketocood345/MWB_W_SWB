# 03 · Mixed mesh

## Allowed

All stock / all MWB+SWB / any mix.

## Behavior

| Local \\ Remote | stock MWB | MWB+SWB (Synchro off) | MWB+SWB (Synchro on) |
|-----------------|-----------|------------------------|----------------------|
| stock MWB | MWB | MWB | MWB |
| MWB+SWB Synchro off | MWB | MWB | MWB |
| MWB+SWB Synchro on | MWB only | MWB only | MWB + stereo matrix |

Stock MWB never parses SWB frames. MWB+SWB must not send SWB payloads on MWB sockets.

## Probe

After MWB is usable (or in parallel): SWB hello to peer:15200 with HMAC(SecurityKey). Fail → Swb=false, continue MWB-only.
