#nullable enable

using Ironfront.Net.Protocol;
using Ironfront.Net.Replication.Client;

namespace Ironfront.Net.Unity.Client
{
    /// <summary>
    /// What a killfeed line needs to know about whom and what it names. The online presenter
    /// answers from the tables the wire fills; the practice presenter from the offline match's.
    /// </summary>
    internal interface IKillfeedSubjects
    {
        /// <summary>What an actor is called on screen: a player's name, a bot's callsign, or its id.</summary>
        string NameOf(ushort actorId);

        /// <summary>The side an actor is on now, <see cref="TeamId.None"/> when nothing says.</summary>
        byte TeamOf(ushort actorId);

        /// <summary>The name a player who left went by, or null when none was kept.</summary>
        string? DepartedNameOf(ushort actorId);

        /// <summary>A capture point's name as a player reads it: "Fortress Capture Point" is FORTRESS.</summary>
        string FlagNameOf(int pointIndex);
    }

    /// <summary>
    /// One killfeed entry, worded and resolved for the HUD. Owner's report of 2026-09-30.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The online feed's lines and the practice feed's, from one implementation</b> (owner's
    /// run of 2026-10-12: practice shows the online killfeed). What differs between the two is only
    /// where a name or a side comes from, which is <see cref="IKillfeedSubjects"/>.
    /// </para>
    /// <para>
    /// <b>A side resolved at the death is kept</b>, and only a side that was unknown then is
    /// looked up again here (<see cref="IKillfeedSubjects.TeamOf"/>).
    /// </para>
    /// </remarks>
    internal static class KillfeedLines
    {
        public static KillfeedLine For(
            in KillfeedEntry entry, IKillfeedSubjects subjects, float hold, bool hasLocal, ushort localActorId)
        {
            switch (entry.Kind)
            {
                case KillfeedKind.Death:
                {
                    KillfeedWording wording = KillfeedWording.For(in entry);

                    // A death nobody scored has no killer to name or colour: the sentence says
                    // what happened to the victim instead of "The world" killing them (feature 2).
                    bool scored = !wording.IsSentence;
                    KillfeedBadge badge = scored ? KillfeedWording.BadgeOf(entry.Accolades) : default;
                    string distance = scored && entry.DistanceMetres >= KillfeedAccoladeTracker.LongShotMetres
                        ? KillfeedWording.Distance(entry.DistanceMetres)
                        : string.Empty;

                    return new KillfeedLine(
                        entry.Sequence,
                        scored ? subjects.NameOf(entry.KillerActorId) : string.Empty,
                        scored ? Known(entry.KillerTeam, entry.KillerActorId, subjects) : TeamId.None,
                        subjects.NameOf(entry.VictimActorId),
                        Known(entry.VictimTeam, entry.VictimActorId, subjects),
                        wording.Label,
                        wording.Sentence,
                        entry.Headshot,
                        localIsKiller: scored && hasLocal && entry.KillerActorId == localActorId,
                        localIsVictim: hasLocal && entry.VictimActorId == localActorId,
                        weaponId: wording.WeaponId,
                        restAfterWeapon: wording.RestAfterWeapon,
                        glyph: (int)wording.Glyph,
                        restAfterGlyph: wording.RestAfterGlyph,
                        badge: badge.Text,
                        badgeTone: (int)badge.Tone,
                        distance: distance,
                        holdSeconds: hold);
                }

                case KillfeedKind.FlagCaptured:
                case KillfeedKind.FlagLost:
                {
                    bool captured = entry.Kind == KillfeedKind.FlagCaptured;
                    return EventLine(
                        in entry, ScoreboardWording.TeamName(entry.SubjectTeam), entry.SubjectTeam,
                        KillfeedWording.FlagVerb(captured), subjects.FlagNameOf(entry.SubjectIndex),
                        captured ? entry.SubjectTeam : TeamId.None, string.Empty, KillfeedGlyph.Flag,
                        highlight: false, hold);
                }

                case KillfeedKind.PlayerJoined:
                {
                    byte team = Known(entry.SubjectTeam, entry.SubjectActorId, subjects);
                    return EventLine(
                        in entry, subjects.NameOf(entry.SubjectActorId), team, KillfeedWording.JoinedVerb,
                        ScoreboardWording.TeamName(team), team, string.Empty, KillfeedGlyph.Joined,
                        highlight: false, hold);
                }

                case KillfeedKind.PlayerLeft:
                {
                    string name = subjects.DepartedNameOf(entry.SubjectActorId)
                                  ?? subjects.NameOf(entry.SubjectActorId);
                    return EventLine(
                        in entry, name, entry.SubjectTeam, string.Empty, string.Empty, TeamId.None,
                        KillfeedWording.LeftSentence, KillfeedGlyph.Left, highlight: false, hold);
                }

                case KillfeedKind.RoundStarted:
                    return EventLine(
                        in entry, string.Empty, TeamId.None, string.Empty, string.Empty, TeamId.None,
                        KillfeedWording.RoundWords(true, TeamId.None), KillfeedGlyph.Trophy,
                        highlight: false, hold);

                case KillfeedKind.RoundEnded:
                {
                    bool drawn = entry.SubjectTeam == TeamId.None;
                    return EventLine(
                        in entry, drawn ? string.Empty : ScoreboardWording.TeamName(entry.SubjectTeam),
                        entry.SubjectTeam, drawn ? string.Empty : KillfeedWording.RoundWords(false, entry.SubjectTeam),
                        string.Empty, TeamId.None,
                        drawn ? KillfeedWording.RoundWords(false, TeamId.None) : string.Empty,
                        KillfeedGlyph.Trophy, highlight: false, hold);
                }

                default:
                    return EventLine(
                        in entry, string.Empty, TeamId.None, string.Empty, string.Empty, TeamId.None,
                        KillfeedWording.OverflowWords(entry.SubjectIndex), KillfeedGlyph.Overflow,
                        highlight: false, hold);
            }
        }

        /// <summary>A line that is not a death: a subject, a verb, an object, closing words and a picture.</summary>
        private static KillfeedLine EventLine(
            in KillfeedEntry entry, string subject, byte subjectTeam, string verb, string target,
            byte targetTeam, string sentence, KillfeedGlyph glyph, bool highlight, float hold)
            => new KillfeedLine(
                entry.Sequence, subject, subjectTeam, target, targetTeam, string.Empty, sentence,
                headshot: false, localIsKiller: highlight, localIsVictim: false,
                glyph: (int)glyph, verb: verb, isEvent: true, holdSeconds: hold);

        /// <summary>The side a line was written with, or a fresh answer when it was written with none.</summary>
        private static byte Known(byte known, ushort actorId, IKillfeedSubjects subjects)
            => known != TeamId.None ? known : subjects.TeamOf(actorId);
    }
}
