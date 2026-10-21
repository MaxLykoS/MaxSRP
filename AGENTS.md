# Codex instructions

## Ignored directories

- Treat `/Library/` (Unity-generated cache) as ignored. Do not enumerate, scan, search, read, or modify files under it unless the user explicitly asks to work on that directory.
- When searching from the project root, explicitly exclude `/Library/`, including when using options that bypass ignore files. For ripgrep, use `-g '!/Library/**'`.
- Scope recursive operations to relevant source directories or prune `/Library/` before descending into it.

## Rendering contracts

- Before changing camera matrices, render passes, GBuffer layouts, vertex animation, shadows, atmosphere, or post-processing, read `Documentation/TemporalAA.md` and preserve its jitter, velocity, HDR, history-lifecycle, and pass-order contracts.
- TAA is shared pipeline infrastructure. New effects must integrate with that contract; do not introduce a second TAA/history resolve without an explicit design decision.
