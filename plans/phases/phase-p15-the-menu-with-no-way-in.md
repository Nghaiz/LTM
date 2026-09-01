# Phase P15 — the menu with no way in

- **Plan:** [`../plan.md`](../plan.md) · **Block:** C (1 of 3) · **Size:** L · **Effort:** 1 session
- **Depends on:** nothing in this plan. Can land any time after P14, and does not read P11–P14's work.
- **Contracts:** [`../00-shared/team-multiplayer-contracts.md`](../00-shared/team-multiplayer-contracts.md)
  **§ 6 — the assembly seal. Read it before writing a single line of UI**; it decides which
  assembly every new file goes in, and `dotnet build` will not tell you when you get it wrong.
- **Filed:** 2026-09-01, from the player-facing audit's **F1**, ranked CRITICAL: *"no way in from
  the menu. Nothing else matters until a player can reach a match without a hotkey."*

---

## 1. Two programs sharing a scene

Build order is Splash → Menu → Island → Dustbowl (`EditorBuildSettings.asset:9,11,13,15`);
`GotoMenu.cs:27` advances Splash → Menu.

**The Menu scene's Canvas is the original single-player menu.** `MainMenu.cs:72-76`
`StartLevel(string) → Application.LoadLevel`, with assault/reverse/night/no-vehicles toggles, a
victory-score field, an actor count, and a bot-balance slider (`:44`) that splits
`ActorManager.team0Bots`/`team1Bots` (`:93-94`). **No button, no field and no code path in
`MainMenu.cs` touches the network stack.**

**The multiplayer shell is in the same scene and is invisible.** `LobbyShellOverlay` and
`ClientFlowBootstrap` are authored on the `Lobby Shell` GameObject in `Menu.unity`
(`:140282` name, `:140288`, `:140326`). The overlay draws entirely from `OnGUI()` (`:223`) behind
**Shift+F2** (`:204-205`), and its component fileIDs occur **exactly twice** in the scene file —
the YAML anchor and the GameObject's `m_Component` list. **Zero Button `onClick` targets
reference them.** Its own header says why (`:20-22`): *"not a replacement for the Canvas UI …
looking finished would only invite someone to ship it."*

So the player's experience is correct and the code is also correct. They are two different
programs.

### 1.1 What already works, and must not be rebuilt

The flow states and the transport are real and tested — `GameFlowState.cs:17-48` (ten states),
`GameFlowController.cs:113-146` (transition table), ~45 tests over the client flow. So is the
master client: **`IMasterClient` already declares `LoginAsync`, `RegisterAsync`, `GetRoomsAsync`,
`CreateRoomAsync`, `JoinRoomAsync`, `LeaveRoomAsync`, `SetReadyAsync`, `SendChatAsync`,
`MatchmakeAsync`, `OnRoomStatePush`, `OnChat`, `OnError`, `OnDisconnected`**
(`Ironfront.MasterClient/IMasterClient.cs:47-70`).

**This block is a wiring job, not a protocol job.** The gap is one layer up: the Unity wrapper
`MasterSession` exposes only `ConnectAsync`, `LoginAsync`, `OpenRoomBrowserAsync`,
`RefreshRoomsAsync`, `JoinRoomAsync`, `LeaveRoomAsync`, `EnterMatch`, `ConnectDirect`,
`LeaveMatch` (`MasterSession.cs:217-550`). **No `RegisterAsync`, no `CreateRoomAsync`, no
`SetReadyAsync`, no chat.** Scope searched: `Ironfront_Reborn/Assets/Scripts/Net/Client/MasterSession.cs`.

That is exactly the brainstorm's finding restated with the remedy attached: `Register`,
`RoomCreate`, `RoomReady`, `Chat` and `Matchmake` are implemented server-side with **zero Unity
callers**, which is why the E2E tool has to open a second account to make a room.

---

## 2. File ownership

```
Ironfront_Reborn/Assets/Scripts/Net/Client/MasterSession.cs         Register wrapper
Ironfront_Reborn/Assets/Scripts/Net/Client/Menu/**                  NEW — the screen components
Ironfront_Reborn/Assets/Scripts/Net/Client/ClientFlowBootstrap.cs
Ironfront_Reborn/Assets/Scripts/Net/Shared/ITeamPalette.cs          NEW seam (contracts § 6.3)
Ironfront_Reborn/Assets/Scripts/Net/Shared/IPracticeLauncher.cs     NEW seam (contracts § 6.3)
Ironfront_Reborn/Assets/Scripts/Net/Shared/NetClientBindings.cs     register the two seams
Ironfront_Reborn/Assets/Scripts/NetBindings/MenuSceneBindings.cs    NEW — the seam implementations
Ironfront_Reborn/Assets/Scenes/Menu.unity                           the new Canvas, via the Editor
Ironfront.Client.Flow.Tests/**
tools/ClientWiringGate/**                                            new authoring detectors
```

**Not owned:** the room browser, the create-room screen, the lobby room (**P16**); anything
in-match (**P17**); `MainMenu.cs` itself — Practice reaches it through a seam, and this phase does
not edit the legacy menu's own logic.

---

## 3. Tasks

### 3.1 — Wrap `RegisterAsync` on `MasterSession` (S)

`IMasterClient.RegisterAsync(username, passwordHash, displayName, ct)` exists and is tested
server-side. `MasterSession` does not expose it. Add the wrapper, in the shape of the
`LoginAsync` wrapper beside it (`:239`) — same error routing through `LastError` and `OnError`,
same `IsLoggedIn` post-condition question answered explicitly.

**Hash on the client.** `PasswordHasher` is already in `Net/Client/`, and `LoginAsync` takes a
`passwordHash`, not a password. Register must use the same function; two hashing paths is how an
account gets created that cannot log in.

**Answer one question in code and say so:** does a successful register also log in, or return to
the login screen with the username pre-filled? Pick the second — it is one fewer state transition
to get wrong, and it confirms to the player that the account exists. Record the choice.

### 3.2 — The new Menu Canvas (L)

A Canvas in `Menu.unity` with **multiplayer as the primary path** and Practice demoted, per the
owner's decision. Screens: **Title**, **Login**, **Register**. The room browser and lobby room are
P16 and get their own screens on the same Canvas — **build the screen-switching mechanism here so
P16 adds a screen rather than inventing a mechanism.**

Six constraints, each of which has already cost this project something:

1. **Author through the Editor, never by editing scene YAML.** fileIDs are Editor-assigned; a
   hand-written reference resolves to null while looking assigned (P3 § 3.3).
2. **`Net/Client` cannot name `MainMenu`, `GameManager`, `ColorScheme` or any legacy type.** See
   [contracts § 6](../00-shared/team-multiplayer-contracts.md#6-where-the-new-ui-code-lives--the-assembly-seal).
   Cross at the two seams in 3.3. **`dotnet build` will not catch a violation** —
   `check-net-layering.ps1` and the Unity compile will, and the script matches on *type name*, so
   even an unrelated `EventType` fails RULE 6a.
3. **The screen state is `GameFlowState`, not a new enum.** Ten states already exist with a tested
   transition table (`GameFlowController.cs:113-146`). A parallel `MenuScreen` enum would be a
   second state machine that drifts. `Booting`, `LoginScreen`, `Authenticating`, `Lobby` are this
   phase's four; P16 owns `RoomBrowser`, `JoiningRoom`, `RoomLobby`.
4. **Every failure must render.** A wrong password gives a clear error — that is an **M3
   acceptance clause** (`plan.md` § 2), and `MasterErrorText` already exists in `Net/Client/` for
   it. Use it; do not add a second error surface.
5. **Do not delete `LobbyShellOverlay` yet.** It is the only working path until P16 lands the room
   browser, and deleting the working path before the replacement is proven is the mistake P14 § 3.4
   explicitly sequences around. Its retirement is P16's last task.
6. **`ClientFlowBootstrap` already loads scenes and binds the session** (`:278-279,311`) — P8 wired
   it and P10 finished it. Extend it; do not write a second bootstrap.

### 3.3 — The two seams (M)

Per [contracts § 6.3](../00-shared/team-multiplayer-contracts.md#63-the-two-new-seams-block-c-needs).
Both follow the eleven that already exist (`IObjectiveHud`, `IMinimapMarkers`, `IHitmarkerHud`, …):
interface in `Net/Shared`, registered on `NetClientBindings`, implemented in `NetBindings/`.

- **`ITeamPalette`** over `ColorScheme.TeamColor`. P16's roster columns and P17's scoreboard rows
  both need it; building it here means neither invents a hardcoded blue/red.
- **`IPracticeLauncher`** over the legacy offline start. Practice is the demoted entry, and what it
  demotes to is `MainMenu`'s existing behaviour: set `GameManager` flags, split
  `ActorManager.team0Bots`/`team1Bots`, `StartLevel`. Keep the bot-balance slider — it is the one
  option in the legacy menu with no multiplayer equivalent, and losing it would be a removal
  nobody asked for.

**A null seam must degrade, not throw.** Every existing binding is nullable at the registry
(`NetClientBindings.Objectives?.…`) and this pattern is not optional: the Menu scene may load
before the bindings register.

### 3.4 — Authoring detectors, observed RED (M)

Standing rule 4. The nine existing `AssetWiringDetectors` checks passed `Ingame UI Container.prefab`
with `capturePointMarkerPrefab` **null** (P3 § 3.3) — an authoring gate that only checks the fields
it was told about is exactly as green as one that checks nothing.

So: a detector per screen asserting its Button `onClick` targets and its `Text`/`InputField`
references. Follow `ScoreUiTextRefsAreAssigned`, which exists in its current form **because three
mutations proved a weaker draft green**: assert the field is assigned, that its fileID names an
object that exists, and that the object is not one another field already drives.

Mutation-test each: unassign the reference, watch it go red, restore. Project memory: *a detector
is unverified until the real artifact is mutated and it goes red.*

### 3.5 — Multiplayer is the default path (S)

The Title screen leads with **Multiplayer**. **Practice** is present, secondary, and reaches the
legacy flow through `IPracticeLauncher`.

**Do not delete `MainMenu.cs` or its Canvas in this phase.** Practice still runs through it, the
offline game is a shipped feature, and removing a working screen in the same phase that adds four
new ones makes a failure impossible to bisect. Its retirement, if any, is a later decision and
belongs on the ledger, not here.

---

## 4. Acceptance

| # | Criterion | Evidence |
|---|---|---|
| 1 | **Screenshot: launching the game and clicking through Splash reaches a menu whose primary action is Multiplayer.** No hotkey, no config file | screenshot |
| 2 | **Screenshot: an account is registered from the UI, on a master that has never seen it**, and the new username then logs in | two screenshots + the master's DB row or log |
| 3 | **Screenshot: a wrong password renders a clear error on screen** — the M3 clause, graded on the pixels | screenshot |
| 4 | Register hashes with the same `PasswordHasher` as login; an account created in the UI logs in through the UI | criterion 2 proves it end to end |
| 5 | Practice starts the offline bot match, and the bot-balance slider still splits the two teams | screenshot of an offline match with a non-50/50 split |
| 6 | Every new screen's references are gated by a detector **observed RED** against an unassigned field | mutation results in the report |
| 7 | `check-net-layering.ps1` green — no `Net/Client` file names an Assembly-CSharp type | `tools/ci.ps1` |
| 8 | `LobbyShellOverlay` still works; nothing was deleted | stated in the report |
| 9 | `tools/ci.ps1` green | CI |

Criteria 1, 2, 3 and 5 are screenshots because this phase's entire subject is *what is on screen*,
and `ClientWiringGate` retires on subscription (`GateRunner.cs:72-75`) — it was green throughout
the period when there was no way into multiplayer at all.

---

## 5. Risks

| Risk | L | I | Score | Mitigation |
|---|---|---|---|---|
| New UI written in the wrong assembly; discovered only at the Unity compile or in CI, after the Canvas is authored | 4 | 4 | **16** | Contracts § 6 is required reading at the top of this file; 3.3 builds the seams **before** the screens need them |
| A second screen-state enum beside `GameFlowState`; the two drift | 3 | 4 | 12 | Constraint 3 names `GameFlowState` and the four states this phase owns |
| Register and login hash differently; an account is created that cannot log in | 3 | 5 | **15** | Step 3.1 names the one hasher; criterion 4 is end-to-end, not two separate tests |
| Canvas authored by hand-editing YAML; references resolve to null while looking assigned | 3 | 4 | 12 | Constraint 1, and the detectors in 3.4 catch it after the fact |
| `LobbyShellOverlay` deleted early; no working path while P16 is unwritten | 2 | 4 | 8 | Constraint 5 and criterion 8 |
| Practice regresses because `MainMenu` was touched | 2 | 3 | 6 | 3.5 forbids editing it; the seam calls it |

Two at ≥ 15. Both are mitigated by doing something *first* — read § 6, build the seams; name the
hasher — rather than by testing afterwards.

---

## 6. Out of scope

- **The room browser, create-room, and the lobby room** — P16, on this phase's Canvas and this
  phase's screen mechanism.
- **In-match UI** — P17.
- **Deleting `MainMenu.cs` or the legacy Canvas.** Practice needs it.
- **`Matchmake`** (0x0030-0x0032). Still zero Unity callers after this phase; the room browser is
  the agreed path in.
- **Account recovery, email, profile.** Not in the agreed scope; the master has username +
  bcrypt-11 + 15-minute lockout + 5/min per-IP and that is the whole account model.
