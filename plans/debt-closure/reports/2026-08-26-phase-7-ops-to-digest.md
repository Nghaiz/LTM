# Phase 7 — the server shipped as far as a digest, 2026-08-26

Phase: [`phases/phase-7-ops-to-digest.md`](../phases/phase-7-ops-to-digest.md) ·
Branch: `phase-7-ops-to-digest` · Source tree: `develop` at `fa275d5`

**The digest:**

```
ghcr.io/nghaiz/ironfront-game-server@sha256:c310da19a065c3dd3a39417d8ede252aa0f9e3c03cabf0cdc3a4a494f9627a40
```

tags `gameserver-v0.3.0` and `2026-08-26T09-22-15VN`, public, pullable with no credentials.
Written into [`docs/handover-ngtukien.md`](../../../docs/handover-ngtukien.md) § 1 and § 4.

The gate passed before anything was pushed, and then passed a second time against the pushed
image rather than only the artifact that went into it. § 3 says why the second run is not
redundant.

---

## 1. The seven steps, in order, with what each returned

| # | Step | Result |
|---|---|---|
| 1 | `tools/build-libs.ps1` | 6 library DLLs + 5 dependencies into `Assets/Plugins`, 0 warnings, 0 errors. Log: [`2026-08-26-phase-7-step1-build-libs.txt`](2026-08-26-phase-7-step1-build-libs.txt) |
| 2 | `EditorBuild.BuildDedicatedServer` (live Editor, MCP) | `[build] dedicated server complete -> D:\Coding\LTM\build\server\Ironfront.Server.x86_64 (126437597 bytes, 6 warning(s))` |
| 3 | `tools/local-server-smoke.sh` — **the gate** | `PASS: 27015/udp bound after ~6s`. Log: [`2026-08-26-phase-7-step3-smoke.txt`](2026-08-26-phase-7-step3-smoke.txt) |
| 4 | `tar -czf build/gameserver-linux.tar.gz -C build/server .` | 49 836 155 B, `sha256:92743cb2f700140528a90fc8e790c9a595c6486fc883866d0e1c9fe50f501d2b`, executable at archive root |
| 5 | `gh release create gameserver-v0.3.0` | <https://github.com/Nghaiz/LTM/releases/tag/gameserver-v0.3.0>. Asset digest as GitHub stored it: `sha256:92743cb2…` — **identical to the local tarball** |
| 6 | `images.yml` | run [`32922452961`](https://github.com/Nghaiz/LTM/actions/runs/32922452961), **success**; both `game-server image` and `master image` green |
| 7 | digest, recorded | read back from `user/packages/container/ironfront-game-server/versions`, not from the run summary |

### Step 1 was load-bearing, and here is the proof rather than the assertion

The phase calls `build-libs.ps1` a trap already paid for once. It was live again on this run.
Before the build, the DLL inside the existing `build/server` tree and the freshly-built plugin
did not match:

```
ba8c2770d1ad24c0f47fcfb40f49473c  build/server/Ironfront.Server_Data/Managed/Ironfront.Net.Replication.dll   (2026-08-21)
ec07280862014493fed3972618985ac1  Ironfront_Reborn/Assets/Plugins/Ironfront.Net.Replication.dll              (2026-08-26, fresh)
```

After step 2 the shipped copy reads `ec072808…` — it matches. Twenty-three commits touched
`Ironfront.Net/` or `Assets/` between `gameserver-v0.2.0` and `fa275d5`; skipping step 1 would
have packaged the 21/08 transport under a 26/08 tag with nothing anywhere saying so.

### Step 6 fired on the release event, not on a manual dispatch

§ 2 lists `gh workflow run images.yml -f gameserver_release_tag=<tag>` as its own step. It was
not needed: `images.yml` triggers on `release: published` and resolves the tag from
`github.event.release.tag_name` (`.github/workflows/images.yml:135-139`), so step 5 already
started step 6. Running the dispatch as well would have produced a second, redundant push of the
same tree. The step is satisfied, by a different trigger than the one written down.

One annotation on the run, harmless: `gameserver-v0.3.0 is not a valid semver` — that is
`docker/metadata-action`'s semver patterns declining to match a non-semver tag, which is the
intended behaviour for artifact tags in this scheme. `gameserver-v0.2.0` produced it too.

---

## 2. The gate, and what it actually showed

```
[smoke] artifact : build/server/Ironfront.Server.x86_64
PASS: 27015/udp bound after ~6s
```

against the real ELF — `ELF 64-bit LSB pie executable, x86-64, stripped`, BuildID
`2bbf639e92710735622ddf4a977f318b4121593f`, run under WSL2 Ubuntu on a real Linux kernel.

**A phrase in AC-2 no longer matches the binary.** The acceptance criterion quotes

```
[net] server up on UDP :27015, 16 connections
```

and the smoke run printed `[net] 16 player slots ready` instead. The line is not gone — the
smoke script greps the last 10 matching lines and the `server up` line had already scrolled past
that window. It is present verbatim in the container run in § 3 below. So AC-2 is met; the
criterion was quoting a log window rather than the log.

**The output-path trap did not fire.** `ResolveOutputDirectory` anchors its fallback to the repo
root off `Application.dataPath` rather than the Editor's cwd
(`Assets/Editor/EditorBuild.cs:329-353`), and the build landed in `<repo>/build/server` as every
consumer expects. Checked, not assumed: `find . -name Ironfront.Server.x86_64 -newermt 2026-08-26`
returns nothing anywhere else in the tree.

---

## 3. The digest holds this build — checked, not inferred

A digest existing proves a push happened. It does not prove the push carried the artifact that
passed the gate, and nothing in the seven steps closes that gap on its own. Two independent
checks close it:

**The bytes.** The image was pulled anonymously by digest, the artifact layer extracted, and the
executable inside compared with the one the gate ran:

```
61c388a1cd661a6230c857fd66de0cec86a1ec497d5f6df1b7f1d585263be19f   app/server/Ironfront.Server.x86_64  (inside the image)
61c388a1cd661a6230c857fd66de0cec86a1ec497d5f6df1b7f1d585263be19f   build/server/Ironfront.Server.x86_64 (what the gate ran)
```

**The behaviour.** The pushed image was then run and asked the only question that matters:

```
docker run -d -p 27015:27015/udp ghcr.io/nghaiz/ironfront-game-server@sha256:c310da19…
```

```
[net] role = Server
[server] batch mode: loading map scene 'Dustbowl' (from IRONFRONT_GAMESERVER_SCENE)
[net] server up on UDP :27015, 16 connections
[net] 16 player slots ready
```

`/proc/net/udp` inside the container reports `00000000:6987` — 27015 bound on all interfaces —
and the host publishes `27015/udp -> 0.0.0.0:27015`. This is the check the whole phase exists
for, run one level further out than the phase asked.

Anonymous pull was verified with `docker logout ghcr.io` first, and again over the raw registry
API with a token minted from no credentials.

---

## 4. Both § 6 warnings, observed

Both appear in the container log above, in production configuration.

**Socket receive buffer, an ops item.**

```
[transport] socket receive buffer clamped to 425984 B (asked for 1048576 B).
            On Linux raise it with `sysctl -w net.core.rmem_max=1048576`. Until then, expect drops under load.
```

425 984 B of the 1 048 576 B requested — the number § 6 asks to be recorded, re-measured here on
the `gameserver-v0.3.0` image and identical to the digit.

**§ 6's premise is wrong for this one as well.** It says both warnings "currently live only in a
plan paragraph". This one does not: `docs/handover-ngtukien.md` **§ 6.4 already carries it** — the
number, the `sysctl` fix, a restart, and a verification step (`grep -c 'clamped'`, expecting 0) —
as a numbered acceptance check rather than a footnote. It is better than what this phase would
have written, because it can be *checked* rather than merely read.

A duplicate § 7.1 was written before this was noticed, and removed. What § 6.4 gained instead is
one sentence: the number was re-measured on 26/08 against the image now pinned in § 4 and has not
moved, so the step is still owed. Two copies of an ops instruction is how one of them goes stale
without anyone noticing — the SSOT is § 6.4.

**Two weapon placeholders — and § 6 has this one wrong.**

```
[transport] [weapons] 2 of 18 weapon configs are class-derived PLACEHOLDERS,
            not registry values: WRENCH(16) SUPER WRENCH(17)
```

§ 6 calls this "a content gap, not a netcode one" and asks for a defect or a reasoned
`won't-do`. Both halves of that are wrong, and the source says so:

- **The content is not missing.** `WeaponCatalog.cs:262-266` carries the real numbers in
  prose — WRENCH 60 damage / 150 balance / 300 force, SUPER_WRENCH 200 / 200 / 2000, both 3 m
  over a 0.15 s swing.
- **It is a netcode gap.** The table models a hitscan shot and a melee swing is not one.
  Writing 60 damage at 3 m range into it would let `ServerFireResolver` resolve a wrench as a
  very short rifle. The entries are held `Inert` and deliberately left UNAUTHORED so
  `DescribeUnauthored` keeps naming them at every startup.

So this is **already a reasoned `won't-do`**, decided at the point the numbers were available
and recorded beside the code that would have consumed them. It closes as a `won't-do` with that
reason, filed as **E-3a** on the ledger. The only thing that was actually missing is that the
decision lived in a code comment where no ledger reader would meet it. The warning firing on
every production start is the intended design, not noise: it is the mechanism that made this
visible at all.

---

## 5. Two observations, neither a blocker

**The server accepts one loopback connection at startup.** Both the WSL smoke and the container
run log a single `conn 1 joined as actor 41 (127.0.0.1:…)` together with client-side lines
(`connected as 1`, `local actor is 41`). It is not new — the same lines appear in the
consolidation run at [`plans/consolidation/plan.md:74`](../../consolidation/plan.md) — and it is
consistent across both runs and both environments. Not investigated here: this phase writes no
netcode source. Worth a look before load-testing, because on a 16-slot server it is one slot.

**`Ravenfield_BurstDebugInformation_DoNotShip` is new in the build output.** 156 KB, absent from
the `gameserver-v0.2.0` tarball (`tar -tzf … | grep -c BurstDebug` → 0), and named by Unity
itself. It was excluded from the archive, which keeps the archive shape identical to v0.2.0's.
Recorded rather than left silent because the exclusion is a deviation from § 2's literal
`tar -czf … -C build/server .`.

---

## 6. Task 7.3 — four dead limits, not three

§ 5 lists three constraints the 2026-08-21 transfer killed. Searching for them turned up a
fourth, and one of the three was already fixed in the place the phase names.

| Recorded as | Actually, 2026-08-26 | Fixed in |
|---|---|---|
| Actions billing-blocked, every job fails in 3–5 s | Public repo, free minutes. Every workflow green today | `plans/00-shared/conventions.md` § On CI |
| No repo admin ⇒ branch protection unreachable | `viewerPermission: ADMIN`; ruleset live | already corrected on 25/08; the **private-repo** claims around it were not |
| GHCR namespace not ours | `ghcr.io/nghaiz/*` is ours; two public packages pushed by the workflow | — |
| **CodeQL skips itself / code scanning needs paid GHAS** | Public repo: CodeQL runs and uploads. 4 green runs; `code-scanning/alerts` answers, 0 alerts | `docs/branch-protection.md` §§ 3, 6 |

`docs/branch-protection.md` § Status was **not** stale in the way § 5 predicted — it was rewritten
on 25/08 and its admin and plan sections are correct. What was stale is everything downstream of
the word *private*: the page still called the repository private in six places while it has been
public since the transfer, and three of those carried live consequences (CodeQL dormant, code
scanning unavailable, secret scanning unavailable). Those are corrected.

The 404-not-403 note § 5 asks to preserve is kept, in the page's own history box.

### Two settings changed as a consequence, both approved and both read back

Documenting these would have left two free security features off for no reason. Approved
explicitly before either was touched.

- **Secret scanning + push protection: ON.** Read back:
  `secret_scanning: enabled`, `secret_scanning_push_protection: enabled`.
- **`analyze (csharp)` added as a required status check.** The page's own precondition — public,
  *and* the job seen to run and pass once — was met four times over. The check name was read
  back from `GET /commits/{sha}/check-runs` character for character, as § 1 of that page
  requires, rather than copied from the page. Verified by asking which rules apply to each ref:
  `main` and `develop` both return
  `["deletion","non_fast_forward","required_status_checks"]` over three contexts, a feature
  branch returns `[]`, and `bypass_actors` is still `[]` with `current_user_can_bypass: never`.

Consequence worth knowing: every PR now waits ~2m45s on CodeQL before it can merge, and with no
bypass actor a CodeQL outage blocks merging. That is the same deliberate trade the page already
made for `build-test`.

**And one step of that page's own post-public checklist is deliberately left open, not quietly
closed.** It listed four things to do on the day the repo went public. Step 1 (see it pass) and
step 4 (require it) are done. Step 2 (turn default setup off) turned out to be a no-op —
`code-scanning/default-setup` reports `state: not-configured`, so the conflict it guarded against
never existed here; checked rather than performed. **Step 3 is not done:** `build-mode` is still
`none` (`codeql.yml:86`), and the page's own measurement — 53% of calls resolved against a
threshold of 85% — means taint analysis misses real findings.

That leaves a required check that is not yet proven sharp, and the report says so rather than
letting the green stand for more than it does: `csharp/diagnostic/database-quality` raised no
annotation on the latest run and the repository shows 0 alerts, but *no annotation* is not *good
quality*, and 0 alerts out of a buildless extraction is also what a blind analysis looks like. The
percentage has not been re-measured since the repository went public. Recorded on
`docs/branch-protection.md` § 3 as the one outstanding step.

---

## 7. Acceptance criteria

| # | Criterion | Where |
|---|---|---|
| 1 | Seven steps, output recorded in order | § 1 |
| 2 | Gate shows a bound UDP port against the real ELF; artifact named | § 2 — `PASS: 27015/udp bound after ~6s`, BuildID `2bbf639e…`. The quoted log line differs; § 2 says why and shows it verbatim in § 3 |
| 3 | A digest exists, written where the handover reads it | Header; `docs/handover-ngtukien.md` §§ 1, 4 |
| 4 | E-3 corrected with the wrong claim visible, closed on the digest | `debt-ledger.md` E-3 |
| 5 | `branch-protection.md` § Status asserts no dead limit; the others searched for and fixed | § 6 |
| 6 | Both warnings recorded — buffer clamp with its number, weapons as defect or reasoned won't-do | § 4. Buffer clamp was **already** recorded, in handover § 6.4, and is re-confirmed there rather than duplicated; weapons close as a reasoned `won't-do`, E-3a |
| 7 | If the gate failed, nothing pushed | Did not arise — the gate passed at step 3, before step 4 |

---

## 8. Handoff

To **#127 / #78**: the digest above, a master digest from the same run, and a handover whose
limits are the current ones. DNS, TLS, `.env` and `deploy.sh up` on the VM remain theirs.

The master pin moved too. `docs/handover-ngtukien.md` had it at `sha256:3421d24b…`, which was a
`develop` build predating three merges. Both pins now come from **one run** (32922452961) off
one commit (`fa275d5`), so the pair cannot drift against each other:

```
IRONFRONT_MASTER_IMAGE=ghcr.io/nghaiz/ironfront-master@sha256:026c885256063c0f3e5901562d53d81d182aa874a397f49b95bdea8ccfe31e81
IRONFRONT_GAMESERVER_IMAGE=ghcr.io/nghaiz/ironfront-game-server@sha256:c310da19a065c3dd3a39417d8ede252aa0f9e3c03cabf0cdc3a4a494f9627a40
```

Both pulled anonymously to confirm before they were written down.
