# HTML UI Raster Parity Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [x]`) syntax for tracking.

**Goal:** Finish the eight HTML-matched menu screens with PNG-only UI assets, authoritative game data, development notifications for unsupported controls, and sharper rendering.

**Architecture:** Keep `MenuScreenController` and the existing per-screen components as the runtime boundary. Rasterize the supplied SVG art deterministically, update both HTML and Unity paths, then regenerate the complete UGUI hierarchy through `BuildMenuCanvas` so the scene remains reproducible.

**Tech Stack:** Unity 6, C#, legacy UGUI, bundled Roboto fonts, Chromium headless SVG rasterization, PowerShell asset verification.

**Spec:** `docs/superpowers/specs/2026-09-18-eight-screen-html-ui-redesign.md`

## Global Constraints

- Change only the eight menu screens represented by the HTML pack; do not change in-game HUD/gameplay scenes.
- Never copy prototype room, ping, player, level, invite-code, map, or team mock data into runtime UI.
- Unsupported controls display exactly `Tính năng đang được phát triển`.
- Do not build the player and do not run the full Unity or .NET test suites.
- Preserve existing multiplayer, practice, settings, authentication, chat, ready, and team-switch behavior.

---

### Task 1: PNG-only asset pipeline

**Files:**
- Create: `tools/convert-ui-svg-to-png.ps1`
- Create: PNG counterparts under `ui-pack/assets/`
- Modify: `ui-pack/index.html`
- Modify: `ui-pack/ASSET_MANIFEST.md`
- Replace: SVG files under `Ironfront_Reborn/Assets/UI/IronfrontReborn/` with PNG files
- Modify: `Ironfront_Reborn/Assets/Editor/NetVerification/IronfrontRebornUiAssetCatalog.cs`

**Interfaces:**
- Produces: deterministic transparent PNGs at stable relative paths.
- Produces: `IronfrontRebornUiAssetCatalog.Sprite(string relativePath)` backed only by `TextureImporter` sprites.

- [x] Add a focused verification script assertion that the pack and Unity menu trees contain no SVG and every HTML image source exists; confirm it fails before conversion.
- [x] Implement Chromium-headless conversion using each SVG viewBox, 4x resolution for icons and 2x resolution for larger surfaces, replacing `currentColor` with white for tintable icons.
- [x] Convert, update HTML/manifest/catalog references, and copy the PNGs into Unity; retain SVGs only as unused source masters.
- [x] Run only the focused asset verification and confirm it passes.

### Task 2: Sharp UGUI rendering

**Files:**
- Modify: `Ironfront_Reborn/Assets/Editor/NetVerification/IronfrontRebornUiAssetCatalog.cs`
- Modify: `Ironfront_Reborn/Assets/Editor/NetVerification/BuildMenuCanvas.cs`
- Modify: generated PNG `.meta` files through Unity import

**Interfaces:**
- Produces: uncompressed, mipmap-free sprites and a 1920x1080 pixel-perfect Canvas.
- Produces: Roboto-backed `Text` elements through `DefaultFont()` and `BoldFont()`.

- [x] Add focused source assertions for PNG-only catalogue paths, no texture compression, pixel-perfect Canvas, and bundled Roboto font loading; confirm they fail.
- [x] Configure icon and surface importers for transparent full-rect sprites without compression or mipmaps.
- [x] Set the generated Canvas pixel-perfect and replace the legacy built-in font with bundled Roboto Regular/Bold assets.
- [x] Run only the focused source assertions and confirm they pass.

### Task 3: Complete visual and runtime parity

**Files:**
- Modify: `Ironfront_Reborn/Assets/Editor/NetVerification/BuildMenuCanvas.cs`
- Modify: `Ironfront_Reborn/Assets/Scripts/Net/Client/Menu/MenuCreateRoomScreen.cs`
- Modify: `Ironfront_Reborn/Assets/Scripts/Net/Client/Menu/MenuRoomLobbyScreen.cs`
- Modify only as required: other existing `Menu*Screen.cs` files
- Modify: `Ironfront_Reborn/Assets/Scenes/Menu.unity`

**Interfaces:**
- Consumes: real `MapCatalog`, `RoomInfo[]`, `RoomState`, settings bridge, practice launcher, and authentication/session operations.
- Produces: eight HTML-layout screen roots with no fabricated runtime values.

- [x] Add focused assertions covering the eight roots, HTML section names, development-toast controls, create-room preview values, settings category layout, and two-team waiting-room layout; confirm missing parity fails.
- [x] Finish the settings category navigation, create-room preview statistics, and waiting-room versus layout using existing serialized runtime fields.
- [x] Audit every HTML control: bind it to real behavior/data or route it to `MenuToast.ShowDevelopment()`.
- [x] Regenerate `Menu.unity` with the authoring command only; do not create a player build.
- [x] Run focused assertions, `git diff --check`, and a missing-reference/path scan.

### Task 4: Lean verification and handoff

**Files:**
- Modify: `ui-pack/status.html`
- Modify: `ui-pack/README.md`

**Interfaces:**
- Produces: an accurate screen-by-screen support matrix and manual build/test handoff.

- [x] Update documentation to describe PNG assets, real-data substitutions, unsupported controls, and the deliberate absence of gameplay-HUD work.
- [x] Record that the repository has 65 tracked test-path files and the latest full EditMode artifact contains 183 cases; do not delete unrelated protection tests.
- [x] Run only the asset/source/scene checks from Tasks 1-3 and inspect the final diff.
- [x] Commit the completed menu parity work; leave player build and runtime visual acceptance to the user.
