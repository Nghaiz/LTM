using System;

namespace Ironfront.Net.Replication.Movement
{
    /// <summary>
    /// A three-component vector. Exists because this assembly must not reference
    /// UnityEngine (architecture.md section 5.1) but the movement simulation it hosts is
    /// vector maths from end to end.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Field layout and semantics match <c>UnityEngine.Vector3</c> exactly (left-handed, Y
    /// up, Z forward) so the Unity-side adapter is a field-for-field copy with no conversion
    /// and no chance of an axis swap creeping in.
    /// </para>
    /// <para>
    /// Deliberately a readonly struct with no operator overloads beyond the four that
    /// movement actually uses. It is not a general-purpose maths library and should not grow
    /// into one — <c>UnityEngine.Vector3</c> already exists for that on the client side.
    /// </para>
    /// </remarks>
    public readonly struct Vec3 : IEquatable<Vec3>
    {
        public readonly float X;
        public readonly float Y;
        public readonly float Z;

        public Vec3(float x, float y, float z)
        {
            X = x;
            Y = y;
            Z = z;
        }

        public static Vec3 Zero => default;

        public static Vec3 operator +(Vec3 a, Vec3 b) => new Vec3(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        public static Vec3 operator -(Vec3 a, Vec3 b) => new Vec3(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        public static Vec3 operator *(Vec3 a, float s) => new Vec3(a.X * s, a.Y * s, a.Z * s);
        public static Vec3 operator *(float s, Vec3 a) => a * s;

        public float SqrMagnitude => X * X + Y * Y + Z * Z;

        public float Magnitude => (float)Math.Sqrt(SqrMagnitude);

        /// <summary>
        /// The unit vector, or <see cref="Zero"/> for a zero-length input.
        /// </summary>
        /// <remarks>
        /// The zero case matters: <c>UnityEngine.Vector3.normalized</c> returns zero rather
        /// than NaN for a zero vector, and the movement port depends on that — no input means
        /// no movement, not a NaN position that propagates into every snapshot afterwards.
        /// The epsilon matches Unity's (1e-5 on the magnitude).
        /// </remarks>
        public Vec3 Normalized
        {
            get
            {
                float magnitude = Magnitude;
                if (magnitude < 1e-5f) return Zero;
                float inverse = 1f / magnitude;
                return new Vec3(X * inverse, Y * inverse, Z * inverse);
            }
        }

        /// <summary>Distance between two points.</summary>
        public static float Distance(in Vec3 a, in Vec3 b) => (a - b).Magnitude;

        public bool Equals(Vec3 other) => X.Equals(other.X) && Y.Equals(other.Y) && Z.Equals(other.Z);

        public override bool Equals(object? obj) => obj is Vec3 other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = X.GetHashCode();
                hash = (hash * 397) ^ Y.GetHashCode();
                hash = (hash * 397) ^ Z.GetHashCode();
                return hash;
            }
        }

        public override string ToString()
            => $"({X:F3}, {Y:F3}, {Z:F3})";
    }
}
