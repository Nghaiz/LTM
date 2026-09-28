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
pwsh tools/build-player.ps1                      # ~10 min; prints "[build] stamp : <sha> ..."
pwsh tools/package-release.ps1 -Version v0.1.0   # zip into artifacts/release/
```

`build-player.ps1` rebuilds the tracked plugin DLLs, which leaves them modified in the working
tree with new PE identities and no source change. Discard them afterwards
(`git checkout -- Ironfront_Reborn/Assets/Plugins`), never commit them from a release build.

`package-release.ps1`:

1. checks the build is stamped with the commit being released (`-Commit`, default `HEAD`),
2. refuses `.env*`, `*.pem`, `*.key`, `*.pfx`, `*.p12`, `*.log` and `credentials*` anywhere in
   the build folder,
3. copies the build without `*_BurstDebugInformation_DoNotShip`,
4. adds `README.txt` with the version and commit filled in,
5. writes `artifacts/release/IronfrontReborn-<version>-windows-x64.zip` and prints its size and
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
pwsh tools/package-release.ps1 -Version v0.1.0 -Publish           # tag on main
pwsh tools/package-release.ps1 -Version v0.1.0 -Publish -NotesFile notes.md
```

`-Publish` runs `gh release create <version> --target main` with the zip attached and prints
the release URL. GitHub caps a release asset at 2 GiB; a build is a few hundred MB.

Tags follow `vMAJOR.MINOR.PATCH`. Bump MINOR when the wire protocol changes: a client from an
older release can no longer talk to the servers, and its players have to download again.

---

## 5. When the servers change

| Change | New release needed? |
|---|---|
| Game servers moved, resized, redeployed | No. The master hands out their address |
| Master redeployed at the same address | No |
| Master moved to a new host name or port | **Yes.** Change `PublicMasterHost` / `PublicMasterPort`, update `Menu.unity` and `play-lan.ps1` (the test names all three), release again |
| Wire protocol version bumped | **Yes.** Old clients are refused |

---

## 6. Known gaps

- The exe is not code-signed, so SmartScreen warns on first launch. The player README says how
  to get past it. Signing needs a certificate the project does not have.
- There is no auto-update. Players download each release by hand.
- Windows only. `build-player.ps1` produces a Windows x64 player and nothing else.
