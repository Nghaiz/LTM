# Game UI Collection Menu, Settings, and Chat Design

## Goal

Replace the current code-drawn multiplayer menu skin with a cohesive, asset-backed sci-fi UI using the checked-in `Game UI collection FREE version`, add one shared Settings experience for the title and in-game pause menus, and make Enter reliably submit chat without replacing the original combat HUD.

## Scope

### Included

- Restyle the title, login, registration, lobby, room browser, room creation, room lobby, password prompt, and settings surfaces.
- Use cyan as the normal/focus colour, yellow for primary or destructive emphasis, charcoal surfaces, and off-white copy.
- Add a Settings button to the title screen.
- Open the same Settings surface from the title and the existing in-game pause menu.
- Add resolution selection, fullscreen selection, and master-volume control. FPS controls remain out of scope.
- Make Enter and keypad Enter submit lobby chat when the chat field is active.
- Preserve T-to-open and Escape-to-cancel for in-match chat, while making Enter and keypad Enter submit the active draft.
- Give in-match chat an asset-backed panel and readable typography without changing health, ammo, crosshair, minimap, score, or loadout HUD.
- Preserve Tab, Shift+Tab, Enter, Escape, button focus, disabled-state skipping, and animated screen transitions.

### Excluded

- FPS caps, VSync, render scale, quality presets, or graphics benchmarking.
- A replacement combat HUD.
- Changes to multiplayer protocol, chat payloads, room state, or server behavior.
- Importing PSD, AI, EPS, duplicate colour variants, or unused source sheets into Unity's `Assets` tree.

## Asset Strategy

The source pack remains in its checked-in top-level folder. Only the PNGs used by the shipped UI are copied into `Ironfront_Reborn/Assets/Resources/Ironfront/UI/GameCollection/` with descriptive names and Unity metadata:

- cyan dialogue/panel surface;
- cyan and yellow button frames;
- cyan/white border or bar elements needed by wide panels;
- speaker and mute icons for audio;
- back, close, confirm, and home icons where they improve recognition.

Unity imports these copies as Sprite (2D and UI), preserves alpha, disables mipmaps, and uses suitable border metadata for sliced controls where the image shape supports slicing. The implementation does not depend on the original `Asset 3.png`-style names.

No generated bitmap is planned because the pack already covers panels, controls, audio, back, close, and confirmation. If implementation reveals one genuinely missing small icon, it will be a code-native geometric mark or a single generated transparent PNG, not a second visual style.

## Visual System

### Composition

- The title screen uses a strong left identity block and a right action stack. `IRONFRONT REBORN` remains the largest copy; `TEAM 10 LTM` is supporting copy.
- Form screens use a centered dialogue panel with explicit field labels, consistent vertical rhythm, and a visible keyboard-focus state.
- Wide data screens use a top brand rail, a primary content frame, and aligned footer actions.
- Primary actions use yellow. Navigation, fields, secondary actions, selection, and focus use cyan. Errors remain warm red.
- Decorative elements never receive raycasts and never sit above interactable controls.

### Interaction states

Buttons and fields must have visibly different normal, hover, pressed, selected, disabled, and keyboard-focus states. State changes use Unity `ColorBlock` plus sprite swaps or tinting, with the existing short screen transition retained. No continuous animation or particle layer is added.

### Layout and scaling

The existing 1920×1080 Canvas reference remains. Anchors and the Canvas Scaler support 16:9 resolutions from 1280×720 upward. Critical controls stay inside a safe 5% screen margin. The layout must remain usable at 1280×720 and 1920×1080.

## Architecture

### Asset-backed menu authoring

`BuildMenuCanvas` remains the authoritative scene authoring path. It creates the menu hierarchy and calls an asset-backed skin layer that assigns the selected sprites, colours, typography, layout, and navigation. The Menu scene is regenerated and saved so builds do not depend on a late visual patch.

`MenuRuntimeTheme` is refactored into an idempotent compatibility pass for older serialized menu roots and tests. Its marker is versioned so the existing `Ironfront Runtime Theme` marker cannot prevent the new collection skin from being applied. The runtime pass and editor builder consume the same sprite/resource catalog and style functions.

### Shared settings ownership

The existing `OptionsUi` remains the single owner of game settings because it already persists and applies `master volume`, is present in the Menu scene, survives scene loads, and is already opened by `IngameMenuUi.Options()`.

The settings UI is reorganized into asset-backed `DISPLAY & AUDIO` and `GAMEPLAY` sections. Existing gameplay settings remain available; resolution, fullscreen, and master volume are the prominent first section. FPS is not added.

The multiplayer title assembly cannot reference predefined `Assembly-CSharp`, so a new shared seam, `IGameSettingsLauncher`, is registered beside `IPracticeLauncher` in `MenuSceneBindings`. It exposes availability and `ShowSettings()`. `MenuTitleScreen` calls that seam; `IngameMenuUi` continues to open the same `OptionsUi` instance directly.

### Display settings

`OptionsUi` builds a distinct list of supported width/height pairs from `Screen.resolutions`, ordered from low to high. Duplicate refresh-rate variants collapse to one visible entry. The current resolution is selected when present; otherwise the nearest supported dimensions are selected.

Apply performs `Screen.SetResolution(width, height, fullscreen)` and persists width, height, and fullscreen through `PlayerPrefs`. Cancel restores the last applied values in the controls without changing the display. On startup, a valid saved choice is applied; an invalid or unavailable saved choice falls back to the current display rather than forcing an arbitrary mode.

### Audio settings

The existing `master volume` preference remains the canonical key so old settings and the original game systems stay compatible. The slider uses a normalized 0–1 value. Apply writes the preference and updates the existing mixer conversion immediately. Cancel restores the last applied slider value. No second volume preference or parallel audio authority is introduced.

## Chat Behavior

### Room lobby chat

`MenuRoomLobbyScreen` owns one submit path used by both the Send button and keyboard submission. A non-empty trimmed message is sent once, the field clears, and keyboard focus returns to the chat input so consecutive messages do not require a mouse click. Empty or whitespace-only input is retained or ignored without sending.

The legacy single-line `InputField` receives an explicit end-edit/submit handler. It submits only for Return or keypad Enter, so losing focus through Tab or a mouse click does not send a draft. The screen removes its listeners when disabled or destroyed so scene reloads cannot double-send.

### In-match chat

T remains the open key because Return is also the original deploy/loadout key when chat is closed. Once composing, both Return and keypad Enter submit the draft, and `LocalTextEntry.Composing` continues suppressing gameplay handling. Escape discards the draft. The sender still waits for the server echo; no local optimistic echo is added.

The immediate-mode chat surface receives a dialogue texture, padding, cyan focus outline, off-white message text, and a clear `SQUAD CHAT` prompt. Other in-game HUD elements remain untouched.

## Data and Control Flow

1. Title `Settings` invokes `NetClientBindings.Settings.ShowSettings()`.
2. `LegacyGameSettingsLauncher` resolves the persistent `OptionsUi` and calls `OptionsUi.Show()`.
3. Settings controls edit a pending snapshot. Apply validates and persists the snapshot, applies resolution/fullscreen and the existing mixer volume, then closes. Cancel reloads the applied snapshot and closes.
4. Pause `Settings` opens the same `OptionsUi`, so menu and match never disagree.
5. Lobby button click or valid keyboard submit calls the same chat-submit method exactly once.
6. In-match chat converts Return/keypad Enter into the existing reliable `C_CHAT` path only while composing.

## Error and Edge Handling

- Settings is disabled if no `OptionsUi` exists rather than presenting a dead button.
- An empty resolution list falls back to the current width and height.
- Saved resolutions absent on a different monitor are ignored safely.
- Apply is disabled only while a valid resolution cannot be formed; audio remains clamped to 0–1.
- Empty chat and sanitized-empty chat do not leave the player stuck in composing mode.
- Keyboard submit cannot fire twice from both the navigator and the field event; one component owns the active-field path.
- Runtime styling is idempotent across scene reloads and does not duplicate cards, labels, listeners, or EventSystems.

## Testing

Development follows red-green-refactor.

- Pure tests cover distinct-resolution construction, saved-resolution fallback, selection, volume clamping, and settings snapshot persistence decisions.
- Unity EditMode tests cover title Settings wiring, shared settings availability, asset-backed panels/buttons, runtime skin idempotence, lobby Return/keypad Enter submission, whitespace refusal, focus restoration, and single-send behavior.
- Existing menu keyboard-navigation tests continue to cover Tab, Shift+Tab, Enter, Escape, disabled controls, and multiline fields.
- Client chat intent tests cover T open, Return/keypad Enter send only while composing, Escape cancel, and no deploy/chat conflict.
- Static/editor verification ensures the selected resource sprites exist, import as UI sprites, and are referenced by the authored menu.
- Final verification runs the full Unity EditMode suite, full `Ironfront.sln` tests, branding verifier, `git diff --check`, and a Windows player build when the environment supports it.
- Manual acceptance checks 1280×720 and 1920×1080, title-to-settings, pause-to-settings, audio preview/application, resolution apply/cancel, lobby consecutive chat, and in-match T/type/Enter.

## Acceptance Criteria

- The first visible menu uses the Game UI Collection assets rather than plain generated rectangles.
- Login, registration, lobby, room browser, room creation, and room lobby share one coherent visual system and remain keyboard-operable.
- Settings is reachable from title and pause, and both routes show the same stored resolution/fullscreen/master-volume values.
- Resolution and master volume apply immediately and persist across restart; cancel does not apply pending changes.
- Lobby Enter and keypad Enter send exactly once and leave the player ready to type another line.
- In-match T opens chat; Return and keypad Enter send; Escape cancels; Return outside chat keeps its original gameplay meaning.
- The original combat HUD remains unchanged apart from the chat surface.
- No unused authoring sources are copied into Unity Assets, and no duplicate settings authority is introduced.
