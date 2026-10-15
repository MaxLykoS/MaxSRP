# Codex instructions

## Ignored directories

- Treat `/Library/` (Unity-generated cache) as ignored. Do not enumerate, scan, search, read, or modify files under it unless the user explicitly asks to work on that directory.
- When searching from the project root, explicitly exclude `/Library/`, including when using options that bypass ignore files. For ripgrep, use `-g '!/Library/**'`.
- Scope recursive operations to relevant source directories or prune `/Library/` before descending into it.
