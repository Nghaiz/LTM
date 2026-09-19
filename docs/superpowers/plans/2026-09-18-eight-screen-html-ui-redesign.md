# Ironfront Reborn Eight-Screen HTML UI Redesign Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the complete pre-game menu with the eight-screen updated HTML design while keeping all multiplayer, practice, settings, and room data authoritative to the current game.

**Architecture:** Keep `MenuScreenController` and the existing screen classes as the network-flow boundary, add focused presentation/input components, and regenerate `Menu.unity` deterministically through `BuildMenuCanvas`. Import only the supplied production assets into a clean Unity folder; reproduce CSS surfaces and states with scalable UGUI elements.

**Tech Stack:** Unity 6, C# nullable context, legacy UGUI (`Canvas`, `Image`, `Text`, `InputField`, `Dropdown`, `Slider`), Unity Test Framework/NUnit, PowerShell verification.

**Spec:** `docs/superpowers/specs/2026-09-18-eight-screen-html-ui-redesign.md`

## Global Constraints

- The HTML/CSS prototype controls presentation, not gameplay data.
- Do not copy mock room, map, team, player, ping, or invite-code values.
- Do not implement Forgot Password.
- Unsupported controls must display `Tính năng đang được phát triển`.
- Branding is `Ironfront Reborn` / `Team 10 LTM`.
- Do not modify or stage the six user-owned plugin DLL changes.
- Do not create a player build.
- Do not merge `feature/ironfront-reborn-ui-refresh` wholesale; reuse only focused UI logic.

---

### Task 1: Reusable menu interaction components

**Files:**
- Create: `Ironfront_Reborn/Assets/Scripts/Net/Client/Menu/MenuTransitionState.cs`
- Create: `Ironfront_Reborn/Assets/Scripts/Net/Client/Menu/MenuScreenTransition.cs`
- Create: `Ironfront_Reborn/Assets/Scripts/Net/Client/Menu/MenuNavigationCycle.cs`
- Create: `Ironfront_Reborn/Assets/Scripts/Net/Client/Menu/MenuKeyboardNavigator.cs`
- Create: `Ironfront_Reborn/Assets/Scripts/Net/Client/Menu/MenuChatInput.cs`
- Create: `Ironfront_Reborn/Assets/Scripts/Net/Client/Menu/MenuToast.cs`
- Test: `Ironfront_Reborn/Assets/Tests/EditMode/Client/MenuScreenTransitionTests.cs`
- Test: `Ironfront_Reborn/Assets/Tests/EditMode/Client/MenuKeyboardNavigatorTests.cs`
- Test: `Ironfront_Reborn/Assets/Tests/EditMode/Client/MenuChatInputTests.cs`
- Test: `Ironfront_Reborn/Assets/Tests/EditMode/Client/MenuToastTests.cs`

**Interfaces:**
- Produces: `MenuScreenTransition.SetVisible(bool visible, bool immediate = false)`.
- Produces: `MenuKeyboardNavigator.Configure(Selectable[] order, Button primary, Button cancel, EventSystem eventSystem = null)`.
- Produces: `MenuChatInput.Configure(InputField field, Action<string> submit, Func<bool> submitKeyDown = null, EventSystem eventSystem = null)`.
- Produces: `MenuToast.ShowDevelopment()` and `MenuToast.Show(string message)`.

- [ ] Write failing EditMode tests proving a 0.32-second transition target, cyclic Tab order, Enter submission without duplicate chat, focus restoration, and development-notification text.
- [ ] Run the focused tests and verify they fail because the types do not exist.
- [ ] Port the keyboard/chat logic from the old UI branch, adapt transition state to fade plus scale from `1.008` to `1`, and implement a timeout-based shared toast.
- [ ] Run the focused tests and verify they pass.
- [ ] Commit only the new components, metas, and tests as `feat(ui): add polished menu interactions`.

### Task 2: Real settings bridge

**Files:**
- Create: `Ironfront_Reborn/Assets/Scripts/Net/Shared/IGameSettingsLauncher.cs`
- Create: `Ironfront_Reborn/Assets/Scripts/Net/Shared/DisplayResolutionCatalog.cs`
- Create: `Ironfront_Reborn/Assets/Scripts/Net/Client/Menu/MenuSettingsScreen.cs`
- Modify: `Ironfront_Reborn/Assets/Scripts/Net/NetClientBindings.cs`
- Modify: `Ironfront_Reborn/Assets/Scripts/NetBindings/MenuSceneBindings.cs`
- Modify: `Ironfront_Reborn/Assets/Scripts/Assembly-CSharp/OptionsUi.cs`
- Test: `Ironfront_Reborn/Assets/Tests/EditMode/Client/GameSettingsBindingTests.cs`
- Test: `Ironfront_Reborn/Assets/Tests/EditMode/Client/DisplayResolutionCatalogTests.cs`

**Interfaces:**
- Produces: `IGameSettingsLauncher.IsAvailable`, `ShowSettings()`, `HideSettings()`, and display-setting accessors used by `MenuSettingsScreen`.
- Produces: `DisplayResolutionCatalog.Build(Resolution[] values)` and `FindClosestIndex(...)`.
- Consumes: `MenuToast.ShowDevelopment()` for unsupported tabs/controls.

- [ ] Write failing tests for resolution de-duplication/sorting, closest-selection, and the binding that opens the existing options owner.
- [ ] Run the focused tests and verify failure.
- [ ] Add resolution, fullscreen/window mode, quality and VSync persistence to `OptionsUi` without altering existing sensitivity/FOV/volume keys; expose them through the assembly-safe launcher.
- [ ] Implement `MenuSettingsScreen` tabs so supported controls apply real values and unsupported controls call the toast.
- [ ] Run focused tests and verify they pass.
- [ ] Commit as `feat(ui): connect redesigned settings to game options`.

### Task 3: Eight-screen controller behavior

**Files:**
- Modify: `Ironfront_Reborn/Assets/Scripts/Net/Client/Menu/MenuScreenController.cs`
- Modify: `Ironfront_Reborn/Assets/Scripts/Net/Client/Menu/MenuTitleScreen.cs`
- Modify: `Ironfront_Reborn/Assets/Scripts/Net/Client/Menu/MenuLoginScreen.cs`
- Modify: `Ironfront_Reborn/Assets/Scripts/Net/Client/Menu/MenuRegisterScreen.cs`
- Modify: `Ironfront_Reborn/Assets/Scripts/Net/Client/Menu/MenuRoomBrowserScreen.cs`
- Modify: `Ironfront_Reborn/Assets/Scripts/Net/Client/Menu/MenuCreateRoomScreen.cs`
- Modify: `Ironfront_Reborn/Assets/Scripts/Net/Client/Menu/MenuRoomLobbyScreen.cs`
- Create: `Ironfront_Reborn/Assets/Scripts/Net/Client/Menu/MenuPracticeScreen.cs`
- Test: `Ironfront_Reborn/Assets/Tests/EditMode/Client/MenuTitleSettingsTests.cs`
- Modify: existing menu screen tests under `Ironfront_Reborn/Assets/Tests/EditMode/Client/`.

**Interfaces:**
- Consumes: transition, navigator, chat, toast, settings launcher from Tasks 1–2.
- Produces: controller routes for Main, Practice, Settings, auth, Rooms, Create Room, Waiting Room, and back navigation.

- [ ] Write failing tests for Settings/Practice routing, real room search, Enter chat submission, and unsupported quick-match/filter/invite/start behavior.
- [ ] Run focused tests and verify failure.
- [ ] Extend the controller with presentation subviews without creating a competing game-flow enum; preserve thread-marshalled session callbacks.
- [ ] Wire real supported fields to existing screen submit methods and route unsupported prototype controls to the toast.
- [ ] Ensure waiting-room rows are populated only from `RoomState`, room rows only from `RoomInfo[]`, and practice map choices only from current game data.
- [ ] Run all client EditMode tests and verify they pass.
- [ ] Commit as `feat(ui): implement eight-screen menu behavior`.

### Task 4: Import and normalize the updated asset pack

**Files:**
- Create: `Ironfront_Reborn/Assets/UI/IronfrontReborn/Backgrounds/*`
- Create: `Ironfront_Reborn/Assets/UI/IronfrontReborn/Branding/*`
- Create: `Ironfront_Reborn/Assets/UI/IronfrontReborn/Icons/*`
- Delete: `Ironfront_Reborn/Assets/UI/IronfrontRebornPack/`

**Interfaces:**
- Consumes: supplied files under `ui-pack/assets`.
- Produces: stable Unity asset paths referenced by the menu authoring tool.

- [ ] Copy the three supplied backgrounds and only the branding/icons used by the eight screens into the normalized hierarchy without modifying their art.
- [ ] Add importer metadata via Unity and confirm sprites retain transparency and correct aspect ratios.
- [ ] Remove the old pack only after confirming the scene builder no longer references it.
- [ ] Scan `Assets/UI` for duplicate obsolete menu-pack art.
- [ ] Commit normalized assets and deletions as `chore(ui): replace obsolete menu art pack`.

### Task 5: Reauthor Menu.unity from the HTML layout

**Files:**
- Modify: `Ironfront_Reborn/Assets/Editor/NetVerification/BuildMenuCanvas.cs`
- Modify: `Ironfront_Reborn/Assets/Scenes/Menu.unity`
- Test: `Ironfront_Reborn/Assets/Tests/EditMode/Client/MenuAuthoringTests.cs`

**Interfaces:**
- Consumes: all behavior components and normalized assets from Tasks 1–4.
- Produces: one serialized `MenuScreenController` hierarchy containing the eight named screens and complete serialized field assignments.

- [ ] Write authoring tests for exactly eight screen roots, 1920×1080 reference scaling, supplied background paths, no forgot-password control, no hard-coded mock rows, and all behavior references assigned.
- [ ] Run the authoring tests and verify they fail against the current three-screen scene.
- [ ] Refactor builder helpers for angular panels, typography, buttons, fields, tabs, lists, rosters, toggles, dropdowns, and sliders using the HTML tokens.
- [ ] Build all eight layouts and configure navigation order, hover/pressed colors, transitions, toast, settings, practice, and room/chat fields.
- [ ] Run Unity in batch mode with `BuildMenuCanvas.RebuildFromCommandLine`, without `-buildTarget` or player-build methods, to regenerate and import the scene/assets.
- [ ] Run authoring tests and verify they pass.
- [ ] Commit builder and scene as `feat(ui): author eight-screen html-inspired menu`.

### Task 6: Branding and identity cleanup

**Files:**
- Modify: `Ironfront_Reborn/ProjectSettings/ProjectSettings.asset`
- Modify: user-facing text/resources discovered by the brand scan.
- Create: `Ironfront_Reborn/Assets/Tests/EditMode/Client/BrandIdentityTests.cs`
- Create: `tools/verify-ui-branding.ps1`

**Interfaces:**
- Produces: project identity `Ironfront Reborn` / `Team 10 LTM` and a repeatable user-facing branding check.

- [ ] Write a failing test/script that checks product/company fields and scans owned user-facing menu/project content for Ravenfield/SteelRaven identity.
- [ ] Run it and record the remaining owned references.
- [ ] Replace owned product/developer labels and obsolete external developer links while leaving third-party licenses and historical technical context intact.
- [ ] Run branding tests and scan again.
- [ ] Commit as `chore(brand): complete ironfront reborn identity`.

### Task 7: Full verification and handoff

**Files:**
- Modify only files required to fix verified failures.

**Interfaces:**
- Consumes: completed Tasks 1–6.
- Produces: test evidence and exact user-run player-build command.

- [ ] Run Unity compilation/import and inspect `Editor.log` for compiler or serialization errors.
- [ ] Run the full Unity EditMode test suite and record total/pass/fail counts.
- [ ] Run the full .NET solution test suite and record total/pass/fail counts.
- [ ] Run branding verification, missing-reference scans, and `git diff --check`.
- [ ] Confirm the six modified plugin DLLs remain unstaged and untouched and the source pack is not accidentally committed.
- [ ] Commit any narrowly scoped verification fixes.
- [ ] Report changed behavior, unsupported controls, verification evidence, and instruct the user to build/run locally; do not build the player.
