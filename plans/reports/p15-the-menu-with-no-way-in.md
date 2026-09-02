# P15 — the menu with no way in

**Phase:** [`../phases/phase-p15-the-menu-with-no-way-in.md`](../phases/phase-p15-the-menu-with-no-way-in.md) ·
**Branch:** `feat/p15-menu-with-no-way-in` · **Date:** 2026-09-02

---

## 1. What was wrong

The Menu scene held two programs. The Canvas a player saw was the original single-player menu,
and no button, field or code path in `MainMenu.cs` touched the network stack. The multiplayer
shell was in the same scene, drew entirely from `OnGUI()` behind **Shift+F2**, and its component
fileIDs occurred exactly twice in the scene file — the YAML anchor and the GameObject's component
list. Zero `Button` `onClick` targets referenced it.

So the player's experience was correct and the code was correct, and they were different
programs. That is F1, ranked CRITICAL: *nothing else matters until a player can reach a match
without a hotkey.*

The gap was one layer above the protocol. `IMasterClient` already declared `RegisterAsync`,
`CreateRoomAsync`, `SetReadyAsync`, `SendChatAsync` and `MatchmakeAsync`, all implemented and
tested server-side. The Unity wrapper `MasterSession` exposed none of them — which is why
`run-e2e.ps1` has to open a second account through a harness to make a room.

## 2. What shipped

### 2.1 `MasterSession.RegisterAsync` (3.1)

The wrapper, in the shape of the `LoginAsync` wrapper beside it: same `LastError` and `OnError`
routing, same `MasterErrorText` phrasing, same `PasswordHasher.Hash(password, username)`.

**3.1's open question, answered in code and recorded here: a successful register does NOT log
in.** It returns to the login form with the username pre-filled. That buys one fewer state
transition to get right — there is no edge in the table for "already Authenticating when the
register answers" — and it confirms to the player that the account now exists. `IsLoggedIn` is
unchanged by the call in both directions, because a register response carries no session token;
that is stated on the method because it is the post-condition a caller is most likely to assume
the other way.

### 2.2 The Canvas (3.2)

`Title`, `Login`, `Register`, plus the `Authenticating` and `Lobby` screens those two have to
land on. Multiplayer is the primary action; Practice is secondary.

**The screen state is `GameFlowState`. No new enum was added** (constraint 3). The mapping:

| State | Screen |
|---|---|
| `Booting` | Title |
| `LoginScreen` | Login, and Register as a sub-view |
| `Authenticating` | "Signing in…" |
| `Lobby` | Signed-in readout (P16 replaces it with the room browser) |

**Register is a sub-view rather than an eleventh state** because it has none of the ten and the
phase forbids inventing one. `_registerRequested` is a boolean sub-mode read only while the flow
says `LoginScreen`, cleared on the way out — it cannot disagree with the flow machine about where
the player is, which is the property a rival enum would lose.

**`Booting` was reopened, and that is a deliberate reversal.** `ClientFlowBootstrap` used to
transition `Booting → LoginScreen` in its own `Awake`, with the comment that the shell's Start
button existed only "to admit they had launched the game" — true of a debug overlay whose Booting
screen had nothing on it. A Title screen is not that. The edge is unchanged and still the only
one out of `Booting`; `MenuScreenController.GoToMultiplayer` takes it now, and the shell's own
Start button still takes the same one, so the two agree. No test pinned the auto-advance —
`ClientFlowBootstrap` is a `MonoBehaviour` and out of `dotnet test`'s reach.

### 2.3 The two seams (3.3)

Both follow the eleven that already exist: interface in `Net/Shared`, nullable slot on
`NetClientBindings` (cleared in `ResetOnLoad`), implementation in `NetBindings/`.

- **`ITeamPalette`** over `ColorScheme.TeamColor`, returning packed `0xRRGGBB`. An `int` rather
  than a `Color` so alpha and colour-space questions stay on the side of the seal where the type
  that answers them lives. Unregistered answers a neutral grey, not a guessed blue or red — a
  wrong team colour that looks plausible is an invisible failure.
- **`IPracticeLauncher`** over the legacy offline start. **It shows a screen; it does not start a
  match.** `MainMenu.StartLevel` reads its own authored toggles, fields and bot-balance slider, so
  the offline game is unchanged here precisely because nothing reproduces it. A
  `Launch(scene, actorCount, botBalance, …)` signature would have needed a fourth screen the phase
  does not scope (3.2 names three) and a second copy of a shipped one, and criterion 5 would then
  have to be re-proven against new controls instead of being true because nothing moved.

`HidePracticeMenu` exists as a method rather than a `SetActive` at the call site because
`MainMenu.Update` re-asserts `menuContent.SetActive(...)` every frame — only deactivating the
object carrying `MainMenu` keeps it down, and which object that is, is not something a caller
that cannot name the type could get right.

### 2.4 Authoring (3.2 constraint 1)

`BuildMenuCanvas` is an Editor command; no scene YAML was hand-written, so every fileID is one
Unity minted. Re-running rebuilds its own subtree deterministically and touches nothing else —
proven, not asserted: the second run logged `rebuilding: removed the previous 'Multiplayer Menu'`
and the gate stayed green across the new fileIDs.

**Two consequences of the seal being two-way, both discovered by hitting them:**

- The builder lives in `Ironfront.Net.Unity.EditorHarness`, not `Assets/Editor` proper.
  `Ironfront.Net.Unity.Client` ships `autoReferenced: false`, so `Assembly-CSharp-Editor` cannot
  name `MenuScreenController`. That asmdef exists because C5b hit exactly this; this is its second
  occupant.
- **`MenuSceneBindings` is a static installer, not a component the builder adds** — an asmdef
  cannot reference the predefined assembly, so the builder could not add it even though it adds
  every other component. Process-wide registration is safe here because
  `LegacyPracticeLauncher.Resolve` caches through a `MainMenu` reference and a destroyed one
  compares null, so leaving the Menu scene drops the cache and `IsAvailable` re-answers false with
  no teardown to forget. It installs at `BeforeSceneLoad`, because
  `NetClientBindings.ResetOnLoad` wipes every slot at `SubsystemRegistration`.

### 2.5 One thing the phase did not list

**`UnityEngine.UI` had to be added to two asmdefs.** Contracts § 6.1 says it "is in neither camp
and is available to both", which is true of the seal but not of the reference graph:
`autoReferenced: true` only auto-references an assembly into *predefined* assemblies, so an asmdef
must list it explicitly. No `Net/` assembly referenced it before this phase, because none had
drawn anything. Worth carrying into P16 and P17.

## 3. Acceptance

| # | Criterion | Result |
|---|---|---|
| 1 | Splash → menu whose primary action is Multiplayer, no hotkey | **MET** — screenshot; Splash advanced on its own, Title the only live panel |
| 2 | Account registered from the UI on a fresh master, then logs in | **MET** — two screenshots + DB row |
| 3 | Wrong password renders a clear error | **MET** — screenshot, "Wrong username or password." |
| 4 | Register hashes with the same `PasswordHasher` as login | **MET** — proven by 2 end to end, plus 10 unit tests |
| 5 | Practice starts the offline match; slider still splits the teams | **MET** — 20 bots at 0.25 → `team0Bots=15, team1Bots=5` |
| 6 | Every new screen gated by a detector observed RED | **MET** — 4 mutations, § 4 |
| 7 | `check-net-layering.ps1` green | **MET** — no new names, no stale rows |
| 8 | `LobbyShellOverlay` still works; nothing deleted | **MET** — § 5 |
| 9 | `tools/ci.ps1` green | **MET** — full run incl. the Unity compile leg |

**Criterion 2's evidence, from the master's own database** — note `last_login_at` is `None`,
which is the register-does-not-log-in decision seen from the far side:

```
accounts: player_id=1  username='p15pilot'  password_hash='$2a$11$…'
          display_name='P15 Pilot'  last_login_at=None
```

The second account (`p15pilot2`, `player_id=2`) was registered through the UI and then signed in
through the UI, and the Lobby screen rendered `Signed in as Second Pilot (#2)`.

**A raycast check, not just a wired listener.** Two overlapping Canvases is a failure this phase
could have introduced, so the Multiplayer button was checked with `EventSystem.RaycastAll` at its
own screen position before being clicked: 3 hits, top hit is the button's own caption at sorting
order 100. The button is reachable, not merely connected.

## 4. Mutation results (criterion 6)

`MenuScreenRefsAreAssigned` grades all 25 references across the four screens on three clauses,
not one — the shape `ScoreUiTextRefsAreAssigned` earned by being proved green, by mutation, on two
authorings it exists to forbid. Each mutation below was applied to the **real** `Menu.unity`, the
gate run, and the scene restored.

| # | Mutation | Result |
|---|---|---|
| M1 | `MenuTitleScreen._multiplayerButton` → `fileID: 0` | **RED** — "unassigned, so no listener is added to the primary action… (criterion 1)" |
| M2 | `MenuLoginScreen._errorText` → `fileID: 999999999` | **RED** — "which no object in Menu.unity carries. Unity loads that as null…" |
| M3 | `MenuRegisterScreen._createButton` = `_backButton` | **RED** — both directions reported |
| M4 | `MenuScreenController` `m_Script` → unknown guid | **RED** — "on no GameObject… that screen does not exist" |

Exit code 1 on all four; 12 authoring checks clean on the restored scene (was 11 before this
phase).

**What these deliberately do not check: where a control sits.** A reference pointing at a genuine,
unclaimed Button that lives on a different screen passes every clause and is still wrong. That is
the same boundary `ScoreUiTextRefsAreAssigned` draws — YAML can say a reference resolves, never
that a player can see or reach it. Criteria 1, 2, 3 and 5 are screenshots because of exactly this
gap, and the raycast check above is the one part of it that could be closed cheaply.

## 5. Nothing was deleted (criterion 8)

`LobbyShellOverlay`, `MainMenu.cs` and the legacy Canvas are all present and all still work.

- The shell starts **hidden** and `Show()` restores it, drawing `state: Booting` — **bound**, not
  "Lobby shell: unbound", so `ClientFlowBootstrap.Bind` still reaches it. It remains the only route
  to the room browser until P16.
- The legacy Canvas is **deactivated, not removed**, and `IPracticeLauncher` reveals it. Practice
  reached it, its shipped first-run news and greenlight screens appeared unchanged, and the match
  started from its own buttons.
- `MainMenu.cs` was not edited.

The shell's scene value did change from `_visible: 1` to `0`, and that is a change worth naming
rather than burying: it was visible because it *was* the user interface, and with a Canvas behind
it an IMGUI panel over the Title screen is the first thing a player sees. The phase's own
description of it — "behind Shift+F2" — had stopped being true of the shipped scene. The C# field
initializer stays `true`, so a shell dropped into a scene with no menu still draws itself.

## 6. Three faults only running it could find

None is visible in a diff, and no gate in this repository would have caught any of them. They are
the argument for criteria 1–3 and 5 being screenshots.

**The menu hung on a wrong password.** `MasterSession` awaits with `ConfigureAwait(false)`, so
`OnError` — and everything after the `await` in `Submit` — resumes on a thread-pool thread.
`GetComponentsInChildren`, `SetActive` and `Text.text` are all main-thread-only, and the
`UnityException` they throw was raised **inside `MasterSession.Fail`**, aborting `LoginAsync`
before it reached `Recover(LoginScreen)`. The flow stranded in `Authenticating` with the correct
error text set and no way back to the form.

`MasterSession`'s own remark — "no thread marshaller, and the reason is not optimism" — is right
about *polled pushes* and silent about *awaited requests*. `LobbyShellOverlay` never met this
because its callback assigns a string field and nothing else; IMGUI reads it on the main thread a
frame later. A Canvas has no such separation. `MenuScreenController` now sets flags from every
callback and touches Unity objects only in `Update()`.

**A success rendered in the failure colour.** "Account 'p15pilot' created" went to the error label
and came out red — telling the player, in the colour reserved for refusals, that the thing that
just worked had not. Same label and the same single error vocabulary (constraint 4 forbids a rival
*error* surface, and a confirmation is not one), with the authored colour captured for failures.

**The debug shell covered the new menu** — § 5.

**One behavioural consequence of the marshaller, recorded because it will bite a harness:** a
screen change now lands on the next frame rather than inside the click handler, so two clicks
cannot be chained within a single frame. Irrelevant to a player; it cost one confusing run here,
where a scripted Multiplayer-then-LOG-IN in one frame was silently swallowed because the Login
panel was still inactive.

## 7. What P16 inherits

- **The screen mechanism.** `MenuScreenController.Apply` is one place; `RoomBrowser`,
  `JoiningRoom` and `RoomLobby` are three more rows keyed on states that already exist.
- **`ITeamPalette`**, so the roster columns do not invent a hardcoded blue and red.
- **The detector table.** `MenuScreenWiringDetectors.Screens` takes a new entry per screen; the
  three clauses come free.
- **`UnityEngine.UI` on the asmdefs** — already added (§ 2.5).
- **The marshalling rule.** Any new screen that reaches the session must set a flag, not touch a
  Unity object, in a callback.
- **`LobbyShellOverlay`'s retirement**, which is P16's last task and still gated on the room
  browser existing.

Still out of scope and still zero Unity callers after this phase: `CreateRoomAsync`,
`SetReadyAsync`, `SendChatAsync`, `MatchmakeAsync`.

**One known limit.** The Canvas menu dials the master in plaintext: `GameClientConfig` carries no
TLS fields, so `MenuScreenController.MasterTls` stays null and TLS remains the shell's serialized
fields. That matches the config's own coverage rather than adding a surface no phase has scoped,
but it is a real gap for a public deployment and belongs on the ledger.
