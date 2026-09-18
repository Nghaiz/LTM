# Game UI Collection Settings and Chat Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Ship an asset-backed Ironfront menu, shared title/pause settings for resolution/fullscreen/master volume, and reliable Enter-to-send behavior for lobby and in-match chat while preserving the original combat HUD.

**Architecture:** A small auto-referenced Unity UI assembly owns reusable asset loading and control styling. The generated multiplayer menu and legacy `OptionsUi` both consume it; `IGameSettingsLauncher` bridges the client menu assembly to `Assembly-CSharp`. Pure resolution and chat-intent policies stay Unity-free where possible so they run in the fast .NET suite, while Unity EditMode tests cover real controls, resources, focus, styling, and scene authoring.

**Tech Stack:** Unity 6000.3.21f1, legacy uGUI, C# 9, NUnit EditMode tests, xUnit .NET tests, PowerShell build scripts.

**Spec:** `docs/superpowers/specs/2026-09-18-game-ui-collection-settings-chat-design.md`

## Global Constraints

- Cyan is the normal/focus colour; yellow marks primary actions; charcoal is the surface colour; errors remain warm red.
- Settings must be the same `OptionsUi` instance from both title and pause; do not create a second persistence or audio authority.
- Resolution, fullscreen, and master volume are in scope; FPS controls are explicitly out of scope.
- T opens in-match chat; Return and keypad Enter send only while composing; Escape cancels.
- Keep the original combat HUD unchanged except for the in-match chat surface.
- Copy only used PNGs into Unity `Assets`; do not import PSD, AI, EPS, SVG source sheets, or unused variants.
- All production behavior changes follow red-green-refactor.

---

### Task 1: Import the selected Game UI Collection sprites and create the shared skin assembly

**Files:**
- Create: `Ironfront_Reborn/Assets/Resources/Ironfront/UI/GameCollection/PanelCyan.png`
- Create: `Ironfront_Reborn/Assets/Resources/Ironfront/UI/GameCollection/ButtonCyan.png`
- Create: `Ironfront_Reborn/Assets/Resources/Ironfront/UI/GameCollection/ButtonYellow.png`
- Create: `Ironfront_Reborn/Assets/Resources/Ironfront/UI/GameCollection/ButtonBorderCyan.png`
- Create: `Ironfront_Reborn/Assets/Resources/Ironfront/UI/GameCollection/ButtonBorderYellow.png`
- Create: selected icon PNGs with descriptive names under the same directory
- Create: Unity `.meta` files by importing through Unity
- Create: `Ironfront_Reborn/Assets/Scripts/UI/Ironfront.Unity.Ui.asmdef`
- Create: `Ironfront_Reborn/Assets/Scripts/UI/GameUiCollectionResources.cs`
- Create: `Ironfront_Reborn/Assets/Scripts/UI/GameUiCollectionSkin.cs`
- Modify: `Ironfront_Reborn/Assets/Scripts/Net/Client/Ironfront.Net.Unity.Client.asmdef`
- Test: `Ironfront_Reborn/Assets/Tests/EditMode/Client/GameUiCollectionSkinTests.cs`

**Interfaces:**
- Produces: `GameUiCollectionResources.Load(): GameUiCollectionResources`
- Produces: `GameUiCollectionSkin.StyleButton(Button button, bool primary)`
- Produces: `GameUiCollectionSkin.StylePanel(Image image)` and field/dropdown/slider/toggle style methods
- Consumes: the checked-in source pack at `Game UI collection FREE version/Game UI collection FREE version/PNG/`

- [ ] **Step 1: Write the failing resource and state-style tests**

Create NUnit tests that load `PanelCyan`, `ButtonCyan`, `ButtonYellow`, speaker/mute, back, close, and confirm resources by their final descriptive resource paths, then style real `Button` and `InputField` objects. Assert every resource is non-null and assert normal, highlighted, selected, pressed, and disabled colours differ where the player must perceive a state.

- [ ] **Step 2: Run the focused Unity test and verify RED**

Run:

```powershell
& $env:UNITY_PATH -batchmode -nographics -projectPath Ironfront_Reborn -runTests -testPlatform EditMode -testFilter Ironfront.Net.Unity.Client.Tests.GameUiCollectionSkinTests -testResults tmp/game-ui-red.xml -logFile tmp/game-ui-red.log
```

Expected: FAIL because the resources and `GameUiCollectionSkin` do not exist.

- [ ] **Step 3: Copy only the selected PNGs and implement the shared assembly**

Use the cyan dialogue, cyan/yellow button and border variants, and only the required icons. Give the copied assets descriptive filenames. Create `Ironfront.Unity.Ui` with a `UnityEngine.UI` reference and `autoReferenced: true`; add it as an explicit reference to the client asmdef.

Implement a cached resource catalog and focused styling methods. Decorative images must set `raycastTarget = false`; controls retain their own raycasts. Button states use a `ColorBlock` with `fadeDuration = 0.08f`.

- [ ] **Step 4: Import assets and verify GREEN**

Run Unity once without `-quit` so it creates deterministic `.meta` files, then rerun the focused tests. Expected: all `GameUiCollectionSkinTests` pass.

- [ ] **Step 5: Commit**

```powershell
git add Ironfront_Reborn/Assets/Resources/Ironfront/UI/GameCollection Ironfront_Reborn/Assets/Scripts/UI Ironfront_Reborn/Assets/Scripts/Net/Client/Ironfront.Net.Unity.Client.asmdef Ironfront_Reborn/Assets/Tests/EditMode/Client/GameUiCollectionSkinTests.cs*
git commit -m "feat(ui): import game UI collection skin"
```

### Task 2: Add tested resolution-selection policy

**Files:**
- Create: `Ironfront_Reborn/Assets/Scripts/Net/Shared/DisplayResolutionCatalog.cs`
- Create: `Ironfront.Client.Flow.Tests/DisplayResolutionCatalogTests.cs`
- Modify: `Ironfront.Client.Flow.Tests/Ironfront.Client.Flow.Tests.csproj`

**Interfaces:**
- Produces: `readonly struct DisplayResolutionOption { int Width; int Height; string Label; }`
- Produces: `DisplayResolutionCatalog.Build(IEnumerable<DisplayResolutionOption>): DisplayResolutionOption[]`
- Produces: `DisplayResolutionCatalog.FindBestIndex(IReadOnlyList<DisplayResolutionOption>, int width, int height): int`
- Consumes: Unity `Screen.resolutions` only through conversion performed later by `OptionsUi`

- [ ] **Step 1: Write failing xUnit tests**

Cover literal fixtures for duplicate width/height modes, ascending ordering, exact-current selection, nearest-mode fallback, and an empty input fallback supplied by the caller. Each test must name the incorrect behavior it catches.

- [ ] **Step 2: Run and verify RED**

```powershell
dotnet test Ironfront.Client.Flow.Tests/Ironfront.Client.Flow.Tests.csproj --no-restore -c Release -m:1 --disable-build-servers --filter DisplayResolutionCatalogTests
```

Expected: compile failure because the catalog is missing.

- [ ] **Step 3: Implement the minimal Unity-free catalog**

Use width/height equality for deduplication, stable ascending pixel-area ordering, and squared distance for nearest fallback. Do not add refresh-rate or FPS policy.

- [ ] **Step 4: Run focused and full flow tests**

Expected: focused tests pass, then all `Ironfront.Client.Flow.Tests` pass.

- [ ] **Step 5: Commit**

```powershell
git add Ironfront_Reborn/Assets/Scripts/Net/Shared/DisplayResolutionCatalog.cs* Ironfront.Client.Flow.Tests
git commit -m "feat(settings): select supported display resolutions"
```

### Task 3: Make the existing OptionsUi the shared asset-backed settings screen

**Files:**
- Create: `Ironfront_Reborn/Assets/Scripts/Net/Shared/IGameSettingsLauncher.cs`
- Modify: `Ironfront_Reborn/Assets/Scripts/Net/Shared/NetClientBindings.cs`
- Modify: `Ironfront_Reborn/Assets/Scripts/NetBindings/MenuSceneBindings.cs`
- Modify: `Ironfront_Reborn/Assets/Scripts/Assembly-CSharp/OptionsUi.cs`
- Modify: `Ironfront_Reborn/Assets/Scripts/Assembly-CSharp/IngameMenuUi.cs`
- Test: `Ironfront_Reborn/Assets/Tests/EditMode/Client/GameSettingsBindingTests.cs`

**Interfaces:**
- Produces: `IGameSettingsLauncher.IsAvailable` and `IGameSettingsLauncher.ShowSettings()`
- Produces: `NetClientBindings.Settings`
- Consumes: `DisplayResolutionCatalog`, `GameUiCollectionSkin`, existing `OptionsUi` controls, mixer and `master volume` key

- [ ] **Step 1: Write failing binding/reset tests**

Add tests proving a fake settings launcher can be installed, invoked, and cleared by the same test reset seam used for other bindings. The production mutation caught is a missing registration slot or a stale process-wide settings binding.

- [ ] **Step 2: Run and verify RED**

Expected: compile failure because `IGameSettingsLauncher` and `NetClientBindings.Settings` do not exist.

- [ ] **Step 3: Implement the bridge and registration**

Add `LegacyGameSettingsLauncher` beside `LegacyPracticeLauncher`. Resolve `OptionsUi` lazily with inactive objects included; `IsAvailable` is false when none exists; `ShowSettings()` calls the existing static show path. Register it before scene load and clear it in `NetClientBindings.ResetOnLoad`.

- [ ] **Step 4: Add resolution/fullscreen controls and reskin OptionsUi**

Extend `OptionsUi` with a resolution dropdown and fullscreen toggle created or resolved idempotently. Populate from `Screen.resolutions` through the pure catalog. Persist `ironfront resolution width`, `ironfront resolution height`, and `ironfront fullscreen`; keep `master volume` unchanged. Apply uses `Screen.SetResolution` and the existing mixer formula. Cancel reloads the last applied state. Organize the screen as `DISPLAY & AUDIO` plus `GAMEPLAY` and apply the shared collection skin.

- [ ] **Step 5: Keep pause routing on the same owner**

Ensure `IngameMenuUi.Options()` still calls `OptionsUi.Show()` and receives the reskinned extended screen. Do not create another settings Canvas.

- [ ] **Step 6: Run Unity tests and compile**

Run the focused binding tests and then the Unity EditMode suite. Expected: pass with no duplicate-assembly or UI reference errors.

- [ ] **Step 7: Commit**

```powershell
git add Ironfront_Reborn/Assets/Scripts/Net/Shared Ironfront_Reborn/Assets/Scripts/NetBindings/MenuSceneBindings.cs Ironfront_Reborn/Assets/Scripts/Assembly-CSharp/OptionsUi.cs Ironfront_Reborn/Assets/Scripts/Assembly-CSharp/IngameMenuUi.cs Ironfront_Reborn/Assets/Tests/EditMode/Client/GameSettingsBindingTests.cs*
git commit -m "feat(settings): share display and audio settings"
```

### Task 4: Add Settings to the title and replace the previous menu skin

**Files:**
- Modify: `Ironfront_Reborn/Assets/Scripts/Net/Client/Menu/MenuTitleScreen.cs`
- Modify: `Ironfront_Reborn/Assets/Scripts/Net/Client/Menu/MenuRuntimeTheme.cs`
- Modify: `Ironfront_Reborn/Assets/Editor/NetVerification/BuildMenuCanvas.cs`
- Modify: `Ironfront_Reborn/Assets/Tests/EditMode/Client/MenuRuntimeThemeTests.cs`
- Create: `Ironfront_Reborn/Assets/Tests/EditMode/Client/MenuTitleSettingsTests.cs`
- Regenerate: `Ironfront_Reborn/Assets/Scenes/Menu.unity`

**Interfaces:**
- Consumes: `NetClientBindings.Settings`, `GameUiCollectionResources`, `GameUiCollectionSkin`
- Produces: a title `Settings` button and versioned `Game UI Collection Theme v2` hierarchy

- [ ] **Step 1: Write failing title and theme tests**

Use real GameObjects and controls. Assert title Settings invokes a fake launcher exactly once, is disabled when unavailable, and appears in keyboard order. Assert the themed title and form screens use non-null collection sprites, preserve distinct interaction states, and remain idempotent. The test must fail against the current plain `Image` theme.

- [ ] **Step 2: Run and verify RED**

Expected: tests fail because there is no Settings button/reference and no collection sprite assignment.

- [ ] **Step 3: Implement title wiring and the asset-backed theme**

Add the serialized Settings button, wire it in `Awake`, and update it from `IsAvailable`. Replace the old flat card/rail implementation with asset-backed panels and button frames while preserving object names consumed by screen scripts. Version the marker and remove only the old generated theme children, never screen controls.

- [ ] **Step 4: Update the builder and regenerate Menu.unity**

Build Settings alongside Multiplayer and Practice, assign the new serialized field, apply the shared skin, run `BuildMenuCanvas.Run`, and save. Verify the scene diff is scoped to the generated multiplayer root and references the imported sprite GUIDs.

- [ ] **Step 5: Run focused and full Unity tests**

Expected: title/theme tests and the existing keyboard, branding, transition, and screen-wiring tests pass.

- [ ] **Step 6: Commit**

```powershell
git add Ironfront_Reborn/Assets/Scripts/Net/Client/Menu Ironfront_Reborn/Assets/Editor/NetVerification/BuildMenuCanvas.cs Ironfront_Reborn/Assets/Tests/EditMode/Client Ironfront_Reborn/Assets/Scenes/Menu.unity
git commit -m "feat(menu): apply game UI collection design"
```

### Task 5: Make lobby Enter submission reliable and focus-preserving

**Files:**
- Create: `Ironfront_Reborn/Assets/Scripts/Net/Client/Menu/MenuChatInput.cs`
- Modify: `Ironfront_Reborn/Assets/Scripts/Net/Client/Menu/MenuRoomLobbyScreen.cs`
- Modify: `Ironfront_Reborn/Assets/Editor/NetVerification/BuildMenuCanvas.cs`
- Create: `Ironfront_Reborn/Assets/Tests/EditMode/Client/MenuChatInputTests.cs`

**Interfaces:**
- Produces: `MenuChatInput.Configure(InputField field, Action<string> submit, Func<bool>? submitKeyDown = null)`
- Produces: one submit event for Return/keypad Enter end-edit, with clear and focus restoration
- Consumes: `MenuRoomLobbyScreen`'s existing controller send method and text limit

- [ ] **Step 1: Write failing real-control tests**

With a real `InputField` and `EventSystem`, inject a deterministic submit-key source. Assert Return submits trimmed non-empty text once, keypad Enter follows the same path, focus-loss without Enter does not send, whitespace does not send, the field clears after send, and focus returns to the field. The captured submitted strings are observable component output, not mock assertions.

- [ ] **Step 2: Run and verify RED**

Expected: compile failure because `MenuChatInput` does not exist.

- [ ] **Step 3: Implement MenuChatInput and connect the room screen**

Own listener subscription/unsubscription in the component lifecycle. Route Send button and keyboard through one screen method so the same draft cannot send twice. Keep the protocol call and server-echo behavior unchanged.

- [ ] **Step 4: Update authoring and navigation**

Have `BuildMenuCanvas` add/configure `MenuChatInput`. Ensure the panel navigator does not independently submit the same active chat field when the field owns Return.

- [ ] **Step 5: Run focused and full tests**

Expected: all chat-input tests pass and existing menu navigation tests remain green.

- [ ] **Step 6: Commit**

```powershell
git add Ironfront_Reborn/Assets/Scripts/Net/Client/Menu Ironfront_Reborn/Assets/Editor/NetVerification/BuildMenuCanvas.cs Ironfront_Reborn/Assets/Tests/EditMode/Client/MenuChatInputTests.cs*
git commit -m "fix(chat): send lobby messages with enter"
```

### Task 6: Support both Enter keys in match chat and skin only its surface

**Files:**
- Create: `Ironfront_Reborn/Assets/Scripts/Net/Client/ChatKeyboardIntent.cs`
- Modify: `Ironfront_Reborn/Assets/Scripts/Net/Client/ClientChatSender.cs`
- Create: `Ironfront.Client.Flow.Tests/ChatKeyboardIntentTests.cs`
- Modify: `Ironfront.Client.Flow.Tests/Ironfront.Client.Flow.Tests.csproj`
- Create: `Ironfront_Reborn/Assets/Tests/EditMode/Client/ClientChatSenderInputTests.cs`

**Interfaces:**
- Produces: `ChatKeyboardIntent.ShouldSend(bool composing, bool returnDown, bool keypadEnterDown): bool`
- Consumes: existing T open, Escape cancel, `LocalTextEntry.Composing`, reliable C_CHAT encoding and server echo

- [ ] **Step 1: Write failing intent tests**

Assert Return and keypad Enter send only while composing, neither opens chat, and no Enter submits while closed. Use literal boolean cases rather than mirroring the implementation.

- [ ] **Step 2: Run and verify RED**

Expected: compile failure because `ChatKeyboardIntent` does not exist.

- [ ] **Step 3: Implement the helper and integrate ClientChatSender**

Keep T and Escape semantics. Replace the single `_sendKey` check with the tested intent using Return plus KeypadEnter. Preserve `SetComposing(false)` after every accepted or refused submit so gameplay input is restored.

- [ ] **Step 4: Apply the chat-only visual skin**

Load the collection dialogue texture once, use it as the chat background with padding, cyan focus treatment, and off-white text. Do not modify the prefab HUD, crosshair, health, ammo, minimap, score, or loadout UI.

- [ ] **Step 5: Run focused and full flow/Unity tests**

Expected: intent tests, client chat tests, and Unity EditMode suite pass.

- [ ] **Step 6: Commit**

```powershell
git add Ironfront_Reborn/Assets/Scripts/Net/Client/ChatKeyboardIntent.cs* Ironfront_Reborn/Assets/Scripts/Net/Client/ClientChatSender.cs Ironfront.Client.Flow.Tests Ironfront_Reborn/Assets/Tests/EditMode/Client
git commit -m "fix(chat): submit active match chat with enter"
```

### Task 7: Regenerate, build, and complete acceptance verification

**Files:**
- Regenerate: `Ironfront_Reborn/Assets/Scenes/Menu.unity`
- Verify without modification: `tools/verify-ui-branding.ps1`

**Interfaces:**
- Consumes: every preceding task
- Produces: a tested Windows player at `build/windows/Ironfront.exe`

- [ ] **Step 1: Run all automated verification**

```powershell
dotnet test Ironfront.sln --no-restore -c Release -m:1 --disable-build-servers
pwsh tools/verify-ui-branding.ps1
git diff --check
```

Run the complete Unity EditMode suite without `-quit` and require zero failures.

- [ ] **Step 2: Build the Windows player**

```powershell
pwsh tools/build-player.ps1
```

Expected: successful build containing the current commit and updated `build/windows/Ironfront.exe` timestamp.

- [ ] **Step 3: Perform manual acceptance**

At 1280×720 and 1920×1080 verify title, login, registration, lobby, room browser, room creation, room lobby, Settings from title, Settings from pause, Apply/Cancel, consecutive lobby chat, and T/type/Enter in-match chat. Confirm original combat HUD remains unchanged.

- [ ] **Step 4: Review the final diff and commit any verification-only metadata**

Ensure no temporary logs/XML, duplicated runtime theme objects, unrelated HUD changes, or imported authoring formats are tracked.
