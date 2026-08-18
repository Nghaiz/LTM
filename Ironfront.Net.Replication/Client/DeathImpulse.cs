using Ironfront.Net.Protocol;
using Ironfront.Net.Replication.Movement;

namespace Ironfront.Net.Replication.Client
{
    /// <summary>
    /// A death, as the ragdoll needs it: who died, who killed them, and the impulse to throw
    /// the corpse with. phase-V10 task 4.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why this exists beside <see cref="KillfeedEntry"/> rather than inside it.</b>
    /// <c>KillfeedEntry.From</c> drops <c>ForceX/Y/Z</c> — correctly, because a text line has no
    /// use for a vector. But the corpse does, so one <c>S_DEATH</c> feeds two consumers: the
    /// feed takes the line and this takes the impulse (V10 D19). Neither type changes.
    /// </para>
    /// <para>
    /// <b>The force unpacks through <see cref="Quantize.UnpackVel16"/>, not
    /// <see cref="Quantize.UnpackVel"/>.</b> The <c>i8</c> form is the <i>snapshot's</i>
    /// velocity slot and saturates at 64 m/s; running a death impulse through it would clamp
    /// every kill and make a rocket read exactly like a pistol. <c>PackVel16</c>'s own doc names
    /// this message as one of the two that use the wide form.
    /// </para>
    /// </remarks>
    public readonly struct DeathImpulse
    {
        public readonly ushort VictimActorId;
        public readonly ushort KillerActorId;
        public readonly CauseOfDeath Cause;

        /// <summary>Impulse in m/s, already unpacked from the wire's i16 triple.</summary>
        public readonly Vec3 Force;

        /// <summary>The killer was the world — fall damage, drowning, a driverless vehicle.</summary>
        public readonly bool KilledByEnvironment;

        public DeathImpulse(
            ushort victimActorId, ushort killerActorId, CauseOfDeath cause,
            Vec3 force, bool killedByEnvironment)
        {
            VictimActorId       = victimActorId;
            KillerActorId       = killerActorId;
            Cause               = cause;
            Force               = force;
            KilledByEnvironment = killedByEnvironment;
        }

        /// <summary>Builds one from the wire message.</summary>
        public static DeathImpulse From(in DeathMessage message)
            => new DeathImpulse(
                message.VictimActorId,
                message.KillerActorId,
                message.Cause,
                new Vec3(
                    Quantize.UnpackVel16(message.ForceX),
                    Quantize.UnpackVel16(message.ForceY),
                    Quantize.UnpackVel16(message.ForceZ)),
                message.KilledByEnvironment);
    }
}
