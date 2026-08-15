# Step 07 — Login and room list, drawn from code

**Feeds** Dev A phase-03 task 2 and phase-02 task 7 · **Session size** medium · **Editor needed after** optional Canvas rework

> Goal: a login screen and a room browser that actually work, built entirely in C#, so the online
> flow can be demonstrated end to end before anyone opens Unity.

---

## The precedent is already in the repository

`Net/Diagnostics/TransportDebugOverlay.cs` draws its whole panel from `OnGUI()` — no Canvas, no
prefab, no serialized reference to wire, no scene to edit. It is a working component built the way
this step needs to be built, by someone on this project, which makes this route a house pattern
rather than a shortcut.

That is what makes phase-03 reachable without the Editor. Steps 05 and 06 supply the state machine
and the master connection; this step gives them a face, and the face costs no Editor time.

## Deliverable

1. **Login** — username, password, submit, error line. Drives `GameFlowState.LoginScreen →
   Authenticating → Lobby` from step 05, calling step 06's login.
2. **Room browser** — the list, a join button per row, and the failure path back to the browser.
3. **Connecting** — a line of text while the junction runs, plus the timeout message.
4. **A direct-connect field** — phase-03 UI item 14, *"network settings screen (manual IP entry)"*,
   marked *"Keep — useful for debugging"*. It is also the LAN fallback: with the game server running
   standalone (`IRONFRONT_MASTER_HOST` empty), a peer's RadminVPN address typed here is the whole
   LAN path. And it is phase-03's own stated contingency for the master not being ready.

## What this is and is not

**It is not a replacement for Dev A's UI.** It is ugly on purpose and should stay ugly, so nobody
mistakes it for finished. Its job is to prove the flow works and to unblock the ten-run login
handoff with Dev D.

**Its value is that it survives being replaced.** Because steps 05 and 06 hold every decision, the
Canvas version Dev A builds later swaps the drawing and keeps the logic — nothing here is rewritten,
it is deleted. If the logic had been written inside the UI, replacing the UI would mean writing it
twice.

Keep it behind a toggle the way `TransportDebugOverlay` keeps its overlay, so it cannot appear in
front of a real UI once one exists.

## What this step proves, and what it does not

**Proves:** the flow, by running it. Once this exists, `login → room list → join → connect` is
demonstrable from a build, which is phase-03 criterion 1 minus the presentation.

**Cannot prove:** anything about how it looks, which is the entire point of Dev A doing it properly
later.

**Dev A checks:** whether to keep it as a debug screen or replace it. Recommendation is keep — a
code-drawn direct-connect screen is worth having permanently for LAN play and for the case where the
master is down.

## Done when

- Login, room browser, connecting and direct-connect all draw and drive the step-05 machine
- No Canvas, no prefab, no scene edit, no `.meta` churn
- The toggle is documented
- Merged and green
