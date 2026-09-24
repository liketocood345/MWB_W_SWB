# 02 · Architecture

`
+---------------------------------------------+
|  MWB+SWB (installer replaces stock MWB)     |
+----------------------+----------------------+
|  MWB core (upstream  |  SWB (optional)      |
|  fork) 15100/15101   |  TCP 15200/UDP 15201 |
+----------------------+----------------------+
         ^                        ^
         | same SecurityKey       | only if peer advertises SWB
    stock MWB peers          MWB+SWB peers
`

- **MWB plane**: always on; compatible with upstream Mouse Without Borders peers.
- **SWB plane**: capability probe; on failure skip silently; never fail the MWB session.
- Settings: share MWB settings.json; SWB flags in side file (e.g. SoundSynchro.json); do not rewrite SecurityKey by default.
