# UI Pack Three-Screen Redesign Implementation Plan

> **For Codex:** Execute this plan in order, keeping every test red/green step small. Do not run a player build.

**Goal:** Rebuild Main Menu, Sign In, and Rooms with the supplied UI pack and add complete behavior for their new controls without changing any other screen.

**Architecture:** Keep `BuildMenuCanvas` as the deterministic scene author. Import exact supplied raster files into a dedicated Unity folder, configure them through Unity import metadata/editor code, extend the three existing view components, and add only the minimum controller/binding seams needed for title return and legacy settings/quit actions.

**Tech stack:** Unity legacy UGUI, C#, Unity Editor serialization, NUnit EditMode tests, .NET wiring-gate tests.

---

## Task 1: Add behavior tests for title actions and login usability

**Files:**

- Modify: `Ironfront_Reborn/Assets/Tests/EditMode/Client/Ironfront.Net.Unity.Client.Tests.asmdef`
- Create: `Ironfront_Reborn/Assets/Tests/EditMode/Client/MenuTitleScreenTests.cs`
- Create: `Ironfront_Reborn/Assets/Tests/EditMode/Client/MenuLoginScreenTests.cs`
- Modify: `Ironfront_Reborn/Assets/Scripts/Net/Shared/NetClientBindings.cs`
- Create: `Ironfront_Reborn/Assets/Scripts/Net/Shared/IMenuPlatformActions.cs`
- Modify: `Ironfront_Reborn/Assets/Scripts/Net/Client/Menu/MenuTitleScreen.cs`
- Modify: `Ironfront_Reborn/Assets/Scripts/Net/Client/Menu/MenuLoginScreen.cs`
- Modify: `Ironfront_Reborn/Assets/Scripts/Net/Client/Menu/MenuScreenController.cs`
- Modify: `Ironfront_Reborn/Assets/Scripts/NetBindings/MenuSceneBindings.cs`

1. Write failing EditMode tests proving Settings and Exit forward through an injectable platform-actions binding.
2. Write failing tests proving Remember Me stores/deletes username only, Forgot Password writes the exact unavailable message, Back calls the guarded title transition, and Enter submits the form once.
3. Run the focused EditMode tests and confirm they fail for missing fields/behavior.
4. Add `IMenuPlatformActions` and a nullable `NetClientBindings.MenuPlatformActions` slot reset with the other bindings.
5. Register a legacy adapter that calls `OptionsUi` and `Application.Quit`.
6. Extend title/login/controller components with the tested behavior, explicit navigation helpers, and no password persistence.
7. Re-run focused tests until green.

## Task 2: Add room search and filtered-row identity tests

**Files:**

- Create: `Ironfront_Reborn/Assets/Tests/EditMode/Client/MenuRoomBrowserScreenTests.cs`
- Modify: `Ironfront_Reborn/Assets/Scripts/Net/Client/Menu/MenuRoomBrowserScreen.cs`

1. Write failing tests for case-insensitive name matching, displayed-map matching, clearing the query, and mapping a filtered visible row back to the correct `RoomInfo`.
2. Run the focused test and confirm failure.
3. Add a serialized search `InputField`, a small pure matching helper, and a filtered room cache/index mapping.
4. Close the password prompt when the query changes; keep refresh, busy state, overflow, and eight authored rows intact.
5. Add private-password Enter and Escape handling.
6. Re-run focused tests until green.

## Task 3: Import the exact supplied raster assets

**Files:**

- Create: `Ironfront_Reborn/Assets/UI/IronfrontRebornPack/**` (exact copies of selected files from `ironfront_reborn_ui_pack(1)/ironfront_reborn_ui_pack/assets/**`)
- Create: corresponding Unity `.meta` files through Unity import
- Create: `Ironfront_Reborn/Assets/Editor/NetVerification/IronfrontRebornUiAssetCatalog.cs`

1. Add an editor validation test/check that every required source and destination asset path exists and that no generated raster path is used.
2. Copy only assets used by Main Menu, Sign In, and Rooms, preserving bytes and filenames.
3. Implement the editor catalog with exact paths and sprite-loading helpers.
4. Configure texture importers from the supplied manifest: Sprite/Full Rect, bilinear, and sliced borders of 18/12/8 where applicable.
5. Validate source/destination file hashes and importer configuration.

## Task 4: Rebuild the three screens from the pack

**Files:**

- Modify: `Ironfront_Reborn/Assets/Editor/NetVerification/BuildMenuCanvas.cs`
- Modify: `Ironfront_Reborn/Assets/Scenes/Menu.unity` (generated only by the builder)
- Modify: `tools/ClientWiringGate/MenuScreenWiringDetectors.cs`
- Modify: `Ironfront.Net.Replication.Tests/AssetWiringGateTests.cs`

1. Extend wiring-gate expectations first for every new serialized control and required sprite state.
2. Confirm the focused .NET wiring tests fail against the old scene/builder.
3. Change the Canvas reference resolution to 1280x720.
4. Recompose Main Menu from the supplied main background, logo, action sprites, and icons; wire Multiplayer, Practice, Settings, and Exit.
5. Recompose Sign In from the supplied background, logo, glass panel, fields, checkbox, buttons, icons, status line, and Back control; serialize every new reference and explicit navigation link.
6. Recompose Rooms from the supplied background, top bar, tabs, glass panel, search/refresh controls, room rows, Join controls, ping art, and Create Room action; keep the existing private-room prompt behavior.
7. Leave Register, Authenticating, Lobby, Create Room, Room Lobby, Settings, and in-match HUD construction unchanged.
8. Run the Unity editor builder (scene authoring only, not a player build) to regenerate `Menu.unity`.
9. Re-run focused wiring tests until green.

## Task 5: Verify scope and quality without building the game

**Files:**

- Verify all files changed above.

1. Run focused EditMode tests for the three screen components.
2. Run `dotnet test` for the wiring-gate test project(s).
3. Run the repository's non-build static/wiring checks that do not invoke a Unity player build.
4. Run `git diff --check`.
5. Inspect `git status --short` and confirm the six pre-existing modified DLLs and source UI pack remain uncommitted/unmodified by this work.
6. Review the generated `Menu.unity` diff and verify only the builder-owned `Multiplayer Menu` subtree changed.
7. Report verification results and give the user the exact build/run commands, explicitly leaving execution to them.
