using System;
using System.IO;

namespace Ironfront.MasterServer.Diagnostics
{
    /// <summary>
    /// A copy of every log line on disk, so an incident can still be read after the console's
    /// buffer is gone. Two files of at most <see cref="MaxBytesPerFile"/>: the live one and
    /// <c>.1</c>, the one before it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why it exists.</b> On fly.io the master's console is the platform's log stream, and its
    /// API returns the last few minutes only. On 2026-10-14 the master counted 105 error replies in
    /// one minute while a class could not get in, and by the time anyone looked, the lines that
    /// said which errors they were had been dropped. The durability CSV said how many; nothing said
    /// what.
    /// </para>
    /// <para>
    /// <b>A disk problem turns it off, loudly, and never takes the master down.</b> The first write
    /// that fails closes the file and says so on the console; the master keeps serving. A log is
    /// worth less than the players it describes.
    /// </para>
    /// </remarks>
    public static class LogFile
    {
        /// <summary>
        /// The size at which the live file is rotated. Two such files sit beside the database on a
        /// 1 GB volume; at the Warn level a busy day is a few hundred kilobytes.
        /// </summary>
        public const long MaxBytesPerFile = 8L * 1024 * 1024;

        private static readonly object Gate = new object();
        private static StreamWriter? Writer;
        private static string FilePath = string.Empty;
        private static long MaxBytes = MaxBytesPerFile;

        /// <summary>Whether lines are being written to a file.</summary>
        public static bool IsOpen
        {
            get { lock (Gate) return Writer is not null; }
        }

        /// <summary>Appends to <paramref name="path"/> from now on. An empty path leaves it off.</summary>
        public static void Open(string path, long maxBytes = MaxBytesPerFile)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            if (maxBytes < 1) throw new ArgumentOutOfRangeException(nameof(maxBytes));

            lock (Gate)
            {
                CloseUnsafe();
                FilePath = path;
                MaxBytes = maxBytes;
                Writer = OpenWriter(path);
            }
        }

        /// <summary>Writes one line, rotating first when it would cross the size cap.</summary>
        public static void Append(string line)
        {
            lock (Gate)
            {
                if (Writer is null) return;
                try
                {
                    if (Writer.BaseStream.Length + line.Length + Environment.NewLine.Length > MaxBytes) RotateUnsafe();
                    Writer.WriteLine(line);
                    Writer.Flush();
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    CloseUnsafe();
                    Console.Out.WriteLine(
                        $"{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff}Z [ERROR] log file {FilePath} turned off: {ex.Message}");
                }
            }
        }

        /// <summary>Flushes and closes the file. Lines after this go to the console only.</summary>
        public static void Close()
        {
            lock (Gate) CloseUnsafe();
        }

        private static void RotateUnsafe()
        {
            Writer!.Dispose();
            File.Move(FilePath, FilePath + ".1", overwrite: true);
            Writer = OpenWriter(FilePath);
        }

        private static StreamWriter OpenWriter(string path)
        {
            string? directory = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
            return new StreamWriter(stream) { AutoFlush = false };
        }

        private static void CloseUnsafe()
        {
            Writer?.Dispose();
            Writer = null;
        }
    }
}
