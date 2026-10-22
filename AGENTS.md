# Codex instructions

## Ignored directories

- Treat `/Library/` (Unity-generated cache) as ignored. Do not enumerate, scan, search, read, or modify files under it unless the user explicitly asks to work on that directory.
- When searching from the project root, explicitly exclude `/Library/`, including when using options that bypass ignore files. For ripgrep, use `-g '!/Library/**'`.
- Scope recursive operations to relevant source directories or prune `/Library/` before descending into it.

## Bistro asset versioning

- Keep downloaded Bistro models, textures, and other third-party source payloads under `Assets/ThirdParty/Bistro/`. These files are local dependencies and must not be committed or pushed, including with `git add -f`.
- Commit all Unity `.meta` files for that directory and its contents, including folder `.meta` files, to preserve GUIDs and importer settings. The `.gitignore` directory exceptions are intentional and must remain so nested `.meta` files can be tracked.
- Keep the project-authored test scene, materials, lighting configuration, and related setup under `Assets/Scenes/BistroTest/`; commit these assets and their `.meta` files normally. Do not move third-party source payloads into this directory to bypass the ignore rule.
- Before a commit or push involving Bistro, verify that staged files under `Assets/ThirdParty/Bistro/` are `.meta` files only. Preserve the source-payload ignore rules in later changes.

## Rendering contracts

- Before changing camera matrices, render passes, GBuffer layouts, vertex animation, shadows, atmosphere, or post-processing, read `Documentation/TemporalAA.md` and preserve its jitter, velocity, HDR, history-lifecycle, and pass-order contracts.
- TAA is shared pipeline infrastructure. New effects must integrate with that contract; do not introduce a second TAA/history resolve without an explicit design decision.
