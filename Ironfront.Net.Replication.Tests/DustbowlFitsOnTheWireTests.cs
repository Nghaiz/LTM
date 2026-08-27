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
    /// X-39 — whether the map Dustbowl authors is a map the wire can carry.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This check already existed and already fired, and that is the point.</b>
    /// <c>LevelBounds.SetupBounds</c> calls <see cref="PlayVolume.FitsOnTheWire"/> and logs an
    /// error when it is false — and it has been printing that error into every lane-B server
    /// log since E-6 landed, while the comment two lines above it says "Today Dustbowl's is, by
    /// a wide margin". Nobody read either. A <c>Debug.LogError</c> in a 300 KB log is not a
    /// gate; this is.
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
        public void DustbowlsPlayVolumeReachesPastTheWiresRange_KnownGap()
        {
            PlayVolume volume = AuthoredVolume("Dustbowl", "Level Bounds");

            // A pinned baseline of a BROKEN state, and it inverts rather than moving. If this
            // goes red it means the volume changed:
            //   - now inside the range  -> the gap is CLOSED. Delete this test, and assert
            //                              volume.FitsOnTheWire in its place. Do not re-pin.
            //   - still outside, new numbers -> the map moved without fixing the range. Read
            //                              the new corners below before deciding anything.
            // Whichever it is, the answer is never "update the constant to whatever the run
            // just reported".
            Assert.False(
                volume.FitsOnTheWire,
                "Dustbowl's authored play volume now fits the wire's +/-2048 m position range. "
                + "That is the gap closing, not a regression: replace this test with a positive "
                + "assertion on FitsOnTheWire and delete the known-gap note in the X-39 ledger "
                + "row. Never re-pin this to a new set of out-of-range corners.");

            // By identity, not by a boolean. FitsOnTheWire is satisfied-or-not by any number of
            // different overruns, and knowing WHICH corner is out is the whole difference
            // between "the map is 300 m too wide" and "somebody moved the whole level".
            Assert.Equal(650f, volume.Min.X, 1);
            Assert.Equal(2350f, volume.Max.X, 1);
            Assert.Equal(620f, volume.Min.Z, 1);
            Assert.Equal(2220f, volume.Max.Z, 1);
            Assert.Equal(-50f, volume.Min.Y, 1);
            Assert.Equal(650f, volume.Max.Y, 1);

            // 302 m of x and 172 m of z, out of a 1700 x 1600 m box. Stated so a reader does
            // not have to subtract, and so a change to POS_MAX moves this line too.
            Assert.Equal(302f, volume.Max.X - Quantize.POS_MAX, 1);
            Assert.Equal(172f, volume.Max.Z - Quantize.POS_MAX, 1);
        }

        [Fact]
        public void TheOasisCapturePointIsInsideTheUnrepresentableRegion_KnownGap()
        {
            // What raises X-39 from "scenery parked off-map" to a defect: one of Dustbowl's
            // seven capture points, its flag, and five vehicle spawners sit past POS_MAX. Every
            // player and vehicle contesting Oasis replicates at exactly 2048.00 m to every
            // other client, so two of them 50 m apart are drawn in the same place.
            //
            // Inverts, never re-pins: if Oasis comes inside the range this goes red announcing
            // the FIX. Move the assertion to "no capture point is outside" and delete this.
            Vec3 oasis = AuthoredPosition("Dustbowl", "Oasis Capture Point");

            Assert.True(
                Quantize.PositionSaturates(oasis.X),
                "the Oasis capture point is now inside the wire's range. That is the gap "
                + "closing: assert that NO capture point saturates, and delete this test.");
            Assert.Equal(2085.6f, oasis.X, 1);
            Assert.Equal(37.6f, oasis.X - Quantize.POS_MAX, 1);
        }

        // ------------------------------------------------------------------ the saturation log

        [Fact]
        public void AnEntityPastTheRangeIsReportedRatherThanSilentlyClamped()
        {
            PositionSaturationLog.Reset();

            SnapshotBuilder.Capture(
                actorId: 41,
                position: new Vec3(2085.6f, 8.9f, 1139.4f),   // standing on the Oasis flag
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
