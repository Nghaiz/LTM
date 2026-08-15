using System;
using System.Globalization;
using System.Net;

namespace Ironfront.Net.Configuration
{
    /// <summary>
    /// The shared parsers every process uses to turn an environment string into a value.
    /// </summary>
    /// <remarks>
    /// <para>
    /// These started as private methods on <c>MasterServerConfig</c>. Sharing them is not
    /// only about avoiding duplication: the point of a parser being shared is that a typo
    /// produces the <i>same</i> message in the master server, the game server and the load
    /// test, so an operator who has debugged one bad value has debugged all three.
    /// </para>
    /// <para>
    /// <b>They throw rather than falling back.</b> Silently substituting a default for a
    /// malformed value means the process listens somewhere the operator did not ask for and
    /// every client fails to connect for no visible reason. A misconfigured process is
    /// genuinely exceptional — it happens once, at startup, and the exception message is the
    /// user interface. That is not the packet path, where conventions.md section 3.2 says
    /// corrupt input returns false instead.
    /// </para>
    /// </remarks>
    public static class EnvParse
    {
        /// <summary>Trims, and maps null or all-whitespace to the empty string.</summary>
        public static string Trimmed(string? raw)
            => string.IsNullOrWhiteSpace(raw) ? string.Empty : raw!.Trim();

        /// <summary>True when the variable carries no usable value.</summary>
        public static bool IsBlank(string? raw) => string.IsNullOrWhiteSpace(raw);

        /// <summary>
        /// Accepts <c>1</c>, <c>true</c>, <c>yes</c> and <c>on</c>, case-insensitively;
        /// <c>0</c>, <c>false</c>, <c>no</c> and <c>off</c> for the negative.
        /// </summary>
        /// <remarks>
        /// Unlike the numeric parsers this one does NOT throw on an unrecognised value, and
        /// the asymmetry is deliberate. A flag turns a diagnostic channel or a development
        /// shortcut on and off; a diagnostic channel staying quietly off is a much smaller
        /// problem than a server refusing to boot over a typo in one.
        /// </remarks>
        public static bool Flag(string? raw, bool fallback = false)
        {
            if (IsBlank(raw)) return fallback;

            switch (raw!.Trim().ToLowerInvariant())
            {
                case "1":
                case "true":
                case "yes":
                case "on":  return true;
                case "0":
                case "false":
                case "no":
                case "off": return false;
                default:    return fallback;
            }
        }

        /// <summary>A whole number of at least 1.</summary>
        public static int PositiveInt(string? raw, int fallback, string variableName)
            => BoundedInt(raw, fallback, variableName, minimum: 1, "a positive integer");

        /// <summary>
        /// Same, but 0 is legal — it is how the global connection cap and the metrics
        /// endpoint are disabled.
        /// </summary>
        public static int NonNegativeInt(string? raw, int fallback, string variableName)
            => BoundedInt(raw, fallback, variableName, minimum: 0, "zero or a positive integer");

        /// <summary>A whole number no smaller than <paramref name="minimum"/>.</summary>
        public static int BoundedInt(string? raw, int fallback, string variableName, int minimum, string expected)
        {
            if (IsBlank(raw)) return fallback;

            if (!int.TryParse(raw!.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) ||
                value < minimum)
            {
                throw new InvalidOperationException($"{variableName}='{raw}' is not {expected}.");
            }

            return value;
        }

        /// <summary>A value in <c>0..255</c>, for the player counts the wire carries as a byte.</summary>
        public static byte Byte(string? raw, byte fallback, string variableName)
        {
            if (IsBlank(raw)) return fallback;

            if (!byte.TryParse(raw!.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out byte value))
            {
                throw new InvalidOperationException($"{variableName}='{raw}' is not a number in 0..255.");
            }

            return value;
        }

        /// <summary>
        /// A TCP or UDP port. <paramref name="allowZero"/> admits 0 as "disabled" rather than
        /// as "let the OS pick", which is what every caller here means by it.
        /// </summary>
        public static int Port(string? raw, int fallback, string variableName, bool allowZero = false)
        {
            if (IsBlank(raw)) return fallback;

            if (!int.TryParse(raw!.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int port) ||
                port > 65535 ||
                port < (allowZero ? 0 : 1))
            {
                throw new InvalidOperationException(
                    $"{variableName}='{raw}' is not a port in " +
                    $"{(allowZero ? "0..65535 (0 disables it)" : "1..65535")}.");
            }

            return port;
        }

        /// <summary>A literal IP address. Host names are rejected — see the remarks.</summary>
        /// <remarks>
        /// Used for bind addresses, where a name that resolves to several addresses has no
        /// single correct answer, and for the advertised public IP, where the value is handed
        /// to clients verbatim. Both want the operator to have decided.
        /// </remarks>
        public static IPAddress IpAddress(string? raw, IPAddress fallback, string variableName)
        {
            string text = Trimmed(raw);
            if (text.Length == 0) return fallback;

            if (!IPAddress.TryParse(text, out IPAddress? address))
            {
                throw new InvalidOperationException($"{variableName}='{raw}' is not an IP address.");
            }

            return address;
        }

        /// <summary>
        /// A comma-separated list of unsigned 16-bit ids, for the map list a game server
        /// advertises. An empty value is an empty list, which means "no preference".
        /// </summary>
        public static ushort[] UInt16List(string? raw, ushort[] fallback, string variableName)
        {
            string text = Trimmed(raw);
            if (text.Length == 0) return fallback;

            string[] parts = text.Split(',');
            var ids = new ushort[parts.Length];

            for (int i = 0; i < parts.Length; i++)
            {
                if (!ushort.TryParse(parts[i].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out ushort id))
                {
                    throw new InvalidOperationException(
                        $"{variableName}='{raw}' must be a comma-separated list of map ids in 0..65535; " +
                        $"'{parts[i].Trim()}' is not one.");
                }

                ids[i] = id;
            }

            return ids;
        }
    }
}
