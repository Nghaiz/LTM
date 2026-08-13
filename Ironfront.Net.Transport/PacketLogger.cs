using System;
using System.IO;
using System.Net;
using System.Text;

namespace Ironfront.Net.Transport
{
    /// <summary>
    /// Writes the transport's raw datagrams to the compact <c>.ifpcap</c> capture format.
    /// The logger is opt-in and is normally enabled with <c>IRONFRONT_PCAP</c>.
    /// </summary>
    /// <remarks>
    /// Captures are intentionally transport-level evidence, not application logs. Payload bytes
    /// may contain tickets or gameplay data, so capture files must be treated as sensitive and
    /// kept out of source control. The hot path only performs work when a logger was constructed.
    /// </remarks>
    public sealed class PacketLogger : IDisposable
    {
        public const ushort FormatVersion = 1;
        private static readonly byte[] Magic = Encoding.ASCII.GetBytes("IFPC");

        private readonly FileStream _stream;
        private readonly BinaryWriter _writer;
        private readonly double _startMonotonicMs;
        private bool _disposed;

        /// <summary>Creates or overwrites a capture file at <paramref name="path"/>.</summary>
        public PacketLogger(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("A capture path is required.", nameof(path));

            string fullPath = Path.GetFullPath(path);
            string? directory = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            _stream = new FileStream(
                fullPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.Read,
                64 * 1024,
                FileOptions.SequentialScan);
            _writer = new BinaryWriter(_stream, Encoding.UTF8, leaveOpen: true);
            _startMonotonicMs = NowMs();

            _writer.Write(Magic);
            _writer.Write(FormatVersion);
            _writer.Write((ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        }

        /// <summary>
        /// Creates a logger from <c>IRONFRONT_PCAP</c>. Invalid paths disable diagnostics and do
        /// not prevent the game server from starting.
        /// </summary>
        public static PacketLogger? FromEnvironment()
        {
            string? path = Environment.GetEnvironmentVariable("IRONFRONT_PCAP");
            if (string.IsNullOrWhiteSpace(path)) return null;

            try
            {
                return new PacketLogger(path);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException
                                        || ex is ArgumentException || ex is NotSupportedException)
            {
                NetLog.Warn($"packet capture disabled: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Writes one record. The endpoint and packet bytes are copied synchronously, so callers
        /// may immediately reuse pooled receive/send buffers.
        /// </summary>
        public void Log(bool outgoing, ReadOnlySpan<byte> data, EndPoint endpoint, double nowMs)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(PacketLogger));
            if (endpoint == null) throw new ArgumentNullException(nameof(endpoint));
            if (data.Length > ushort.MaxValue) throw new ArgumentOutOfRangeException(nameof(data));
            if (!EndpointKey.TryCreate(endpoint, out EndpointKey key)) return;

            double elapsed = nowMs - _startMonotonicMs;
            uint timestampMs = elapsed <= 0.0
                ? 0u
                : elapsed >= uint.MaxValue ? uint.MaxValue : (uint)elapsed;

            _writer.Write((byte)(outgoing ? 1 : 0));
            _writer.Write(timestampMs);
            _writer.Write(key.Address);
            _writer.Write(key.Port);
            _writer.Write((ushort)data.Length);
            _stream.Write(data);
        }

        /// <summary>Flushes the capture so another process can inspect it while the server runs.</summary>
        public void Flush(bool flushToDisk = false)
        {
            if (_disposed) return;
            _writer.Flush();
            if (flushToDisk) _stream.Flush(flushToDisk: true);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _writer.Flush();
            _writer.Dispose();
            _stream.Dispose();
        }

        internal static bool IsValidHeader(byte[] magic)
            => magic.Length == Magic.Length
               && magic[0] == Magic[0]
               && magic[1] == Magic[1]
               && magic[2] == Magic[2]
               && magic[3] == Magic[3];

        private static double NowMs()
            => System.Diagnostics.Stopwatch.GetTimestamp() * 1000.0
               / System.Diagnostics.Stopwatch.Frequency;
    }

    /// <summary>A decoded record from an <c>.ifpcap</c> file.</summary>
    public readonly struct PacketCaptureRecord
    {
        public PacketCaptureRecord(
            bool outgoing, uint timestampMs, uint address, ushort port, byte[] data)
        {
            Outgoing = outgoing;
            TimestampMs = timestampMs;
            Address = address;
            Port = port;
            Data = data ?? throw new ArgumentNullException(nameof(data));
        }

        public bool Outgoing { get; }
        public uint TimestampMs { get; }
        public uint Address { get; }
        public ushort Port { get; }
        public byte[] Data { get; }
    }

    /// <summary>Streaming reader for the versioned <c>.ifpcap</c> format.</summary>
    public sealed class PacketCaptureReader : IDisposable
    {
        private readonly FileStream _stream;
        private readonly BinaryReader _reader;
        private bool _disposed;

        public PacketCaptureReader(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("A capture path is required.", nameof(path));

            FileStream stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite,
                64 * 1024,
                FileOptions.SequentialScan);
            BinaryReader reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
            try
            {
                byte[] magic = reader.ReadBytes(4);
                if (magic.Length != 4 || !PacketLogger.IsValidHeader(magic))
                    throw new InvalidDataException("Not an IFPC capture (expected magic IFPC).");

                ushort version = reader.ReadUInt16();
                if (version != PacketLogger.FormatVersion)
                    throw new InvalidDataException($"Unsupported IFPC version {version}.");
                ulong startUnixMs = reader.ReadUInt64();

                _stream = stream;
                _reader = reader;
                FormatVersion = version;
                StartUnixMs = startUnixMs;
            }
            catch
            {
                reader.Dispose();
                stream.Dispose();
                throw;
            }
        }

        public ushort FormatVersion { get; }
        public ulong StartUnixMs { get; }

        /// <summary>Reads the next record; returns false at a clean end of file.</summary>
        public bool TryRead(out PacketCaptureRecord record)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(PacketCaptureReader));
            record = default;
            if (_stream.Position == _stream.Length) return false;

            byte direction = _reader.ReadByte();
            uint timestamp = _reader.ReadUInt32();
            uint address = _reader.ReadUInt32();
            ushort port = _reader.ReadUInt16();
            ushort length = _reader.ReadUInt16();
            if (direction > 1) throw new InvalidDataException("IFPC record has an invalid direction.");
            if (length > 1200) throw new InvalidDataException("IFPC record exceeds the safe MTU.");

            byte[] data = ReadExactly(length);
            record = new PacketCaptureRecord(direction == 1, timestamp, address, port, data);
            return true;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _reader.Dispose();
            _stream.Dispose();
        }

        private byte[] ReadExactly(int count)
        {
            byte[] bytes = _reader.ReadBytes(count);
            if (bytes.Length != count) throw new EndOfStreamException("IFPC record is truncated.");
            return bytes;
        }
    }
}
