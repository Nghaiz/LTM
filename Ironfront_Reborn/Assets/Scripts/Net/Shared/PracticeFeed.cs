#nullable enable

using System;
using Ironfront.Net.Protocol;
using UnityEngine;

namespace Ironfront.Net.Unity
{
    /// <summary>
    /// One death in the offline practice match, as the practice killfeed and Tab board need it:
    /// the facts the server would put in <c>S_DEATH</c>, and what the kill earned. Owner's run of
    /// 2026-10-12.
    /// </summary>
    /// <remarks>
    /// The actors are named by their <c>GetInstanceID</c>, the key the practice roster names them
    /// by too (<see cref="PracticeActor.Key"/>); the HUD numbers them. 0 is nobody.
    /// </remarks>
    public readonly struct PracticeDeath
    {
        public PracticeDeath(
            int victimKey, byte victimTeam, bool victimIsPlayer,
            int killerKey, byte killerTeam, bool killerIsPlayer,
            CauseOfDeath cause, byte weaponId, byte vehicleType, DeathDetail detail,
            byte killerSeatVehicleType, bool headshot, ushort distanceMetres, int points)
        {
            VictimKey = victimKey;
            VictimTeam = victimTeam;
            VictimIsPlayer = victimIsPlayer;
            KillerKey = killerKey;
            KillerTeam = killerTeam;
            KillerIsPlayer = killerIsPlayer;
            Cause = cause;
            WeaponId = weaponId;
            VehicleType = vehicleType;
            Detail = detail;
            KillerSeatVehicleType = killerSeatVehicleType;
            Headshot = headshot;
            DistanceMetres = distanceMetres;
            Points = points;
        }

        /// <summary>The actor that died.</summary>
        public int VictimKey { get; }

        public byte VictimTeam { get; }

        /// <summary>The victim is the person playing rather than a bot.</summary>
        public bool VictimIsPlayer { get; }

        /// <summary>Whom the damage named: 0 for nobody (the world), the victim's own key for its own doing.</summary>
        public int KillerKey { get; }

        /// <summary>The killer's side, <see cref="TeamId.None"/> when nobody killed.</summary>
        public byte KillerTeam { get; }

        public bool KillerIsPlayer { get; }

        public CauseOfDeath Cause { get; }

        /// <summary>
        /// The hand weapon the damage named (<see cref="WeaponIds"/>); <c>NONE</c> for a vehicle's
        /// own gun, a ram or a fall.
        /// </summary>
        public byte WeaponId { get; }

        /// <summary>The vehicle the damage named: the one that went down with the victim, or ran them over.</summary>
        public byte VehicleType { get; }

        /// <summary>What the damage's call site knew: went down with a vehicle, a melee blow.</summary>
        public DeathDetail Detail { get; }

        /// <summary>The vehicle the killer sat in (<see cref="VehicleIds"/>), <c>NONE</c> on foot.</summary>
        public byte KillerSeatVehicleType { get; }

        /// <summary>The killing blow struck a head hitbox.</summary>
        public bool Headshot { get; }

        /// <summary>Whole metres from the killer to the victim; 0 for the world or the victim's own doing.</summary>
        public ushort DistanceMetres { get; }

        /// <summary>What the kill put on the killer's side's score; 0 for a team kill or nobody's.</summary>
        public int Points { get; }
    }

    /// <summary>A flag in the practice match changing hands.</summary>
    public readonly struct PracticeFlagChange
    {
        public PracticeFlagChange(int key, string name, byte owner, byte previousOwner)
        {
            Key = key;
            Name = name ?? string.Empty;
            Owner = owner;
            PreviousOwner = previousOwner;
        }

        /// <summary>The capture point, by <c>GetInstanceID</c>.</summary>
        public int Key { get; }

        /// <summary>Its authored name, "Fortress Capture Point"; the feed words it.</summary>
        public string Name { get; }

        /// <summary>The side holding it now, <see cref="TeamId.None"/> for nobody.</summary>
        public byte Owner { get; }

        public byte PreviousOwner { get; }
    }

    /// <summary>One soldier in the practice match, as the Tab board lists them.</summary>
    public readonly struct PracticeActor
    {
        public PracticeActor(int key, byte team, bool isPlayer, bool isAlive, bool isSeated)
        {
            Key = key;
            Team = team;
            IsPlayer = isPlayer;
            IsAlive = isAlive;
            IsSeated = isSeated;
        }

        /// <summary>The actor's <c>GetInstanceID</c>, the key every practice report names it by.</summary>
        public int Key { get; }

        public byte Team { get; }

        /// <summary>The person playing rather than a bot.</summary>
        public bool IsPlayer { get; }

        public bool IsAlive { get; }

        /// <summary>In a vehicle seat.</summary>
        public bool IsSeated { get; }
    }

    /// <summary>The practice match's score: each side's points and flags held, and who won.</summary>
    public readonly struct PracticeScore
    {
        public PracticeScore(int score0, int score1, int flags0, int flags1, byte winningTeam)
        {
            Score0 = score0;
            Score1 = score1;
            Flags0 = flags0;
            Flags1 = flags1;
            WinningTeam = winningTeam;
        }

        public int Score0 { get; }

        public int Score1 { get; }

        /// <summary>Capture points team 0 holds.</summary>
        public int Flags0 { get; }

        public int Flags1 { get; }

        /// <summary>The side that won, <see cref="TeamId.None"/> while the match is open.</summary>
        public byte WinningTeam { get; }
    }

    /// <summary>
    /// What the practice HUD reads of the offline match while it runs: the score and who is in
    /// it. Implemented in <c>Assembly-CSharp</c> over <c>MatchScoreboard</c> and
    /// <c>ActorManager</c>, which no assembly definition can name.
    /// </summary>
    public interface IPracticeMatchView
    {
        PracticeScore Score { get; }

        /// <summary>
        /// Writes everyone in the match into <paramref name="actors"/>, in the order they joined
        /// it, and returns how many it wrote.
        /// </summary>
        int FillActors(PracticeActor[] actors);
    }

    /// <summary>
    /// What happens in the offline practice match, told to its HUD as it happens: each death and
    /// each flag changing hands. Raised by the legacy game (<c>Assembly-CSharp</c>) and drawn by
    /// the client's practice presenter as the online killfeed and Tab board are (owner's run of
    /// 2026-10-12).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Offline and local by construction.</b> Every report comes from the game running on this
    /// machine and goes to the HUD in the same process. Nothing here touches the network, so a
    /// practice match needs no server and no internet, and no server does any of this work.
    /// </para>
    /// <para>
    /// <b>A HUD fault never reaches the game.</b> The reports are made from inside the damage and
    /// capture paths, where a handler that threw would leave a death half-applied. It is caught
    /// and logged instead: the log says the HUD is broken, and the match carries on.
    /// </para>
    /// </remarks>
    public static class PracticeFeed
    {
        /// <summary>A soldier died in the practice match. Raised on the main thread.</summary>
        public static event Action<PracticeDeath>? Died;

        /// <summary>A flag in the practice match changed hands. Raised on the main thread.</summary>
        public static event Action<PracticeFlagChange>? FlagChanged;

        /// <summary>Whether a practice match has begun since the game started.</summary>
        public static bool HasMatch { get; private set; }

        /// <summary>How the latest practice match was set up; meaningful when <see cref="HasMatch"/>.</summary>
        public static PracticeMatch Match { get; private set; }

        /// <summary>A practice match began, set up as <paramref name="match"/>.</summary>
        public static void Begin(in PracticeMatch match)
        {
            Match = match;
            HasMatch = true;
        }

        /// <summary>Tells the HUD about one death.</summary>
        public static void ReportDeath(in PracticeDeath death)
        {
            Action<PracticeDeath>? handlers = Died;
            if (handlers == null) return;

            try
            {
                handlers(death);
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
            }
        }

        /// <summary>Tells the HUD a flag changed hands.</summary>
        public static void ReportFlag(in PracticeFlagChange change)
        {
            Action<PracticeFlagChange>? handlers = FlagChanged;
            if (handlers == null) return;

            try
            {
                handlers(change);
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
            }
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetOnLoad()
        {
            Died = null;
            FlagChanged = null;
            HasMatch = false;
            Match = default;
        }
    }
}
