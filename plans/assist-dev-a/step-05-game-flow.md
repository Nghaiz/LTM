# Step 05 — The game-flow state machine

**Feeds** Dev A phase-03 task 1 · **Session size** medium · **Editor needed after** none

> Goal: the ten states between launching the game and the end of a match exist as a plain class with
> a transition table, unit-tested, and impossible to leave in an illegal state.

---

## Why it is a plain class, not a `MonoBehaviour`

Phase-03 sketches `GameFlowController : MonoBehaviour`, and phase-03 acceptance criterion 9 asks for
*"unit tests for `GameFlowController`"*. **Those two cannot both happen in this repository.** The
`.NET` test projects cannot reference `UnityEngine`, and the Unity Test Framework is not set up —
`Assets/` contains no `.asmdef`. As sketched, criterion 9 is unimplementable.

Put the states, the transition table and the guard in a plain class in a `netstandard2.1` library.
Unity gets a `MonoBehaviour` that owns a reference to it and forwards events. Criterion 9 then costs
nothing, and this whole step needs no Editor.

## Deliverable

1. `GameFlowState` — `Booting, LoginScreen, Authenticating, Lobby, RoomBrowser, JoiningRoom,
   RoomLobby, ConnectingGame, InMatch, MatchEnd`.
2. The transition table, **declared in full**. Phase-03's sketch leaves it as `// ... declare them
   all`; the `stateDiagram-v2` block under [phase-03 task 1](../dev-a-unity-client/phases/phase-03-match.md)
   is the specification, including the failure edges (`Authenticating → LoginScreen`,
   `JoiningRoom → RoomBrowser`, `ConnectingGame → RoomLobby`, `InMatch → Lobby` on disconnect).
3. `Transition(next)` throwing on an illegal move, and a `OnStateChanged` event.
4. Tests: every legal edge passes, a representative illegal set throws, and the diagram has no state
   that cannot be reached or left.

## Why throwing is right here

This is the one place in the client where an exception beats a return code. An invalid transition is
a programming error, it happens once, and the alternative failure is the bug phase-03 names — *"we're
in the lobby but the match HUD is still showing"* — which has no error message and is found by
staring. `development-principles.md` § "Errors Over Silent Fallbacks" applies directly.

It does not contradict `conventions.md` § 3.2, which governs the packet path, where malformed input
is routine and expected.

## What this step proves, and what it does not

**Proves:** phase-03 criterion 9 outright, by `dotnet test`. This is the only Dev A acceptance
criterion in phases 00–03 that this track can close on its own.

**Cannot prove:** that the states are wired to anything. That is steps 06 and 07.

**Dev A checks:** nothing, until the `MonoBehaviour` adapter is attached in a scene — and even that
can wait, since step 07 drives the machine from IMGUI without a scene.

## Done when

- Every edge in the phase-03 diagram is declared and tested
- No `UnityEngine` reference
- Merged and green
