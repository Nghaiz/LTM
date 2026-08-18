using System;

namespace Ironfront.Net.Replication.Movement
{
    /// <summary>
    /// A unit quaternion. The rotational sibling of <see cref="Vec3"/>, and it exists for the
    /// same reason: this assembly must not reference UnityEngine, and vehicle replication is
    /// rotation maths from end to end.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Field order and handedness match <c>UnityEngine.Quaternion</c> exactly, so the Unity-side
    /// adapter is a field-for-field copy. Getting <c>W</c> into the wrong slot produces a
    /// rotation that is plausible, stable, and wrong by a fixed offset — the kind of defect that
    /// survives a play session because nothing about it looks like a bug.
    /// </para>
    /// <para>
    /// <b>Actors do not need this and vehicles do.</b> An actor replicates a single yaw
    /// (<c>SnapshotInterpolator.TryLerpYaw</c>) because an infantryman does not roll. A vehicle
    /// rolls, pitches and yaws at once, so its rotation is a full quaternion and its
    /// interpolation is a slerp rather than an angle lerp.
    /// </para>
    /// <para>
    /// Deliberately minimal: the components, identity, and equality. Everything that operates on
    /// a quaternion lives in <c>QuatMath</c>, so there is one place to look for the sign
    /// conventions rather than some on the type and some beside it.
    /// </para>
    /// </remarks>
    public readonly struct Quat : IEquatable<Quat>
    {
        public readonly float X;
        public readonly float Y;
        public readonly float Z;
        public readonly float W;

        public Quat(float x, float y, float z, float w)
        {
            X = x;
            Y = y;
            Z = z;
            W = w;
        }

        /// <summary>No rotation.</summary>
        /// <remarks>
        /// Not <c>default</c>: the all-zero quaternion is not a rotation at all, and a struct
        /// that defaults to it would make an unset field silently degenerate rather than
        /// harmlessly neutral.
        /// </remarks>
        public static Quat Identity => new Quat(0f, 0f, 0f, 1f);

        public bool Equals(Quat other)
            => X.Equals(other.X) && Y.Equals(other.Y) && Z.Equals(other.Z) && W.Equals(other.W);

        public override bool Equals(object? obj) => obj is Quat other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = X.GetHashCode();
                hash = (hash * 397) ^ Y.GetHashCode();
                hash = (hash * 397) ^ Z.GetHashCode();
                hash = (hash * 397) ^ W.GetHashCode();
                return hash;
            }
        }

        public override string ToString() => $"({X:F4}, {Y:F4}, {Z:F4}, {W:F4})";
    }
}
