# Handing over a build

How to give somebody else a playable Ironfront build, and how either of you confirms they are
running the one you meant. Written for the case where one person hosts and the others join.

This is not [operations.md](operations.md). That runbook is the Azure VM, containers and the
master server. This page is one developer handing a Windows build to a teammate.

---

## 1. The one thing that goes wrong

**A game server runs code from several DLLs, and `Assembly-CSharp.dll` is not the interesting
one.** The netcode lives in its own assemblies:

```
build/windows/Ironfront_Data/Managed/
├─ Assembly-CSharp.dll                 the imported game (movement, weapons, UI)
├─ Ironfront.Net.Unity.Server.dll      spawn placement, tick loop, authority   <- server fixes land here
├─ Ironfront.Net.Unity.Client.dll      prediction, HUD, deploy flow
├─ Ironfront.Net.Unity.Shared.dll      the seams both sides share
└─ Ironfront.Net.Replication.dll …     the protocol libraries
```

Somebody told "the spawn bug is fixed" reasonably reaches for the file whose name sounds like the
game. On 2026-09-05 the X-89 and X-90 spawn fixes were entirely inside
`Ironfront.Net.Unity.Server.dll`; a host who replaced `Assembly-CSharp.dll` would have kept every
line of the old placement code and seen the identical symptom, with no error anywhere to suggest
why.

> **Give the whole `build/windows/` folder. Never individual DLLs.** There is no case where
> copying one file is the right answer, including the case where you are sure you know which file
> changed.

---

## 2. Cut the build

The Editor must be closed — the project lock, and a queued recompile the build would otherwise
wait on for ever.

```powershell
pwsh .claude/scripts/unity-editor.ps1 -Stop     # if one is open
pwsh tools/build-player.ps1                      # ~10 minutes, silent until it ends
```

It prints the stamp it baked in:

```
[build] stamp : 9f3a1c2 2026-09-06T10:04:11Z
```

Two warnings are worth stopping for:

| Warning | What it means | Do this |
|---|---|---|
| `the tree has uncommitted changes` | the SHA names a commit the binary does **not** match | commit or stash, rebuild |
| `no git commit could be read` | the binary will report `dev` and be unidentifiable later | build from a real checkout |

**Judge the build by the managed DLLs, not by `Ironfront.exe`.** Unity keeps the executable and
rewrites the assemblies, so a green build routinely leaves the `.exe` timestamp untouched. The
script prints `Assembly-CSharp.dll last written` for this reason.

---

## 3. Deliver it

Zip `build/windows/` whole and send the archive, along with the stamp line the build printed. The
receiver unzips to a **fresh folder** — not over an existing one.

Unzipping over an old build is the same failure as copying one DLL, one level up: anything the new
build no longer produces survives in the folder, and you get a mixture that was never tested.
Delete the old folder or unzip beside it.

---

## 4. Confirm they are running it

Both sides print their identity at startup. Ask the host for the line, and compare it to the stamp
you sent.

The server prints both of its assemblies:

```
[net] build 9f3a1c2 built 2026-09-06T10:04:11Z (server assembly); shared assembly 9f3a1c2 built 2026-09-06T10:04:11Z
```

A client prints the one it can see:

```
[net] build 9f3a1c2 built 2026-09-06T10:04:11Z (client, shared assembly)
```

Read it like this:

| Line says | Meaning |
|---|---|
| the SHA you sent | they are running your build |
| a **different** SHA | an older build; send them the folder again |
| `-dirty` suffix | built from an uncommitted tree; the SHA is not the whole truth |
| `dev (built from the Editor…)` | not built by `build-player.ps1` — either their own Editor build, or a build predating the stamp |
| `MIXED BUILD FOLDER` (an error) | two assemblies from different commits — somebody copied files rather than replacing the folder. Nothing measured on this process is trustworthy |

Where the lines appear: the server writes them to its console and to its log; a client writes them
to `%USERPROFILE%\AppData\LocalLow\SteelRaven7\Ravenfield\Player.log`. That path is not a typo —
`companyName` and `productName` in `ProjectSettings.asset` are still the base game's, so Unity
puts the log where Ravenfield's would go.

**The stamp only speaks for builds cut after it shipped.** Anything older reports `dev`, which is
indistinguishable from a developer's own Editor build. For that first exchange there is no
instrument — send the folder, have them delete the old one, and take the new stamp line as the
starting point.

---

## 5. Play a match

Once everybody has the same build, `tools/play-lan.ps1` and `tools/playtest-local.ps1` stand up
the stack. `playtest-local.ps1 -Clients 4` runs master, game server and four client windows on one
machine, which is the quickest way to confirm a build works before anybody else downloads it.

---

## 6. Related

- [operations.md](operations.md) — the deployed master server and game servers on Azure
- [unity-setup.md](unity-setup.md) — getting the Editor and the project to build at all
- `tools/build-player.ps1` — the build; the stamp rewrite lives here
- `Ironfront_Reborn/Assets/Scripts/Net/Shared/BuildStamp.cs` — why the stamp is compiled into the
  assembly rather than written beside the executable, and why the fields are `static readonly`
