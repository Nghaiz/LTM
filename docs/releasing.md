# Releasing a player build

How to turn `develop` into a zip that anyone can download from the GitHub Releases page,
extract, and play by double-clicking `Ironfront.exe`. No Unity on their side, no `.env`, no
launcher.

This page is for whoever cuts the release. The players' own instructions are
[`tools/release/README.txt`](../tools/release/README.txt), which ships inside the zip.
[handing-over-a-build.md](handing-over-a-build.md) covers the narrower case of giving one
teammate a build folder; this page covers everyone else.

---

## 1. Why a bare exe works

A downloaded build has nothing but what was compiled and serialized into it, so three things
have to be true without any help from the environment:

| Needs | Where it comes from |
|---|---|
| The master's address | `ClientFlowBootstrap` in `Menu.unity`: `kien-master-2026.fly.dev:443`, TLS on. The constants are `ClientFlowBootstrap.PublicMasterHost` / `PublicMasterPort`, and `ReleasedClientMasterEndpointTests` fails CI if the scene, the field initializers or `play-lan.ps1` disagree with them |
| The game server's address | The master sends it with the room join, alongside a signed ticket. Moving or resizing the Azure game servers never needs a new client release |
| The client role | `ClientFlowBootstrap` declares the process a client on the shipped route into a match, before the map loads. `IRONFRONT_ROLE` is only needed for launches that skip the menu |

The one secret in the system, `IRONFRONT_SHARED_SECRET`, is read by the master and the game
servers only. A client never needs it, and a zip that contained it would let anybody mint
match tickets. `package-release.ps1` refuses to package any `.env`, key or certificate for that
reason.

Environment variables and a `.env` still override everything above, which is how developers
point a build at another master.

---

## 2. Cut the release

From a clean checkout of the commit you mean to ship (normally `develop` after merging it into
`main`):

```powershell
git switch develop; git pull --ff-only
git status --short                               # must be empty
pwsh .claude/scripts/unity-editor.ps1 close       # the build needs the Editor closed
pwsh tools/build-player.ps1                      # release player; prints "[build] stamp : <sha> ..."
pwsh tools/package-release.ps1 -Version v1.1.0   # zip into artifacts/release/
```

**The release player (since 2026-10-02).** `build-player.ps1` builds, by default, a release build
(no `BuildOptions.Development`) on **IL2CPP** with `Net/Diagnostics` compiled out. Every zip up to
v3.1.1 was the lane-B harness's Development build on Mono: "Development Build" printed on every
screen, profiler hooks compiled in, and the harness's scripted aim and input inside. It needs two
things on the build machine, both one-time installs:

- the **Windows Build Support (IL2CPP)** module for the project's Unity version, added through
  Unity Hub (Installs → Manage → Manage modules), never by running the module installer directly;
- the **Visual Studio Build Tools** with the C++ workload (MSVC), which IL2CPP compiles with.

The first IL2CPP build spends several extra minutes in the C++ compiler; later builds reuse
`Library/Bee`. `-Development` still builds the old development player on Mono (what the Unity
Profiler attaches to); `-KeepDiagnostics` builds the release player with diagnostics kept, for
measuring it with `IRONFRONT_LOG_FRAMES=1`, and `package-release.ps1` refuses to package it.

`build-player.ps1` rebuilds the tracked plugin DLLs, which leaves them modified in the working
tree with new PE identities and no source change. Discard them afterwards
(`git checkout -- Ironfront_Reborn/Assets/Plugins`), never commit them from a release build.

`package-release.ps1`:

1. refuses anything but the release player: it must be IL2CPP (`GameAssembly.dll`), not a
   Development build (no `player-connection-*` in `boot.config`), with `Net/Diagnostics` compiled
   out (no `LaneBHarness` in `global-metadata.dat`),
2. checks the build is stamped with the commit being released (`-Commit`, default `HEAD`),
3. refuses `.env*`, `*.pem`, `*.key`, `*.pfx`, `*.p12`, `*.log` and `credentials*` anywhere in
   the build folder,
4. copies the build without `*_BurstDebugInformation_DoNotShip` and IL2CPP's
   `*_BackUpThisFolder_ButDontShipItWithYourGame` (generated C++ and symbols),
5. adds `README.txt` with the version and commit filled in,
6. writes `artifacts/release/IronfrontReborn-<version>-windows-x64.zip` and prints its size and
   SHA-256.

---

## 3. Test the zip, not the build folder

The zip is what players run, and the build folder can pass where the zip fails: a `.env` in any
parent directory of `build/windows` is picked up by `DotEnv.LoadFromAncestors`, and so are
`IRONFRONT_*` variables left in your shell. Either one hides a wrong default.

1. Extract the zip somewhere with no `.env` above it (e.g. `%TEMP%\ironfront-release-test`).
2. Start two copies from a shell with every `IRONFRONT_*` variable removed, or from Explorer.
3. Each log must say, near the top:
   ```
   [flow] master = kien-master-2026.fly.dev:443 (TLS, cert name 'kien-master-2026.fly.dev', no pin).
   [net] build <sha> built <time> (client, shared assembly)
   ```
4. Sign both in, put them in one room, ready both, deploy both. Two players are the minimum for
   a room to start.

---

## 4. Publish

```powershell
pwsh tools/package-release.ps1 -Version v1.1.0 -Publish           # tag on main
pwsh tools/package-release.ps1 -Version v1.1.0 -Publish -NotesFile notes.md
```

`-Publish` runs `gh release create <version> --target main` with the zip attached and prints
the release URL. GitHub caps a release asset at 2 GiB; a build is a few hundred MB.

Tags follow semantic versioning, `vMAJOR.MINOR.PATCH`. **v1.0.0 (2026-09-28, built from
`9836e90`) is the first stable release.** From there:

| Bump | When | Players |
|---|---|---|
| MAJOR | the wire protocol version changes | old clients are refused by the servers and must download again |
| MINOR | new features, still protocol-compatible | old clients keep working |
| PATCH | fixes, still protocol-compatible | old clients keep working |

---

## 5. When the servers change

| Change | New release needed? |
|---|---|
| Game servers moved, resized, redeployed | No. The master hands out their address |
| Master redeployed at the same address | No |
| Master moved to a new host name or port | **Yes.** Change `PublicMasterHost` / `PublicMasterPort`, update `Menu.unity` and `play-lan.ps1` (the test names all three), release again |
| Wire protocol version bumped | **Yes, as a new MAJOR.** Old clients are refused |

---

## 6. Known gaps

- The exe is not code-signed, so SmartScreen warns on first launch. The player README says how
  to get past it. Signing needs a certificate the project does not have.
- There is no auto-update. Players download each release by hand.
- The macOS player is Mono, not IL2CPP, and is not notarized (§ 7).

---

## 7. macOS

Since 2026-10-07 the same release can carry a macOS zip beside the Windows one. It is built on
this Windows machine:

```powershell
pwsh .claude/scripts/unity-editor.ps1 close
pwsh tools/build-player.ps1 -Platform macos                    # build/macos/Ironfront.app
pwsh tools/package-release.ps1 -Platform macos -Version v4.5.0 # IronfrontReborn-v4.5.0-macos.zip
```

What is different, and why:

| | Windows | macOS |
|---|---|---|
| Module | Windows Build Support (IL2CPP) | **Mac Build Support (Mono)**, added through Unity Hub |
| Backend | IL2CPP | **Mono**. IL2CPP for macOS links with Apple's toolchain and can only be built on a Mac |
| CPU | x64 | **universal**: x86_64 + arm64 in one binary, checked by `package-release.ps1` |
| Signature | none (SmartScreen warns) | Unity's **ad-hoc** signature, written on Windows too. Apple silicon will not start unsigned code. Not notarized, so Gatekeeper blocks the first launch until the player clicks "Open Anyway" |
| Zip | `System.IO.Compression` | `tools/release/zip_unix_release.py macos`, which records Unix permissions. A zip made by .NET on Windows drops the executable bit and the app does not open |
| First build | minutes | far longer: every shader compiles for Metal once; later builds reuse `Library/` |

**This machine cannot run the result, so a Mac does.** `.github/workflows/macos-smoke.yml` takes
the zip from a release (a draft is fine), unzips it with `ditto` as Finder would, checks the
executable bit, both architectures and `codesign --verify --deep --strict`, then starts the player
natively and under Rosetta: a minute at the menu, then a practice match entered from the keyboard
(Tab, Return, Return), left running against bots for 90 s, then DEPLOY clicked. It then asks the
player to quit, because the macOS player writes its log out only on a clean quit, and requires the
`[flow] master`, `title screen ready`, `plays offline` and `[vegetation] 'Terrain` lines, no
exception line and exit status 0. Practice is offline, so its log has no `[net] build` line; the
stamp is checked in the DLL by `package-release.ps1`. Logs and a screenshot are kept as artifacts. Before a zip is
public:

```powershell
gh release create macos-smoke-<n> --draft --title "macOS smoke <n>" artifacts/release/<zip>
git tag macos-smoke-<n>; git push origin macos-smoke-<n>     # the tag push starts the run
```

For a published release: `gh workflow run macos-smoke.yml -f release=<tag>`. Delete the draft and
the tag afterwards.

`-Publish` adds the zip to the release when it already exists (the Windows zip went first),
otherwise it creates it. A batch macOS build switches the Editor's target back to Windows when it
ends, so the next Editor opens where it always does.

## 8. Linux

Since 2026-10-09 (owner's list of 2026-10-09: "win, mac, linux") the release also carries a Linux
zip. It is built on this Windows machine too:

```powershell
pwsh .claude/scripts/unity-editor.ps1 close
pwsh tools/build-player.ps1 -Platform linux                    # build/linux/Ironfront.x86_64
pwsh tools/package-release.ps1 -Platform linux -Version v4.6.0 # IronfrontReborn-v4.6.0-linux-x64.zip
```

| | Windows | Linux |
|---|---|---|
| Module | Windows Build Support (IL2CPP) | **Linux Build Support (IL2CPP)** |
| Backend | IL2CPP | **IL2CPP**, cross-compiled from Windows with the `com.unity.toolchain.win-x86_64-linux` package in `Packages/manifest.json`. Not a choice: Unity ships no non-development Mono player for Linux |
| CPU | x64 | x86_64; `package-release.ps1` checks the ELF header |
| Native code | `GameAssembly.dll` | `GameAssembly.so` |
| Zip | `System.IO.Compression` | `tools/release/zip_unix_release.py linux`, which marks every ELF file 0755 |
| README | CRLF, UTF-8 with BOM | LF, UTF-8 without BOM (`tools/release/README-linux.txt`) |

**This machine cannot run the result, so a GitHub runner does.** `.github/workflows/linux-smoke.yml`
takes the zip from a release (a draft is fine), unzips it with `unzip`, checks the executable bit
and the ELF type, then starts the player on Xvfb with Mesa's software OpenGL (`-force-glcore`;
the runner has no GPU): the title screen, a practice match entered from the keyboard (Tab, Return,
Return) with `xdotool`, DEPLOY clicked, then SIGTERM. It requires the `[flow] master`,
`title screen ready`, `plays offline` and `[vegetation] 'Terrain` lines and no exception line, and
keeps the log and screenshots as artifacts. Before a zip is public:

```powershell
gh release create linux-smoke-<n> --draft --title "Linux smoke <n>" artifacts/release/<zip>
git tag linux-smoke-<n>; git push origin linux-smoke-<n>     # the tag push starts the run
```

For a published release: `gh workflow run linux-smoke.yml -f release=<tag>`. Delete the draft and
the tag afterwards. A batch Linux build switches the Editor's target back to Windows when it ends.
