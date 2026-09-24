# 01 · Requirements

1. **Installer replaces MWB** (not a forever side-car only).
2. **Inherit** existing MWB SecurityKey and machine matrix; do not force re-pair just for SWB.
3. **P0: MWB integrity** — mouse/keyboard/clipboard/file (within upstream limits), layout, hotkeys, service options must work; SWB must not break MWB.
4. **Mixed mesh with stock MWB** — if remote has no SWB, behave as MWB only; any mix of {stock MWB, MWB+SWB} is allowed.
5. **SWB additive only** — Sound Synchro only when both sides have SWB and user enables it; do not occupy/corrupt MWB ports **15100/15101**.
6. **Stereo** — SWB peers on the sounding matrix must play stereo correctly.

## Acceptance

| ID | Check |
|----|--------|
| A1 | After overwrite install, same SecurityKey; mouse reaches old peers |
| A2 | MWB+SWB ↔ stock MWB: input/clipboard OK; no forced audio |
| A3 | Both MWB+SWB with Synchro off ≡ pure MWB |
| A4 | Both Synchro on: handshake + stereo matrix |
| A5 | Rollback/uninstall recoverable |
