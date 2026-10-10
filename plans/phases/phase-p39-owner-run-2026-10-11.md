# Phase P39: the owner's run of 2026-10-11 (v4.6.1 playtest fixes) -> v4.6.2

Status: **done 2026-10-11, v4.6.2 released.** One PR per item, each merged to `develop` before the
next started; game servers redeployed after the server-side fixes; at the end `main` was promoted
and v4.6.2 shipped for Windows, macOS and Linux. Owner rules for this run: single-threaded, no
subagents, references to big shooters for HUD/sound, clean old versions off the servers and the
local Docker Desktop at the end.

## The owner's list (2026-10-11, translated)

1. Review every bug from many v4.6.1 multiplayer and practice matches (friend logs in
   `tmp/friend-v4.6.1-logs`, and the logs on the owner's PC).
2. Island and Dustbowl in-match maps back to v4.6.0 and earlier; their Night Mode maps must work
   like Forest Lake's; keep the room-creation pictures.
3. Tab board column headers misaligned; redo the rank medal and the white player icon.
4. Achievements sharing pictures (MID-AIR / DOGFIGHT); review all.
5. A friend at 0 HP did not die and kept playing.
6. Global ranking: wins shown as 0, matches wrong, every column reviewed. Follow-up: "4 matches"
   is room joins, several rounds are played per room; the Tab board accumulates across rounds.
   Owner chose to reset the ranking.
7. Bots knocked down by a body hit are immortal while lying.
8. Weapons miss when aimed true, recoil must be the original game's ("don't invent"), bullet drop,
   balance; the guided launcher sometimes will not fire with a missile loaded, or fires only black
   smoke.

## What shipped

| Item | PR | Cause | Fix |
|---|---|---|---|
| 6 | #613 | Score tally kept across rounds; round credit gated on room membership that a dropped master link removed | Tally cleared per round; credit for every result player; empty room kept while its server has players; career season 2 (old season archived) |
| 2 | #614 | Baked hand-drawn pictures replaced the original live render | `originalLivePicture` on Island/Dustbowl, live night picture graded by `MinimapNightGrade` |
| 3 | #615 | Column widths and header anchors differed | Shared widths, centred heads, new `rank` / `soldier` glyphs |
| 4 | #616 | 33 badges reused glyphs | One glyph per badge, `check_unique_glyphs()` |
| 5 | #617 | Health byte truncated 0.x to 0 for a living actor | `ClampHealth` rounds up while alive |
| 7 | #618 | Client refused hits on a ragdoll-posed living body | `RemoteRagdoll.TryHitSegment` against the bones |
| 8a | #619 | An invented aim climb; hip fire as accurate as aimed | Original spring kick on the rounds (`RecoilKick`), hip/moving cones, judge allowance |
| 8b | #620 | Two independent locks; the client predicted a launch the server refused; the refused round never came back (resync threshold) | Clip settles once the server processed the shot's frame; server holds a pull 1 s for its lock; client shows the launch on the server's clip drop; seeker tones |
| 1 | none | Client logs clean; server rejections 86% accepted, `NoTarget` 8.4% = rounds in flight after a kill | Covered by 5-8 |

## Evidence

- Live repro of 8b against the Dustbowl server: `tmp/javelin-live/run2` (old build: tap at 2.2 s
  spent the client's round, server kept it, client stuck at 0 vs 1) and `run3`/`run4` (new build:
  no prediction, launch shown on the server's drop, clips agree, tones heard). The tap at the exact
  lock moment was not reproduced live: the scripted aim found no lockable target in two tries.
- The lane-B `switchWeaponSlot` reaches the server only, never the local engine body; switch the
  engine yourself (`Actor.SwitchWeapon`) to test client-side weapon code.
- Server logs before each deploy: `tmp/azure-logs/9fd29e8-20261011-*`, `7573e04-20261011-*`.

## Deploys and cleanup

- Master `7573e04` (season 2). Game servers `7f6da77` (= develop `bbcf673e` tree), ids Dustbowl 5,
  Island 4, Forest Lake 6; rollback `7573e04`.
- Azure VM: `9fd29e8`, `3a14481` removed (disk 5.2 -> 4.1 GB). Local Docker: six old server images
  and 76 Ironfront build-cache records removed. GHCR: master versions pruned to the release tags,
  develop, main, the 10 newest and the Fly digest.
