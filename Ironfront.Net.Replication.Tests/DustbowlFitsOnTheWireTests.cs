using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using Ironfront.Net.Protocol;
using Ironfront.Net.Replication.Movement;
using Ironfront.Net.Replication.World;
using Xunit;

namespace Ironfront.Net.Replication.Tests
{
    /// <summary>
    /// X-39 / X-53 — whether the map Dustbowl authors is a map the wire can carry. It is, since
    /// 4.0.0 moved the position window from +/-2048 to -1024..3072.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This check already existed and already fired, and that is the point.</b>
    /// <c>LevelBounds.SetupBounds</c> calls <see cref="PlayVolume.FitsOnTheWire"/> and logs an
    /// error when it is false — and it printed that error into every lane-B server log from E-6
    /// until X-53, while the comment two lines above it said "Today Dustbowl's is, by a wide
    /// margin". Nobody read either. A <c>Debug.LogError</c> in a 300 KB log is not a gate; this
    /// is. What finally forced it was a player falling through the world at the Oasis spawn.
    /// </para>
    /// <para>
    /// <b>It reads the scene file off disk rather than the Editor.</b> Assembly-CSharp cannot
    /// be referenced by any test assembly (E-11b), so the authored numbers can only be reached
    /// as text. The parse is deliberately strict: an ancestor with a rotation or a non-unit
    /// scale makes the world position something this walk cannot compute, and the test says so
    /// and fails rather than quietly returning a number that looks fine.
    /// </para>
    /// </remarks>
    public sealed class DustbowlFitsOnTheWireTests
    {
        [Fact]
        public void DustbowlsPlayVolumeFitsOnTheWire()
        {
            PlayVolume volume = AuthoredVolume("Dustbowl", "Level Bounds");

            // INVERTED, not re-pinned (X-53). This was
            // DustbowlsPlayVolumeReachesPastTheWiresRange_KnownGap, asserting the gap was still
            // open and carrying its own instruction for this moment: "now inside the range ->
            // the gap is CLOSED. Delete this test, and assert volume.FitsOnTheWire in its place.
            // Do not re-pin." The gap closed by MOVING the wire's window rather than the map --
            // Quantize.POS_MIN/POS_MAX went from +/-2048 to -1024..3072, same 4096 m width, same
            // 6.25 cm -- so from here a red means a genuine regression: either the map grew or
            // somebody narrowed the window.
            Assert.True(
                volume.FitsOnTheWire,
                $"Dustbowl's authored play volume ({volume.Min} .. {volume.Max}) no longer fits "
                + $"the wire's {Quantize.POS_MIN} .. {Quantize.POS_MAX} m position range. "
                + "Bodies outside it are clamped silently by the snapshot encoder and desync "
                + "permanently. Do NOT widen the range to absorb this without reading X-53 "
                + "first: widening halves the resolution for every actor on every map.");

            // By identity, not by the boolean. FitsOnTheWire is satisfied by any volume inside
            // the window, so knowing WHICH corner moved is the difference between "the map grew"
            // and "somebody moved the whole level".
            Assert.Equal(650f, volume.Min.X, 1);
            Assert.Equal(2350f, volume.Max.X, 1);
            Assert.Equal(620f, volume.Min.Z, 1);
            Assert.Equal(2220f, volume.Max.Z, 1);
            Assert.Equal(-50f, volume.Min.Y, 1);
            Assert.Equal(650f, volume.Max.Y, 1);

            // The headroom that is left, stated so a later widening of the MAP is a red here
            // rather than a silent desync in a match. 722 m on x, 852 m on z.
            Assert.Equal(722f, Quantize.POS_MAX - volume.Max.X, 1);
            Assert.Equal(852f, Quantize.POS_MAX - volume.Max.Z, 1);
        }

        [Fact]
        public void NoCapturePointIsOutsideTheWiresRange()
        {
            // INVERTED per this fixture's own instruction (X-53). It used to assert that Oasis
            // SATURATED -- "if Oasis comes inside the range this goes red announcing the FIX.
            // Move the assertion to 'no capture point is outside' and delete this." Done.
            //
            // Why it mattered: Oasis is team 0's OPENING base, so under X-53's other half every
            // team-0 player spawned there. At x = 2085.6 against the old POS_MAX of 2048 that
            // replicated as exactly 2048.00 to every client -- 37.6 m from where the server had
            // the body, over terrain that is not there. Measured on a real 3-client run: both
            // team-0 clients were placed at x = 2084-2086 and fell through the world.
            SceneTransforms transforms = Parse("Dustbowl");

            // Named, because a point this walk cannot place is NOT a point known to be inside.
            // 'Outpost Capture Point' hangs off a rotated ancestor, and the parser refuses to
            // sum local positions through a rotation rather than return a plausible wrong
            // number. Asserted as a set so a NEW unresolvable point is a red here instead of
            // silently dropping out of the sweep -- the exemption cannot become a graveyard.
            var unresolvable = new List<string>();
            var checked_ = new List<string>();

            foreach (string point in new[]
            {
                "Fortress Capture Point", "Bridge Capture Point", "Town Capture Point",
                "Oasis Capture Point", "Outpost Capture Point", "Mine Capture Point",
            })
            {
                Vec3 p;
                try { p = transforms.Resolve(point).World; }
                catch (Exception) { unresolvable.Add(point); continue; }

                checked_.Add(point);

                Assert.False(
                    Quantize.PositionSaturates(p.X),
                    $"{point} is at x = {p.X}, outside the wire's "
                    + $"{Quantize.POS_MIN} .. {Quantize.POS_MAX} m range, so every body "
                    + "contesting it replicates at the boundary and desyncs permanently.");
                Assert.False(
                    Quantize.PositionSaturates(p.Z),
                    $"{point} is at z = {p.Z}, outside the wire's range.");
            }

            Assert.Equal(new[] { "Outpost Capture Point" }, unresolvable);
            Assert.Equal(5, checked_.Count);

            // Oasis by identity, because it is the one that was out and the one whose position
            // a future map edit is most likely to move.
            Assert.Equal(2085.6f, transforms.Resolve("Oasis Capture Point").World.X, 1);
        }

        // ------------------------------------------------------------------ the saturation log

        [Fact]
        public void AnEntityPastTheRangeIsReportedRatherThanSilentlyClamped()
        {
            PositionSaturationLog.Reset();

            SnapshotBuilder.Capture(
                actorId: 41,
                position: new Vec3(Quantize.POS_MAX + 40f, 8.9f, 1139.4f),  // past the ceiling
                yawDegrees: 0f, pitchDegrees: 0f,
                velocity: default,
                stateFlags: default,
                health: 100f, weaponId: 0, ammoInClip: 0, team: 0);

            Assert.Equal(1, PositionSaturationLog.Count);
            Assert.Equal(1, PositionSaturationLog.DistinctEntities);
            Assert.Contains("actor 41", PositionSaturationLog.First);

            PositionSaturationLog.Reset();
        }

        [Fact]
        public void AnEntityInsideTheRangeIsNotReported()
        {
            PositionSaturationLog.Reset();

            SnapshotBuilder.Capture(
                actorId: 41,
                position: new Vec3(1610f, 43f, 1453f),        // an ordinary Dustbowl position
                yawDegrees: 0f, pitchDegrees: 0f,
                velocity: default,
                stateFlags: default,
                health: 100f, weaponId: 0, ammoInClip: 0, team: 0);

            Assert.Equal(0, PositionSaturationLog.Count);
            Assert.Null(PositionSaturationLog.First);
        }

        [Fact]
        public void ExactlyOnTheBoundaryRoundTripsAndIsNotReported()
        {
            // PlayVolume.FitsOnTheWire is inclusive at the boundary because PackPos maps
            // exactly POS_MAX to the top code. The two have to agree, or a volume that passes
            // the fit check would still report saturation on every tick at its own edge.
            Assert.False(Quantize.PositionSaturates(Quantize.POS_MAX));
            Assert.False(Quantize.PositionSaturates(Quantize.POS_MIN));
            Assert.True(Quantize.PositionSaturates(Quantize.POS_MAX + 0.1f));
            Assert.True(Quantize.PositionSaturates(Quantize.POS_MIN - 0.1f));
            Assert.True(Quantize.PositionSaturates(float.NaN));
        }

        // ------------------------------------------------------------------ the scene parse

        private static PlayVolume AuthoredVolume(string scene, string objectName)
        {
            SceneTransforms transforms = Parse(scene);
            (Vec3 world, Vec3 localScale) = transforms.Resolve(objectName);
            return new PlayVolume(world, localScale);
        }

        private static Vec3 AuthoredPosition(string scene, string objectName)
            => Parse(scene).Resolve(objectName).World;

        private static SceneTransforms Parse(string scene)
        {
            string path = Path.Combine(
                RepoRoot(), "Ironfront_Reborn", "Assets", "Scenes", scene + ".unity");
            Assert.True(File.Exists(path), $"no scene at {path}");
            return new SceneTransforms(File.ReadAllText(path));
        }

        /// <summary>
        /// The subset of Unity's scene YAML this check needs: every Transform's local position,
        /// local scale and parent, plus the GameObject names to look them up by.
        /// </summary>
        private sealed class SceneTransforms
        {
            private readonly Dictionary<long, (Vec3 Pos, Vec3 Scale, long Father, bool Identity)> _transforms
                = new Dictionary<long, (Vec3, Vec3, long, bool)>();
            private readonly Dictionary<string, long> _transformByName = new Dictionary<string, long>();

            public SceneTransforms(string yaml)
            {
                var nameByGameObject = new Dictionary<long, string>();
                foreach (System.Text.RegularExpressions.Match block in Regex.Matches(
                    yaml, @"--- !u!1 &(\d+)\r?\nGameObject:\r?\n(.*?)(?=\r?\n--- !u!|\z)",
                    RegexOptions.Singleline))
                {
                    System.Text.RegularExpressions.Match name = Regex.Match(block.Groups[2].Value, @"m_Name: (.+)");
                    if (name.Success)
                        nameByGameObject[long.Parse(block.Groups[1].Value, CultureInfo.InvariantCulture)]
                            = name.Groups[1].Value.Trim();
                }

                foreach (System.Text.RegularExpressions.Match block in Regex.Matches(
                    yaml, @"--- !u!4 &(\d+)\r?\nTransform:\r?\n(.*?)(?=\r?\n--- !u!|\z)",
                    RegexOptions.Singleline))
                {
                    long id = long.Parse(block.Groups[1].Value, CultureInfo.InvariantCulture);
                    string body = block.Groups[2].Value;

                    Vec3 pos = ReadVec(body, "m_LocalPosition");
                    Vec3 scale = ReadVec(body, "m_LocalScale");
                    long father = ReadId(body, "m_Father");
                    long owner = ReadId(body, "m_GameObject");

                    System.Text.RegularExpressions.Match rotation = Regex.Match(
                        body,
                        @"m_LocalRotation: \{x: (-?[\d.eE+-]+), y: (-?[\d.eE+-]+), "
                        + @"z: (-?[\d.eE+-]+), w: (-?[\d.eE+-]+)\}");
                    bool identity = rotation.Success
                        && Math.Abs(float.Parse(rotation.Groups[1].Value, CultureInfo.InvariantCulture)) < 1e-4f
                        && Math.Abs(float.Parse(rotation.Groups[2].Value, CultureInfo.InvariantCulture)) < 1e-4f
                        && Math.Abs(float.Parse(rotation.Groups[3].Value, CultureInfo.InvariantCulture)) < 1e-4f;

                    _transforms[id] = (pos, scale, father, identity);

                    if (nameByGameObject.TryGetValue(owner, out string? objectName)
                        && !_transformByName.ContainsKey(objectName))
                    {
                        _transformByName[objectName] = id;
                    }
                }
            }

            public (Vec3 World, Vec3 LocalScale) Resolve(string objectName)
            {
                Assert.True(
                    _transformByName.TryGetValue(objectName, out long id),
                    $"no GameObject named '{objectName}' in the scene");

                Vec3 localScale = _transforms[id].Scale;
                float x = 0f, y = 0f, z = 0f;

                for (long cursor = id; cursor != 0; cursor = _transforms[cursor].Father)
                {
                    Assert.True(
                        _transforms.ContainsKey(cursor),
                        $"'{objectName}' has an ancestor transform {cursor} the scene does not "
                        + "declare — this walk cannot compute a world position, and guessing "
                        + "one would be worse than failing");

                    (Vec3 pos, Vec3 scale, _, bool identity) = _transforms[cursor];

                    // A rotated or scaled ancestor makes this sum wrong, and wrong in a way
                    // that still produces a plausible number. LevelBounds itself takes the
                    // WORLD position and the LOCAL scale, so a scaled ancestor would also make
                    // the shipped volume disagree with the authored box.
                    Assert.True(
                        identity,
                        $"'{objectName}' has a rotated ancestor; this check computes world "
                        + "positions by summing local ones and cannot handle that");
                    if (cursor != id)
                    {
                        Assert.True(
                            Math.Abs(scale.X - 1f) < 1e-4f
                            && Math.Abs(scale.Y - 1f) < 1e-4f
                            && Math.Abs(scale.Z - 1f) < 1e-4f,
                            $"'{objectName}' has a scaled ancestor; LevelBounds builds its "
                            + "volume from the WORLD position and the LOCAL scale, so the "
                            + "authored box and the shipped one would already disagree");
                    }

                    x += pos.X;
                    y += pos.Y;
                    z += pos.Z;
                }

                return (new Vec3(x, y, z), localScale);
            }

            private static Vec3 ReadVec(string body, string key)
            {
                System.Text.RegularExpressions.Match m = Regex.Match(
                    body,
                    key + @": \{x: (-?[\d.eE+-]+), y: (-?[\d.eE+-]+), z: (-?[\d.eE+-]+)\}");
                Assert.True(m.Success, $"no {key} in transform block");
                return new Vec3(
                    float.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture),
                    float.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture),
                    float.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture));
            }

            private static long ReadId(string body, string key)
            {
                System.Text.RegularExpressions.Match m = Regex.Match(body, key + @": \{fileID: (\d+)\}");
                return m.Success ? long.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : 0L;
            }
        }

        private static string RepoRoot()
        {
            DirectoryInfo? directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory != null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "Ironfront.sln")))
                    return directory.FullName;

                directory = directory.Parent;
            }

            throw new InvalidOperationException(
                $"No Ironfront.sln found walking up from {AppContext.BaseDirectory}.");
        }
    }
}
