using System;

namespace Ironfront.Net.Replication.Combat
{
    /// <summary>
    /// What a destroyed vehicle's explosion and a burning vehicle's fire do to a soldier at a
    /// given distance: blast by peak overpressure, fire by radiant heat. Pure physics; the scene
    /// supplies the distances, the vehicle's explosive charge and its fire's power.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Owner ruling 2026-10-06</b>: "explosions and fires must hurt what is around them, more
    /// the closer it stands -- work out the real numbers and apply them". It reverses the
    /// 2026-09-27 ruling that a wreck's blast damages nothing, which the owner had made after a
    /// flat 300 damage over 6 m set parked vehicles off in a chain.
    /// </para>
    /// <para>
    /// <b>Blast.</b> Peak free-air overpressure from the Mills (1987) fit to TNT data,
    /// ΔP = 1772/Z³ − 114/Z² + 108/Z kPa, at the Hopkinson-Cranz scaled distance Z = R / W^(1/3)
    /// (R in metres, W in kg of TNT-equivalent), so a charge eight times bigger reaches twice as
    /// far. Injury tracks the logarithm of the overpressure (the blast-injury probit models are
    /// linear in ln ΔP), so damage runs from nothing at <see cref="HarmlessKpa"/> (about 1 psi:
    /// windows break, a soldier is shaken) to a full health bar at <see cref="LethalKpa"/> (about
    /// 30 psi: lung damage, and in a vehicle explosion fragments and fire on top). A wall between
    /// the two lets <see cref="ShieldedFraction"/> of the overpressure round it.
    /// </para>
    /// <para>
    /// <b>Fire.</b> A burning vehicle radiates like a point source, q = P / (4πd²) kW/m², and a
    /// soldier near it accrues the thermal dose q^(4/3)·t that the Eisenberg burn probit measures;
    /// <see cref="LethalThermalDose"/> is the dose that kills half of those exposed. So damage per
    /// second is <see cref="FullHealth"/>·q^(4/3)/dose: standing 1 m from a burning jeep kills in
    /// about seven seconds, 2 m in about forty, and below <see cref="HarmlessFluxKw"/> (bearable
    /// for long periods) nothing at all.
    /// </para>
    /// </remarks>
    public static class WreckHazard
    {
        /// <summary>Health a soldier has at full health.</summary>
        public const float FullHealth = 100f;

        /// <summary>Peak overpressure, kPa, below which a blast hurts nobody (about 1 psi).</summary>
        public const float HarmlessKpa = 7f;

        /// <summary>Peak overpressure, kPa, that kills a soldier at full health (about 30 psi).</summary>
        public const float LethalKpa = 200f;

        /// <summary>Share of the overpressure that reaches a soldier behind a wall.</summary>
        public const float ShieldedFraction = 0.3f;

        /// <summary>Radiant heat flux, kW/m², that a soldier bears without harm.</summary>
        public const float HarmlessFluxKw = 2.5f;

        /// <summary>The thermal dose, (kW/m²)^(4/3)·s, that kills half of those exposed (Eisenberg).</summary>
        public const float LethalThermalDose = 2370f;

        /// <summary>The scaled distance, m/kg^(1/3), at which the overpressure falls to <see cref="HarmlessKpa"/>.</summary>
        public static readonly float HarmlessScaledDistance = ScaledDistanceAt(HarmlessKpa);

        /// <summary>The scaled distance, m/kg^(1/3), at which the overpressure reaches <see cref="LethalKpa"/>.</summary>
        public static readonly float LethalScaledDistance = ScaledDistanceAt(LethalKpa);

        /// <summary>
        /// Peak free-air overpressure, kPa, <paramref name="distanceMetres"/> from a charge of
        /// <paramref name="tntKg"/> kg TNT-equivalent (Mills 1987).
        /// </summary>
        public static float OverpressureKpa(float distanceMetres, float tntKg)
        {
            if (!(tntKg > 0f)) return 0f;
            double z = Math.Max(0.05, distanceMetres) / Math.Pow(tntKg, 1.0 / 3.0);
            return (float)Mills(z);
        }

        /// <summary>
        /// The health a peak overpressure of <paramref name="kpa"/> takes: zero at
        /// <see cref="HarmlessKpa"/> or less, <see cref="FullHealth"/> at <see cref="LethalKpa"/>
        /// or more, logarithmic in between.
        /// </summary>
        public static float BlastDamageAt(float kpa)
        {
            if (!(kpa > HarmlessKpa)) return 0f;
            double t = Math.Log(kpa / HarmlessKpa) / Math.Log(LethalKpa / HarmlessKpa);
            return FullHealth * (float)Math.Min(1.0, t);
        }

        /// <summary>
        /// The health a soldier <paramref name="distanceMetres"/> from a <paramref name="tntKg"/>
        /// kg blast loses, behind a wall or in the open.
        /// </summary>
        public static float BlastDamage(float distanceMetres, float tntKg, bool shielded)
        {
            float kpa = OverpressureKpa(distanceMetres, tntKg);
            return BlastDamageAt(shielded ? kpa * ShieldedFraction : kpa);
        }

        /// <summary>The distance, metres, past which a <paramref name="tntKg"/> kg blast hurts nobody.</summary>
        public static float BlastReach(float tntKg)
            => tntKg > 0f ? HarmlessScaledDistance * (float)Math.Pow(tntKg, 1.0 / 3.0) : 0f;

        /// <summary>The distance, metres, within which a <paramref name="tntKg"/> kg blast kills in the open.</summary>
        public static float LethalRadius(float tntKg)
            => tntKg > 0f ? LethalScaledDistance * (float)Math.Pow(tntKg, 1.0 / 3.0) : 0f;

        /// <summary>
        /// Radiant heat flux, kW/m², <paramref name="distanceMetres"/> from a fire radiating
        /// <paramref name="radiatedMegawatts"/>.
        /// </summary>
        public static float HeatFluxKw(float distanceMetres, float radiatedMegawatts)
        {
            if (!(radiatedMegawatts > 0f)) return 0f;
            double d = Math.Max(0.5, distanceMetres);
            return (float)(radiatedMegawatts * 1000.0 / (4.0 * Math.PI * d * d));
        }

        /// <summary>
        /// Health lost per second <paramref name="distanceMetres"/> from a fire radiating
        /// <paramref name="radiatedMegawatts"/>, with nothing in between.
        /// </summary>
        public static float FireDamagePerSecond(float distanceMetres, float radiatedMegawatts)
        {
            float q = HeatFluxKw(distanceMetres, radiatedMegawatts);
            if (!(q > HarmlessFluxKw)) return 0f;
            return FullHealth * (float)Math.Pow(q, 4.0 / 3.0) / LethalThermalDose;
        }

        /// <summary>The distance, metres, past which a fire radiating <paramref name="radiatedMegawatts"/> hurts nobody.</summary>
        public static float FireReach(float radiatedMegawatts)
            => radiatedMegawatts > 0f ? (float)Math.Sqrt(radiatedMegawatts * 1000.0 / (4.0 * Math.PI * HarmlessFluxKw)) : 0f;

        private static double Mills(double z) => 1772.0 / (z * z * z) - 114.0 / (z * z) + 108.0 / z;

        /// <summary>The scaled distance at which <see cref="Mills"/> falls to <paramref name="kpa"/>: it is monotonic past its peak.</summary>
        private static float ScaledDistanceAt(float kpa)
        {
            double lo = 0.5, hi = 200.0;
            for (int i = 0; i < 80; i++)
            {
                double mid = 0.5 * (lo + hi);
                if (Mills(mid) > kpa) lo = mid; else hi = mid;
            }
            return (float)(0.5 * (lo + hi));
        }
    }
}
