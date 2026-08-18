using System;
using Ironfront.Net.Protocol;
using Ironfront.Net.Replication.Movement;
using Ironfront.Net.Replication.Vehicles;
using Xunit;

namespace Ironfront.Net.Replication.Tests
{
    /// <summary>
    /// phase-V4 tasks 1 and 2 — the vehicle registry and the once-per-tick capture.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The id pool is deliberately NOT re-tested here.</b> The phase plan's task 1 called for
    /// extracting a shared <c>QuarantinedIdPool</c> and asserting the 5-second boundary again;
    /// V8 task 6 had already shipped <c>VehicleIdPool</c> standalone, and its quarantine,
    /// exhaustion, FIFO rotation and <c>ReleaseAll</c> are covered by
    /// <see cref="VehicleLifecycleWireTests"/>. A second suite over the same class would be the
    /// duplicate <c>development-principles.md</c> forbids, and it would be the copy that goes
    /// stale.
    /// </para>
    /// <para>
    /// Everything below runs against <see cref="FakePose"/>, which is the whole point of
    /// <see cref="IVehiclePoseSource"/>: capture is testable in CI precisely because the only
    /// thing on the far side of that interface is a <c>Rigidbody</c> read.
    /// </para>
    /// </remarks>
    public sealed class VehicleCaptureTests
    {
        [Fact]
        public void RegisteringAndRemovingTracksTheLiveCount()
        {
            var registry = new VehicleRegistry();

            Assert.True(registry.Add(Car(1), new FakePose()));
            Assert.True(registry.Add(Car(2), new FakePose()));
            Assert.Equal(2, registry.LiveCount);

            Assert.True(registry.Remove(1));
            Assert.Equal(1, registry.LiveCount);
            Assert.False(registry.Contains(1));
            Assert.True(registry.Contains(2));
        }

        /// <summary>
        /// Id 0 is the protocol's "no vehicle", and registering under it would put an entry in the
        /// capture buffer that names nothing.
        /// </summary>
        [Fact]
        public void IdZeroAndADuplicateAreBothRefused()
        {
            var registry = new VehicleRegistry();

            Assert.False(registry.Add(Car(0), new FakePose()));
            Assert.True(registry.Add(Car(3), new FakePose()));
            Assert.False(registry.Add(Car(3), new FakePose()));
            Assert.Equal(1, registry.LiveCount);
        }

        /// <summary>
        /// A capture must survive V3's encoder and decoder to within the documented quantisation
        /// error — the whole capture path is worthless if it produces bytes the codec cannot read
        /// back.
        /// </summary>
        [Fact]
        public void ACapturedEntryRoundTripsThroughTheV3Codec()
        {
            var registry = new VehicleRegistry();
            var pose = new FakePose
            {
                Position        = new Vec3(12.5f, 3.25f, -40f),
                LinearVelocity  = new Vec3(20f, 0f, -5f),
                AngularVelocity = new Vec3(1f, -2f, 0.5f),
                RotationW       = 1f,
            };

            registry.Add(Car(7), pose);

            var world = new VehicleWorldSnapshot();
            registry.CaptureInto(world, serverTick: 99);

            var buffer = new byte[VehicleSnapshotMessage.MaxBodySize];
            int written = VehicleDeltaEncoder.WriteFull(buffer, world);
            Assert.True(written > 0);

            var decoder = new VehicleDeltaDecoder();
            Assert.Equal(
                SnapshotReadResult.Applied,
                decoder.Read(new ReadOnlySpan<byte>(buffer, 0, written)));

            Assert.True(decoder.Current.TryFind(7, out VehicleSnapshotEntry entry));

            // Each field against ITS OWN documented quantum, not one shared tolerance. Position
            // is 6.25 cm; velocity is 64/127 = 0.5 m/s and the packers TRUNCATE rather than
            // round, so the error is one-sided and up to a full step; angular is 8/127 = 0.063
            // rad/s on the same truncating scale. A single tight tolerance across all three
            // would fail on velocity while saying nothing about position.
            Assert.InRange(Quantize.UnpackPos(entry.PosX), 12.5f - 0.0625f, 12.5f + 0.0625f);
            Assert.InRange(Quantize.UnpackPos(entry.PosY), 3.25f - 0.0625f, 3.25f + 0.0625f);
            Assert.InRange(Quantize.UnpackPos(entry.PosZ), -40f - 0.0625f, -40f + 0.0625f);

            const float VelStep = Quantize.VEL_MAX / 127f;
            Assert.InRange(Quantize.UnpackVel16(entry.VelX), 20f - VelStep, 20f + VelStep);
            Assert.InRange(Quantize.UnpackVel16(entry.VelZ), -5f - VelStep, -5f + VelStep);

            const float AngStep = Quantize.ANGVEL_MAX / 127f;
            Assert.InRange(Quantize.UnpackAngVel(entry.AngVelX), 1f - AngStep, 1f + AngStep);
            Assert.InRange(Quantize.UnpackAngVel(entry.AngVelY), -2f - AngStep, -2f + AngStep);
        }

        /// <summary>
        /// Angular velocity has its OWN scale, and sharing the linear one would be invisible in
        /// every test that only checks a round trip.
        /// </summary>
        /// <remarks>
        /// At <c>VEL_SCALE</c> the i8 slot saturates at 64 rad/s — ten revolutions a second — so
        /// every rotation a vehicle actually performs would land in the bottom two or three codes
        /// and quantise to nothing. This pins that 2 rad/s is a distinguishable value rather than
        /// the same code as 3.
        /// </remarks>
        [Fact]
        public void AngularVelocityHasEnoughResolutionToDistinguishRealRotations()
        {
            sbyte two   = Quantize.PackAngVel(2f);
            sbyte three = Quantize.PackAngVel(3f);

            Assert.NotEqual(two, three);
            Assert.Equal(2f, Quantize.UnpackAngVel(two), 1);
        }

        /// <summary>
        /// Saturating, not wrapping. A wrapped cast turns a violent spin into a slow
        /// counter-rotation on every client — a codec bug that looks exactly like a physics bug.
        /// </summary>
        /// <remarks>
        /// The negative extreme is <c>-127</c> and not <c>sbyte.MinValue</c>, because the scale is
        /// <c>127 / ANGVEL_MAX</c> and is therefore symmetric about zero — the same arrangement
        /// <see cref="Quantize.PackVel"/> uses. <c>-128</c> is deliberately never produced: a
        /// range that reaches one code further in one direction than the other makes a clockwise
        /// and an anticlockwise spin of equal speed decode to different numbers.
        /// </remarks>
        [Theory]
        [InlineData(50f, (sbyte)127)]
        [InlineData(-50f, (sbyte)(-127))]
        public void AnAngularVelocityPastTheCeilingSaturatesRatherThanWrapping(
            float value, sbyte expected)
        {
            sbyte packed = Quantize.PackAngVel(value);

            Assert.Equal(Math.Sign(value), Math.Sign((int)packed));
            Assert.Equal(expected, packed);
            Assert.Equal(Quantize.ANGVEL_MAX * Math.Sign(value), Quantize.UnpackAngVel(packed), 3);
        }

        /// <summary>Health is normalized against the vehicle's OWN maxHealth, not a constant.</summary>
        [Fact]
        public void HealthIsNormalizedAgainstTheVehiclesOwnMaximum()
        {
            VehicleState tank = VehicleState.Spawned(
                1, 0, VehicleKind.Tank, seatCount: 2, maxHealth: 1000f, ownerTeam: 0);

            Assert.Equal(Quantize.HEALTH_MAX, tank.NormalizedHealth);

            tank.Health = 500f;
            Assert.Equal(50, tank.NormalizedHealth);

            tank.Health = 0f;
            Assert.Equal(0, tank.NormalizedHealth);
        }

        /// <summary>
        /// conventions section 3.2 — the capture path is on the 20 Hz snapshot stage and must not
        /// hand the GC anything. Measured, not asserted by inspection.
        /// </summary>
        [Fact]
        public void CaptureOfEveryVehicleAllocatesNothing()
        {
            var registry = new VehicleRegistry();
            for (ushort id = 1; id <= ProtocolConstants.MAX_VEHICLES; id++)
                registry.Add(Car(id), new FakePose { RotationW = 1f });

            var world = new VehicleWorldSnapshot();

            // Warm every path first: the JIT, the array bounds checks and the interface dispatch
            // all allocate on their first pass, and measuring that would measure the runtime.
            for (int i = 0; i < 8; i++) registry.CaptureInto(world, (uint)i);

            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 100; i++) registry.CaptureInto(world, (uint)(100 + i));
            long after = GC.GetAllocatedBytesForCurrentThread();

            Assert.Equal(ProtocolConstants.MAX_VEHICLES, world.VehicleCount);
            Assert.Equal(0, after - before);
        }

        /// <summary>Flags come from the state and the pose source, not from a stored copy.</summary>
        [Fact]
        public void TheFlagsByteReflectsBurningAndTheWaterAndAirborneReads()
        {
            var registry = new VehicleRegistry();
            var pose = new FakePose { RotationW = 1f, InWater = true, Airborne = true };
            registry.Add(Car(1), pose);

            registry.TryGetState(1, out VehicleState state);
            state.Burning = true;
            registry.TrySetState(1, in state);

            var world = new VehicleWorldSnapshot();
            registry.CaptureInto(world, 1);

            VehicleStateFlags flags = world.Vehicles[0].Flags;
            Assert.True((flags & VehicleStateFlags.Burning) != 0);
            Assert.True((flags & VehicleStateFlags.InWater) != 0);
            Assert.True((flags & VehicleStateFlags.Airborne) != 0);
            Assert.True((flags & VehicleStateFlags.Dead) == 0);
        }

        /// <summary>
        /// The helicopter tail is a normalized u16 split low-byte-first. Getting the byte order
        /// backwards produces a rotor that reads as spinning at some other plausible speed, which
        /// is why the encoding is one tested function rather than two shifts at a call site.
        /// </summary>
        [Fact]
        public void TheHelicopterSubtypeTailRoundTripsItsRotorSpeed()
        {
            VehicleSubtypeTail.PackHelicopter(0.75f, out byte a, out byte b);

            Assert.Equal(0.75f, VehicleSubtypeTail.UnpackHelicopter(a, b), 3);
            Assert.NotEqual(a, b);   // a genuine 16-bit split, not one byte written twice
        }

        /// <summary>A steer angle keeps its sign through the i8 tail byte.</summary>
        [Theory]
        [InlineData(35f)]
        [InlineData(-35f)]
        public void TheSteeredSubtypeTailKeepsTheSignOfTheAngle(float degrees)
        {
            VehicleSubtypeTail.PackSteered(degrees, 1f, out byte a, out byte b);

            Assert.Equal(degrees, VehicleSubtypeTail.UnpackSteerAngle(a), 0);
            Assert.Equal(1f, VehicleSubtypeTail.UnpackUnitByte(b), 2);
        }

        private static VehicleState Car(ushort id)
            => VehicleState.Spawned(id, 0, VehicleKind.Car, seatCount: 2, maxHealth: 100f, ownerTeam: 0);

        /// <summary>
        /// The fake that makes every test above possible. Everything the real implementation adds
        /// is a <c>Rigidbody</c> read.
        /// </summary>
        internal sealed class FakePose : IVehiclePoseSource
        {
            public Vec3 Position;
            public Vec3 LinearVelocity;
            public Vec3 AngularVelocity;
            public bool InWater;
            public bool Airborne;

            // Properties rather than fields, so the ones no test sets are not build errors under
            // warnings-as-errors (CS0649). The identity quaternion is the default because a
            // capture of a zeroed quaternion is not a rotation at all.
            public float RotationX { get; set; }
            public float RotationY { get; set; }
            public float RotationZ { get; set; }
            public float RotationW { get; set; } = 1f;
            public byte SubtypeA { get; set; }
            public byte SubtypeB { get; set; }

            public float TurretYaw { get; set; }
            public float TurretPitch { get; set; }
            public bool IsInWater => InWater;
            public bool IsAirborne => Airborne;

            public void ReadPose(
                out Vec3 position,
                out float rotationX, out float rotationY, out float rotationZ, out float rotationW,
                out Vec3 linearVelocity,
                out Vec3 angularVelocity)
            {
                position        = Position;
                rotationX       = RotationX;
                rotationY       = RotationY;
                rotationZ       = RotationZ;
                rotationW       = RotationW;
                linearVelocity  = LinearVelocity;
                angularVelocity = AngularVelocity;
            }

            public void ReadSubtypeTail(out byte subtypeA, out byte subtypeB)
            {
                subtypeA = SubtypeA;
                subtypeB = SubtypeB;
            }
        }
    }
}
