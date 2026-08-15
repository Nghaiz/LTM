using System;
using System.Collections.Generic;
using System.IO;

namespace Ironfront.Net.Configuration
{
    /// <summary>
    /// A minimal <c>.env</c> reader: <c>KEY=VALUE</c> lines, <c>#</c> comments, nothing else.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Phase 00 objective 6 is "<c>.env</c> and secret management", and the failure message
    /// in <c>MasterServerConfig</c> tells the operator to "copy <c>.env.example</c> to
    /// <c>.env</c> and fill it in" — which is only true advice if something actually reads the
    /// file. Thirty lines here is cheaper than a NuGet dependency for a format this small.
    /// </para>
    /// <para>
    /// <b>A real environment variable always wins over the file.</b> That is the direction
    /// deployment needs: the VPS unit file (phase 03) sets the secret in the process
    /// environment, and a stale <c>.env</c> left in the working directory must not silently
    /// override it.
    /// </para>
    /// <para>
    /// Values are NOT unescaped beyond stripping one matching pair of surrounding quotes.
    /// A secret is base64, a path is a path; neither needs <c>\n</c> handling, and inventing
    /// escape rules that <c>.env.example</c> does not document would only create a way for
    /// the two sides of the shared secret to disagree.
    /// </para>
    /// <para>
    /// <b>Moved here from <c>Ironfront.MasterServer</c>.</b> It lived inside the master server
    /// while the master server was the only process that read configuration, and the cost of
    /// that showed up as soon as a second one did: the Unity game server and the tools could
    /// not load a <c>.env</c> at all, so the instruction printed by one process was false for
    /// every other.
    /// </para>
    /// </remarks>
    public static class DotEnv
    {
        /// <summary>The file name looked for, next to the working directory.</summary>
        public const string DefaultFileName = ".env";

        /// <summary>
        /// Loads <paramref name="path"/> into the process environment, skipping any variable
        /// that is already set. Missing file is not an error — the environment may be
        /// configured entirely outside the file.
        /// </summary>
        /// <returns>The number of variables actually applied.</returns>
        public static int Load(string path)
        {
            if (!File.Exists(path)) return 0;

            int applied = 0;
            foreach (KeyValuePair<string, string> entry in Parse(File.ReadAllLines(path)))
            {
                if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable(entry.Key))) continue;

                Environment.SetEnvironmentVariable(entry.Key, entry.Value);
                applied++;
            }

            return applied;
        }

        /// <summary>
        /// Loads <see cref="DefaultFileName"/> from the current directory, if it is there.
        /// </summary>
        public static int LoadDefault()
            => Load(Path.Combine(Directory.GetCurrentDirectory(), DefaultFileName));

        /// <summary>
        /// Loads <see cref="DefaultFileName"/> from <paramref name="startDirectory"/> or the
        /// nearest ancestor that has one, and returns the file that was used.
        /// </summary>
        /// <remarks>
        /// A Unity player runs with its working directory set to the build output, not to the
        /// checkout, and a developer running the master server through <c>dotnet run</c> is in
        /// the project directory rather than the repository root — so "the current directory"
        /// is the one place the file reliably is NOT. Walking up finds the single
        /// repository-root <c>.env</c> from any of them.
        /// </remarks>
        /// <param name="startDirectory">Where to start looking. Null means the current directory.</param>
        /// <param name="path">The file that was loaded, or the empty string when none was found.</param>
        /// <returns>The number of variables actually applied.</returns>
        public static int LoadFromAncestors(string? startDirectory, out string path)
        {
            path = string.Empty;

            var directory = new DirectoryInfo(
                string.IsNullOrWhiteSpace(startDirectory) ? Directory.GetCurrentDirectory() : startDirectory!);

            while (directory != null)
            {
                string candidate = Path.Combine(directory.FullName, DefaultFileName);
                if (File.Exists(candidate))
                {
                    path = candidate;
                    return Load(candidate);
                }

                directory = directory.Parent;
            }

            return 0;
        }

        /// <summary>
        /// Parses <c>.env</c> lines. Separated from <see cref="Load"/> so it can be tested
        /// without touching the process environment.
        /// </summary>
        public static IReadOnlyList<KeyValuePair<string, string>> Parse(IReadOnlyList<string> lines)
        {
            var parsed = new List<KeyValuePair<string, string>>();

            for (int i = 0; i < lines.Count; i++)
            {
                string line = lines[i].Trim();
                if (line.Length == 0 || line[0] == '#') continue;

                int separator = line.IndexOf('=');
                if (separator <= 0) continue;                      // no key, or no '=' at all

                string key = line.Substring(0, separator).Trim();
                string value = line.Substring(separator + 1).Trim();

                if (value.Length >= 2 &&
                    ((value[0] == '"' && value[value.Length - 1] == '"') ||
                     (value[0] == '\'' && value[value.Length - 1] == '\'')))
                {
                    value = value.Substring(1, value.Length - 2);
                }

                parsed.Add(new KeyValuePair<string, string>(key, value));
            }

            return parsed;
        }
    }
}
