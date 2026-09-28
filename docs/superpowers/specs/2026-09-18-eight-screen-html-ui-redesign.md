# Ironfront Reborn eight-screen HTML UI redesign

## Goal

Rebuild the complete pre-game menu flow from `ui-pack` while preserving the real multiplayer and gameplay behavior already present in the project. The HTML prototype is the visual and interaction reference; it is not a data source.

## Scope

The Unity menu scene will provide eight screens matching the prototype:

1. Main Menu
2. Sign In
3. Create Account
4. Practice
5. Settings
6. Rooms
7. Create Room
8. Waiting Room

Splash and all in-game HUD/gameplay scenes are explicitly out of scope for this pass.

Branding is **Ironfront Reborn**, developed by **Team 10 LTM**. Forgot Password remains visible for HTML parity and reports the shared development notice.

## Source-of-truth rules

- Layout, typography hierarchy, colors, angular panels, buttons, hover/pressed states, and screen transition timing follow the HTML/CSS prototype.
- Room rows, map names, teams, players, ready state, chat, capacity, privacy, and connection state come only from the current game/session/master-server models.
- Prototype names, teams, rooms, pings, invite codes, and other mock values are never copied into Unity.
- Existing network contracts are not extended merely to imitate prototype data.
- A visible, shared notification reports `Tính năng đang được phát triển` when an intentionally unsupported control is activated.

## Architecture

`MenuMultiplayerController` remains the coordinator for authentication and room flow. Existing screen components keep ownership of their real operations. The UI hierarchy is regenerated deterministically by the editor authoring tool so prefab and scene references remain consistent.

Reusable behavior is separated into small components:

- `MenuScreenTransition`: 0.32-second fade and settle animation corresponding to the CSS `screenIn` behavior.
- `MenuKeyboardNavigator`: Tab/Shift+Tab focus traversal, Enter primary action, and Escape back/cancel without stealing Enter from multiline/chat input.
- `MenuChatInput`: Enter sends one trimmed message, clears the field, and restores focus.
- `MenuToast`: shared status/development notification.
- Existing launch/service interfaces bridge menu controls to real settings and practice systems.

Reusable, relevant logic may be selectively taken from `feature/ironfront-reborn-ui-refresh`; the branch will not be merged wholesale because it contains unrelated gameplay and protocol changes.

## Screen behavior

### Main Menu

- Multiplayer opens Sign In when disconnected or Rooms when authenticated.
- Practice opens the new Practice screen.
- Settings opens the new Settings screen.
- Exit uses the existing platform exit action.

### Sign In and Create Account

- Existing master-server authentication and registration are preserved.
- Remember Username remains functional.
- Password visibility controls are local UI behavior.
- Forgot Password reports the shared development notice.
- Validation and network errors appear in the styled status area.

### Rooms

- Search filters the real room list by real room/map text.
- Refresh requests current room data.
- Joining and creating rooms use existing services.
- Mode/region filtering is disabled or reports development status because those properties are absent from the current room protocol.
- Quick Match uses a real supported matchmaking operation if one already exists; otherwise it reports development status. It never selects a fabricated room.

### Create Room

- Room name, map, capacity, bot count, privacy, and password bind to the current create-room contract.
- Mode, region, and balancing controls report development status if the protocol cannot persist them.

### Waiting Room

- Both team rosters, host/ready state, player names, chat, team switching, ready, and leave are session-driven.
- Chat sends on Enter and by button without duplicate submission.
- Invite-code copy reports development status because no authoritative invite code exists.
- The manual Start Game control explains/reports development status unless a real server command exists; the current automatic start-on-ready flow remains authoritative.

### Practice

- Values are populated from current maps and legacy practice capabilities.
- Controls supported by the current offline launcher are applied to the real practice session.
- Unsupported prototype options report development status and do not pretend to save or launch.

### Settings

- Resolution, display mode/fullscreen, quality, VSync, master volume, FOV, and sensitivity are connected where supported by current Unity/player settings.
- FPS limit remains deferred.
- Mixer-specific volume channels, dynamic range, output device, language, colorblind mode, subtitles, motion blur, and camera shake report development status unless the current project already has an authoritative implementation.
- Save applies real settings; Reset restores only settings owned by this UI.

## Assets

Every SVG in the supplied pack is rasterized to a transparent PNG. The source HTML and the Unity asset catalogue reference those PNGs, and no runtime menu asset depends on Unity's SVG importer. Icons are rasterized as white-alpha images so UGUI tinting preserves the prototype's per-state colours; fixed-colour badges, wordmarks, panels, fields, buttons, and decoration retain their authored colours. Original SVGs remain only as recoverable source masters and are never loaded by the menu.

Only assets actually required by the Unity UI are copied from the updated pack into a clean `Assets/UI/IronfrontReborn` hierarchy. HTML, CSS, JavaScript, previews, and mock content stay outside the Unity asset tree. Existing SVG copies are retained as unused source masters after scene references are regenerated and validated.

The three supplied backgrounds and rasterized branding/icons are used without generative alteration. Angular surfaces and state changes described by CSS may be reproduced with Unity UI geometry and colors so they remain scalable; no new art assets are generated.

## Sharpness

- Menu authoring uses the existing 1920x1080 reference canvas and the bundled Roboto font assets instead of `LegacyRuntime.ttf`.
- The generated Canvas is pixel-perfect at the reference resolution; UI positions and sizes are integral pixels.
- Raster UI assets disable mipmaps and compression, keep alpha transparency, use full-rect sprites, and use point filtering for pixel-sized icons or bilinear filtering only where a large surface is intentionally scaled.
- Backgrounds keep their source dimensions and aspect ratio. They may use bilinear filtering, but not lossy Unity texture compression.

## Verification

- Keep verification focused on asset references, the generated scene hierarchy, real-data bindings, and unsupported-action routing.
- Regenerate the menu scene and check that all serialized references resolve.
- Do not run the full Unity or .NET suites for this visual pass. The user will build and runtime-test locally.
- Scan user-facing project/menu text for obsolete Ravenfield/SteelRaven branding while preserving third-party/legal attribution where appropriate.
- Do not create a player build; the user will build and perform final visual/runtime validation.
