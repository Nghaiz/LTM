using System;

namespace Ironfront.MasterServer.Diagnostics
{
    /// <summary>
    /// Verbosity threshold for <see cref="MasterLog"/>.
    /// </summary>
    /// <remarks>
    /// conventions.md section 3.3 defines exactly three levels, so there are exactly three
    /// here. <c>IRONFRONT_LOG_LEVEL</c> in <c>.env.example</c> is written as <c>Info</c>,
    /// which is not one of them — <see cref="MasterLog.TryParseLevel"/> accepts it as an
    /// alias for <see cref="Warn"/> (the default: warnings on, per-packet detail off) rather
    /// than growing a fourth level that the conventions do not have.
    /// </remarks>
    public enum MasterLogLevel
    {
        /// <summary>Errors only.</summary>
        Error = 0,

        /// <summary>Errors and warnings. The default.</summary>
        Warn = 1,

        /// <summary>Everything, including per-connection and per-frame detail.</summary>
        Debug = 2,
    }

    /// <summary>
    /// The master server's logger — the three levels from conventions.md section 3.3.
    /// </summary>
    /// <remarks>
    /// <para>
    /// conventions.md section 3.3 names <c>NetLog</c>, but no such class exists in the
    /// repository yet and it would belong in Dev B's <c>Ironfront.Net.Transport</c>, which
    /// Dev D may not edit (section 7). So the master server carries its own equivalent with
    /// the same three levels and the same <see cref="DebugEnabled"/> guard. If a shared
    /// <c>NetLog</c> ever lands in <c>Ironfront.Net.Protocol</c>, this becomes a thin
    /// forwarder.
    /// </para>
    /// <para>
    /// Everything goes to stdout, including errors: interleaving two streams destroys the
    /// ordering, and the ordering is the whole value of a connection log when you are trying
    /// to work out which of accept / timeout / disconnect happened first.
    /// </para>
    /// </remarks>
    public static class MasterLog
    {
        /// <summary>The active threshold. Settable at runtime, per conventions.md section 3.3.</summary>
        public static MasterLogLevel Level { get; set; } = MasterLogLevel.Warn;

        /// <summary>
        /// Guard for <see cref="Debug"/> call sites. Formatting an interpolated string costs
        /// even when the message is then thrown away, so per-frame logging must be wrapped
        /// in this check (conventions.md section 3.3).
        /// </summary>
        public static bool DebugEnabled => Level >= MasterLogLevel.Debug;

        /// <summary>A real error that needs handling. Always on.</summary>
        public static void Error(string message) => Write("ERROR", message);

        /// <summary>Abnormal but self-recovering. On by default.</summary>
        public static void Warn(string message)
        {
            if (Level >= MasterLogLevel.Warn) Write("WARN ", message);
        }

        /// <summary>Per-connection / per-frame detail. Off by default.</summary>
        public static void Debug(string message)
        {
            if (DebugEnabled) Write("DEBUG", message);
        }

        /// <summary>
        /// Parses an <c>IRONFRONT_LOG_LEVEL</c> value. <c>Info</c> is accepted as an alias
        /// for <see cref="MasterLogLevel.Warn"/> — see the note on <see cref="MasterLogLevel"/>.
        /// </summary>
        public static bool TryParseLevel(string? text, out MasterLogLevel level)
        {
            level = MasterLogLevel.Warn;
            if (string.IsNullOrWhiteSpace(text)) return false;

            switch (text.Trim().ToLowerInvariant())
            {
                case "error": level = MasterLogLevel.Error; return true;
                case "warn":
                case "warning":
                case "info": level = MasterLogLevel.Warn; return true;
                case "debug": level = MasterLogLevel.Debug; return true;
                default: return false;
            }
        }

        private static void Write(string tag, string message)
        {
            // UTC, because phase 03 puts this on a VPS in some other timezone and a log
            // whose timestamps need mental arithmetic is a log nobody reads.
            Console.Out.WriteLine($"{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff}Z [{tag}] {message}");
        }
    }
}
