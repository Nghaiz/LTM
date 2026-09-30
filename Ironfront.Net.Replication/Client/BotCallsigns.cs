using System.Globalization;

namespace Ironfront.Net.Replication.Client
{
    /// <summary>
    /// What a bot is called on screen: a callsign, "VIPER" or "HAVOC", instead of "Blue Team Bot 3".
    /// Owner's report of 2026-09-30.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Keyed on the actor id, so every client agrees with nothing new on the wire.</b> A bot keeps
    /// its actor through every death and respawn, so it keeps its callsign for the whole match, and
    /// two clients that hold the same spawns name the same bot the same way.
    /// </para>
    /// <para>
    /// <b>A callsign, never a person's name</b> (owner ruling 2026-09-29): a real player could share
    /// a made-up name, and a callsign reads as a unit in the field rather than as somebody. Written in
    /// capitals, which also sets a bot apart from the players, whose names keep their own case.
    /// </para>
    /// <para>
    /// The list holds a callsign for each of the 128 actor ids (protocol 13, 100-bot matches), so
    /// every bot in a match is named differently; an id past the end of the list takes a number
    /// after its callsign ("VIPER 2"), which keeps a larger roster unique too.
    /// </para>
    /// </remarks>
    public static class BotCallsigns
    {
        private static readonly string[] Names =
        {
            "VIPER", "HAVOC", "GHOST", "REAPER", "FALCON", "NOMAD", "RAVEN", "SABER",
            "TALON", "HUNTER", "WARDEN", "COBRA", "JACKAL", "ONYX", "TEMPEST", "BISHOP",
            "ROOK", "ATLAS", "TITAN", "BLAZE", "FROST", "SPECTER", "STRIKER", "PHANTOM",
            "VANDAL", "WRAITH", "MAKO", "KESTREL", "GRIZZLY", "MONGOOSE", "HYDRA", "ANVIL",
            "BOLT", "CINDER", "DAGGER", "ECHO", "FURY", "GUNNER", "HAWK", "IRONSIDE",
            "JESTER", "KODIAK", "LANCER", "MAMBA", "NOVA", "ORCA", "PANTHER", "QUAKE",
            "RANGER", "SPARROW", "THUNDER", "UMBRA", "VALOR", "WOLFHOUND", "YETI", "ZEPHYR",
            "ARROW", "BRICK", "COMET", "DIESEL", "EMBER", "FLINT", "GRANITE", "HARRIER",
            "ICARUS", "KRAKEN", "LYNX", "MAVERICK", "NIGHTHAWK", "OUTLAW", "PYTHON", "RAMPART",
            "SCORPION", "TOMAHAWK", "VULTURE", "WARTHOG", "BANSHEE", "CYCLONE", "GOLIATH", "HAMMER",
            "APEX", "BADGER", "BARRACUDA", "BLIZZARD", "BRONCO", "BUZZARD", "CARBINE", "CHIMERA",
            "CONDOR", "CORSAIR", "COYOTE", "CROSSBOW", "CUTLASS", "DINGO", "DRAGOON", "FIREBRAND",
            "FOXHOUND", "GARGOYLE", "GLADIUS", "GRIFFIN", "HALBERD", "HELLCAT", "HORNET", "HURRICANE",
            "JAVELIN", "JUGGERNAUT", "KATANA", "LEOPARD", "LONGBOW", "MARAUDER", "MUSTANG", "OSPREY",
            "PALADIN", "PEREGRINE", "RAPTOR", "RATTLER", "RIPTIDE", "SENTINEL", "SHRIKE", "SIDEWINDER",
            "SPARTAN", "STINGRAY", "TYPHOON", "WILDCAT", "WOLVERINE", "BULWARK", "CATAPULT", "DYNAMO",
        };

        /// <summary>How many callsigns there are before one repeats with a number.</summary>
        public static int Count => Names.Length;

        /// <summary>The callsign of the bot holding <paramref name="actorId"/>.</summary>
        public static string For(ushort actorId)
        {
            string name = Names[actorId % Names.Length];
            int round = actorId / Names.Length;
            return round == 0 ? name : name + " " + (round + 1).ToString(CultureInfo.InvariantCulture);
        }
    }
}
