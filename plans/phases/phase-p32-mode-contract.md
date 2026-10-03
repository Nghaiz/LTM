# P32 — Game-mode settings: the contract every side implements

The one shape the lobby form, the master, the game server and the client HUD agree on for
item 1 of `phase-p32-modes-bots-perf.md`. Protocol **14**.

## Values

| Name | Type | Values | Default |
|---|---|---|---|
| `gameMode` | u8 | `0` Point Match, `1` Night Mode | `0` |
| `victoryRule` | u8 | `0` Margin (lead by N), `1` Target (first to N) | `0` |
| `victoryPoints` | u16 | Margin: 50-1000, step 10. Target: 100-3000, step 50 | `200` (Margin), `500` (Target) |
| `nightVisionSeconds` | u8 | Night Mode: 10-180, step 5. Point Match: `0` | `45` |

- **Night Mode is Forest Lake only** (`MapCatalog` id of Forest Lake). The master refuses it on any
  other map; the form never offers it there.
- Night Mode keeps the conquest score and the victory rule: night is a condition, the scoring is
  the same.
- The ranges and defaults live once, in `Ironfront.Net.Protocol.RoomRules`; the form, the master
  and the server read them from there.

## Wire

**MSP (JSON, camelCase), added to:**
- `RoomCreateRequest`: `gameMode`, `victoryRule`, `victoryPoints`, `nightVisionSeconds`.
- each `RoomListResponse.rooms[]` entry and `RoomStatePush`: the same four.
- `GsRoomAssigned` (master to game server): the same four, beside `botsPerTeam`.

A missing field reads as its default (a v13 sender), so a field-by-field reader never fails; the
protocol version still moves because the UDP message below changes size.

Refusal: an out-of-range or map-mismatched setting answers `RoomCreateResponse` with
`errorCode = InvalidRoomSettings` (new `ErrorCode`, appended).

**UDP `S_MATCH_STATE` (0x45), size 10 -> 13**, three bytes appended after `victoryPoints`:

```
u8  phase | u16 score0 | u16 score1 | u16 phaseSecondsRemaining | u8 humanPlayerCount
| u16 victoryPoints | u8 victoryRule | u8 gameMode | u8 nightVisionSeconds
```

`MatchStateMessage.WinningTeam` decides through `ConquestScoreRule.Decide(score0, score1,
victoryPoints, victoryRule)`: Margin as today; Target = the first side at or above `victoryPoints`,
the higher score when both are, nobody on a tie. Elimination ends a Target match by raising the
winner to `victoryPoints`.

## Who owns what

| Side | Reads | Does |
|---|---|---|
| Lobby form | `RoomRules` | Mode picker, rule picker, points slider, night-vision battery slider (Night Mode only) |
| Master | request | Validates, stores on `Room`, echoes in the room list and room state, forwards in `GsRoomAssigned` |
| Game server | `GsRoomAssigned` | Applies rule and points to `MatchStateMachine`, night to the bots, sends all of it in `S_MATCH_STATE` |
| Client | room state before loading; `S_MATCH_STATE` after | Sets night before the map loads; HUD shows the rule; night vision with the room's battery |
