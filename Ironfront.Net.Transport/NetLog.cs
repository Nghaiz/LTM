using System;

namespace Ironfront.Net.Transport
{
    /// <summary>Small, host-neutral logging hook for the transport library.</summary>
    public static class NetLog
    {
        /// <summary>Set to true only when per-packet diagnostics are needed.</summary>
        public static bool DebugEnabled { get; set; }

        /// <summary>Optional warning sink. The transport never writes to Console by itself.</summary>
        public static Action<string>? Warning { get; set; }

        /// <summary>Optional error sink.</summary>
        public static Action<string>? Error { get; set; }

        /// <summary>Optional debug sink.</summary>
        public static Action<string>? Debug { get; set; }

        public static void Warn(string message) => Warning?.Invoke(message);

        public static void LogError(string message) => Error?.Invoke(message);

        public static void LogDebug(string message)
        {
            if (DebugEnabled) Debug?.Invoke(message);
        }
    }
}
