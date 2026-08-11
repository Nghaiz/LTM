using System;
using System.Text;

namespace Ironfront.Net.Protocol.Tests
{
    /// <summary>
    /// Hex helpers so that a failing byte-layout assertion prints something a human can
    /// diff against the spec, rather than "byte[16] != byte[16]".
    /// </summary>
    internal static class Hex
    {
        /// <summary>Formats bytes as uppercase, space-separated hex.</summary>
        public static string ToHex(ReadOnlySpan<byte> bytes)
        {
            if (bytes.Length == 0) return string.Empty;

            var sb = new StringBuilder(bytes.Length * 3);
            for (int i = 0; i < bytes.Length; i++)
            {
                if (i > 0) sb.Append(' ');
                sb.Append(bytes[i].ToString("X2"));
            }
            return sb.ToString();
        }

        /// <summary>Parses space-separated hex back into bytes.</summary>
        public static byte[] FromHex(string hex)
        {
            string[] parts = hex.Split(new[] { ' ', '\n', '\r', '\t' },
                                       StringSplitOptions.RemoveEmptyEntries);
            var bytes = new byte[parts.Length];
            for (int i = 0; i < parts.Length; i++)
                bytes[i] = Convert.ToByte(parts[i], 16);
            return bytes;
        }

        /// <summary>Repeats one byte, for filler such as a dummy 64-byte joinTicket.</summary>
        public static string Repeat(byte value, int count)
        {
            var sb = new StringBuilder(count * 3);
            for (int i = 0; i < count; i++)
            {
                if (i > 0) sb.Append(' ');
                sb.Append(value.ToString("X2"));
            }
            return sb.ToString();
        }
    }
}
