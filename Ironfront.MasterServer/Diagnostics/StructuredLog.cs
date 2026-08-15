using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Ironfront.MasterServer.Diagnostics
{
    /// <summary>
    /// One JSON object per line, on stdout, for the events an operator greps for
    /// (phase 03 task 3).
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is a second channel beside <see cref="MasterLog"/>, not a replacement for it.
    /// <see cref="MasterLog"/> is prose a human reads while watching a terminal;
    /// <c>StructuredLog</c> is a record a script aggregates — <c>jq 'select(.type ==
    /// "login")'</c>, count logins per minute, find the ten slowest joins. Merging them would
    /// make one of the two jobs worse: prose is unparseable, and JSON is unreadable at 3am.
    /// </para>
    /// <para>
    /// <b>Redaction is enforced here rather than trusted at every call site.</b> Every value
    /// registered with <see cref="Redact"/> — the shared secret, the certificate password —
    /// is replaced in the serialised line before it is written. Relying on "nobody will ever
    /// log the secret" is exactly how secrets end up in logs, and phase 03 criterion 11
    /// (<c>grep -i secret /var/log/ironfront/*</c> comes back empty) is a criterion precisely
    /// because it is otherwise so easy to fail by accident.
    /// </para>
    /// <para>
    /// Session tokens are never passed in at all. Redaction cannot help there — a token is
    /// minted per login, so there is no fixed value to register.
    /// </para>
    /// </remarks>
    public static class StructuredLog
    {
        private const string RedactionMarker = "[redacted]";

        private static readonly object Gate = new object();
        private static readonly List<string> Secrets = new List<string>();
        private static readonly JsonSerializerOptions Json = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false,
        };

        // PascalCase, not _output: conventions.md section 3.1 gives private STATIC fields
        // PascalCase and reserves the underscore prefix for instance fields. Caught by the
        // dotnet-format style gate (IDE1006), which is what that gate is for.
        private static TextWriter Output = Console.Out;

        /// <summary>
        /// Off by default so a developer running the server locally gets readable output.
        /// <c>IRONFRONT_STRUCTURED_LOG=1</c> turns it on, which is what the systemd unit does.
        /// </summary>
        public static bool Enabled { get; set; }

        /// <summary>
        /// Registers a value that must never appear in output. Idempotent; short values are
        /// ignored, because redacting a two-character string would blank out unrelated text.
        /// </summary>
        public static void Redact(string? secret)
        {
            if (string.IsNullOrWhiteSpace(secret) || secret.Length < 8) return;

            lock (Gate)
            {
                if (!Secrets.Contains(secret)) Secrets.Add(secret);
            }
        }

        /// <summary>Drops every registered secret. For tests.</summary>
        internal static void ClearRedactions()
        {
            lock (Gate) Secrets.Clear();
        }

        /// <summary>Redirects output. For tests; production always writes to stdout.</summary>
        internal static void RedirectTo(TextWriter writer)
        {
            lock (Gate) Output = writer ?? throw new ArgumentNullException(nameof(writer));
        }

        /// <summary>Restores stdout.</summary>
        internal static void RestoreOutput()
        {
            lock (Gate) Output = Console.Out;
        }

        /// <summary>
        /// Writes one event. <paramref name="data"/> is serialised as the <c>data</c> member;
        /// anonymous types are the expected shape.
        /// </summary>
        public static void Event(string type, object data)
        {
            if (!Enabled) return;
            Write(Format(type, data));
        }

        /// <summary>
        /// Serialises an event without writing it. Exposed so a test can assert on the exact
        /// bytes — including that a registered secret is not among them.
        /// </summary>
        internal static string Format(string type, object data)
        {
            string line;
            try
            {
                line = JsonSerializer.Serialize(
                    new
                    {
                        ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                        type,
                        data,
                    },
                    Json);
            }
            catch (NotSupportedException ex)
            {
                // An un-serialisable payload must not take a request path down with it. The
                // event is worth less than the connection it was describing.
                line = JsonSerializer.Serialize(new
                {
                    ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    type = "log_error",
                    data = new { forType = type, error = ex.GetType().Name },
                });
            }

            return Scrub(line);
        }

        private static string Scrub(string line)
        {
            lock (Gate)
            {
                for (int i = 0; i < Secrets.Count; i++)
                    line = line.Replace(Secrets[i], RedactionMarker, StringComparison.Ordinal);
            }

            return line;
        }

        private static void Write(string line)
        {
            lock (Gate)
            {
                Output.WriteLine(line);
            }
        }
    }
}
