# Ironfront Reborn UI and Branding Refresh

**Status:** Approved in chat on 2026-09-17

## Goal

Present the class project as **Ironfront Reborn**, made by **Team 10 LTM**, and replace the inherited menu experience with a polished, responsive multiplayer-first interface. The work must preserve the current game-flow and network contracts while making every menu usable with keyboard and mouse.

## Scope

### Player-facing identity

- Set Unity's company name to `Team 10 LTM` and product name to `Ironfront Reborn`.
- Use a stable standalone application identifier derived from those names.
- Replace inherited Ravenfield, SteelRaven7, author, copyright, social, and voting copy or links in the splash and menu experience.
- Show `IRONFRONT REBORN` as the game title and `TEAM 10 LTM` as the project credit.
- Keep historical and technical references to Ravenfield in internal documentation and source comments when they explain compatibility, provenance, or gameplay parity. Those references are not product branding and deleting them would damage maintainability.
- Do not rename solution assemblies, namespaces, environment variables, or paths that already use `Ironfront`; they are stable technical interfaces.

### Visual direction

The menu uses a modern tactical-command visual language built entirely with Unity UI primitives and existing local fonts:

- deep navy/charcoal backdrop with a restrained blue-grey gradient treatment;
- warm amber as the primary action/accent colour, muted cyan for live/network status, and red only for destructive states or errors;
- a left-aligned brand rail and a focused content card instead of controls floating in the middle of an empty screen;
- clear typography hierarchy, consistent field labels, generous spacing, and readable contrast at 1920×1080 and scaled resolutions;
- button, input, toggle, dropdown, selected, disabled, and validation states share one token palette;
- subtle screen fades and small positional easing, with no animation that delays input or network operations;
- no downloaded art, asset-store dependency, or third-party font. Decorative shapes, grid lines, vignettes, and status marks are generated from Unity UI components.

The UI remains authored by `BuildMenuCanvas`, so `Menu.unity` is reproducible and references continue to be assigned through Unity Editor APIs rather than hand-written YAML.

## Information architecture

The existing `GameFlowState` remains the single source of truth. The refresh changes presentation and input behavior but does not introduce a competing navigation state machine.

- **Title:** brand, short multiplayer descriptor, Multiplayer primary action, Practice secondary action, Team 10 LTM credit.
- **Sign in:** labelled username and password fields, login primary action, account creation link, inline status/error region.
- **Register:** labelled username, password, confirmation, and optional display-name fields; concise requirements; create primary action and back action.
- **Lobby:** signed-in identity and a clear route to room browsing.
- **Room browser:** scan-friendly rows with room name, map, population, privacy/state, refresh and create actions.
- **Create room:** grouped room settings with explicit labels and sensible defaults.
- **Room lobby:** room identity, teams/roster, chat, readiness/start controls, and status feedback.
- **Transient overlays:** private-room password prompt and authenticating state use the same card and feedback language as the main forms.

## Keyboard and focus behavior

A reusable `MenuKeyboardNavigator` component owns keyboard semantics for each screen without owning game-flow state.

- Opening a form selects its first meaningful control.
- `Tab` moves to the next interactable control and wraps; `Shift+Tab` moves backward and wraps.
- `Enter` submits the screen's primary action. In a multiline field it inserts a newline instead; the current chat field is single-line, so Enter sends chat there.
- `Escape` performs the local back/cancel action when one exists. It never exits the application silently.
- Navigation skips inactive and non-interactable controls.
- A busy form cannot submit twice. Focus remains deterministic when controls become disabled.
- Password fields retain masking, and existing password-clearing behavior remains intact.
- Mouse interaction continues to work and updates the selected control normally.

Each form screen exposes small public action methods used by both its button listeners and keyboard navigator. Validation continues to route through the existing single error surface.

## Motion and feedback

`MenuScreenTransition` provides short unscaled-time fade/slide transitions through a `CanvasGroup`. It starts a newly opened panel in an input-safe state, completes in approximately 160–220 ms, and immediately cancels/restarts if flow changes again. Reduced complexity is preferred over elaborate animation: no tweening package is added.

Loading/busy feedback appears in the active card, disables repeatable actions, and leaves navigation/back behavior consistent with the current network safety rules. Error, success, and neutral status messages use distinct semantic colours rather than one generic red line.

## Implementation boundaries

- `BuildMenuCanvas.cs` owns visual tokens, generated decorative elements, layouts, navigation wiring, and scene regeneration.
- `MenuKeyboardNavigator.cs` owns reusable Tab/Shift+Tab/Enter/Escape behavior.
- `MenuScreenTransition.cs` owns panel entrance/exit presentation only.
- Existing menu screen scripts remain responsible for collecting values, local validation, and calling `MenuScreenController`.
- `MenuScreenController` remains responsible for flow-driven visibility, busy state, and session feedback.
- `ProjectSettings.asset`, `Splash.unity`, and legacy menu copy/URLs are cleaned of inherited player-facing branding.

## Error handling and accessibility

- Missing optional navigation references degrade safely: the screen remains mouse-usable and logs a precise setup warning in development builds.
- Missing required generated references fail the existing wiring/build checks.
- Text and controls target WCAG-style readable contrast, minimum practical 1080p body size, visible selected state, and colour-independent status cues where space allows.
- Layout uses the existing `CanvasScaler` and anchored cards so ultrawide and smaller 16:9 windows do not push actions off-screen.

## Testing and verification

Development follows test-first behavior changes.

- EditMode tests cover forward/backward wrapping, skipping disabled controls, primary submit, back/cancel, initial focus, and duplicate-submit protection.
- Static/editor tests assert the new company/product identity and reject player-facing `Ravenfield`, `SteelRaven7`, and old-author copy in active scenes/settings/menu code.
- Existing client-flow and master-session tests must remain green.
- Regenerate `Menu.unity` through `BuildMenuCanvas`; do not edit generated multiplayer-menu YAML by hand.
- Run the Unity EditMode suite, the relevant .NET test projects, a Unity compile/build check, and a manual 1920×1080 keyboard walkthrough of title → login → register → lobby → rooms → room lobby.

## Non-goals

- Replacing gameplay art, weapons, maps, animation, or the in-match HUD.
- Reworking authentication or multiplayer protocols.
- Removing internal historical references required to understand parity with the original codebase.
- Introducing localization, controller navigation, a new UI framework, or third-party UI packages in this pass.

