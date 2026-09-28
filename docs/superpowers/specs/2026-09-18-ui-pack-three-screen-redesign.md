# UI Pack Three-Screen Redesign

**Date:** 2026-09-18  
**Status:** Approved  
**Scope:** Main Menu, Sign In, and Rooms only

## Goal

Replace the three pre-game screens with the supplied `ironfront_reborn_ui_pack(1)` visual system while preserving the existing multiplayer flow. The result should match the pack's HTML reference at a 1280x720 reference resolution, scale cleanly through Unity's Canvas scaler, and add complete keyboard and pointer behavior to every new control.

No new visual asset will be generated. Selected PNG and JPG files will be copied unchanged into the Unity project and configured for UI use.

## Non-goals

- Do not redesign Register, Authenticating, signed-in Lobby, Create Room, Room Lobby, Settings, or in-match HUD screens.
- Do not change networking, authentication protocol, room lifecycle, or scene transition rules.
- Do not persist passwords.
- Do not implement a real password-recovery service.
- Do not build a player as part of this task.

## Visual system and asset handling

- Copy only the supplied assets needed by the three screens into a dedicated Unity UI folder.
- Preserve the source pixels and filenames; the copies are imports, not newly authored artwork.
- Configure backgrounds as full-screen sprites and use bilinear filtering.
- Configure panel, button, and field sprites as sliced UI sprites using the manifest borders: panel `18`, button `12`, field `8`.
- Use the pack's base, hover, pressed, and disabled states through Unity `Selectable` sprite-state transitions.
- Use the pack's 1280x720 composition as the Canvas reference resolution.
- Use the supplied logo, icons, backgrounds, panels, rows, tabs, toggles, and button/field sprites only.
- Display `IRONFRONT REBORN` and `Team 10 LTM`; remove old product/publisher attribution from these screens.

## Main Menu

The main background fills the screen. The supplied logo and tagline sit in the upper-left composition, followed by four vertically stacked actions:

1. Multiplayer — primary orange action; transitions into the existing login flow.
2. Practice — secondary action; opens the existing legacy offline menu and is disabled when unavailable.
3. Settings — secondary action; opens the existing `OptionsUi` through a shared binding seam.
4. Exit — danger action; quits in a player build and remains harmless in Edit Mode tests.

The supplied icons accompany each action. Footer copy identifies Team 10 LTM without legacy publisher information.

## Sign In

The supplied sign-in background, logo, glass panel, input field sprites, checkbox sprites, buttons, icons, and back button form the screen.

Behavior:

- Username and password retain existing validation and authentication behavior.
- Tab and Shift+Tab follow an explicit selectable order.
- Enter submits login from either credential field.
- Escape and Back return to the Main Menu by transitioning the flow from `LoginScreen` to `Booting`; registration remains a separate existing sub-view.
- Remember Me stores the username only in `PlayerPrefs`; clearing it removes the stored username. Password is never persisted.
- Forgot Password writes this clear message into the existing status surface: `Password recovery is not available in this classroom build.`
- Create Account opens the existing Register screen.
- Errors use the supplied error field treatment and the existing single error vocabulary.

## Rooms

The supplied rooms background, top bar, active/inactive tabs, glass panel, search field, room rows, ping art, refresh control, Join buttons, and Create Room button form the screen. The screen continues to display real rooms from `MasterSession`; no demo rows are introduced.

Each visible row presents the available room name, map, player count, lifecycle, privacy, and joinability data. The existing eight-row authored capacity and overflow message remain.

Behavior:

- Search filters the current room snapshot locally and case-insensitively by room name or displayed map name.
- Clearing search restores the complete snapshot immediately.
- Refresh clears any password prompt and requests a fresh room list.
- Join preserves existing full/in-progress guards and private-room password prompt behavior.
- Create Room opens the existing Create Room screen.
- Enter in the private-room password field submits the join; Escape cancels the prompt.
- Disabled rooms use the supplied disabled state and cannot be joined.
- The latency readout remains explicitly the measured master-server ping.

## Architecture

`BuildMenuCanvas` remains the authoring source for `Menu.unity`. It will compose the new visuals and serialize all references required by the existing screen components. A small editor-side asset catalog will centralize exact pack asset paths and import settings so scene generation cannot silently drift from the manifest.

Runtime behavior remains in the existing menu screen components:

- `MenuTitleScreen` receives Settings and Exit controls.
- `MenuLoginScreen` receives Remember Me, Forgot Password, Back, and keyboard submission behavior.
- `MenuRoomBrowserScreen` receives a search field and a filtered view-to-room mapping so clicking a filtered row joins the correct room.
- `MenuScreenController` receives a guarded return-to-title operation.
- A shared settings-launcher interface is registered by `MenuSceneBindings`, mirroring the existing practice launcher seam and avoiding a forbidden direct reference from the menu assembly to legacy `Assembly-CSharp` types.

Keyboard navigation will be authored through Unity `Selectable.navigation` and narrowly scoped input handling; it will not add a second menu state machine.

## Error and state handling

- Busy state continues to disable actions during network requests.
- Login failures remain routed through the existing single status/error label.
- The Forgot Password notice uses the same status surface with notice styling and performs no network request.
- Search is presentation-only and never mutates the session's room array.
- A filter change closes a private-room prompt so a hidden row cannot remain selected.
- Password fields are cleared on submission or prompt close as they are today.

## Verification

Implementation will be test-driven where seams permit it:

- EditMode tests for username persistence and the guarantee that no password is stored.
- EditMode tests for Forgot Password messaging, Back behavior, Settings/Exit forwarding, and keyboard submit hooks.
- EditMode tests for room search matching, filtered row-to-room identity, and reset behavior.
- Authoring tests for exact sprite references, sliced borders, selectable states, serialized wiring, and unchanged scope outside the three screens.
- Static checks including `git diff --check` and targeted source/scene validation.

The player build is intentionally left to the user after the checks pass.
