namespace MuggleBuds.Presentation.Cmf;

/// <summary>
/// One frame of the Nothing/CMF control protocol, which runs over an RFCOMM byte stream.
/// </summary>
/// <remarks>
/// Layout: <c>55 60 01 | command (uint16, little-endian) | payload length | 00 | operation | payload | CRC16 (little-endian)</c>.
/// Outgoing frames have the CRC over every byte before it, as ear-web sends them (bluetooth_socket.js:
/// <c>send</c> and <c>crc16</c>). Checked against real CMF Buds Pro 2 traffic, there are two kinds of
/// incoming frame: replies start <c>55 60 01</c> and have the CRC over the payload alone, and the
/// pushes the buds send on their own (battery, noise control...) start <c>55 00 01</c> and have no
/// CRC at all.
/// </remarks>
internal sealed class CmfPacket(ushort command, byte operation, byte[] payload)
{
    public const byte Start        = 0x55;
    public const int HeaderLength  = 8;
    public const int CrcLength     = 2;
    public const int MaxPayloadLength = byte.MaxValue;

    // Second byte of a push, which has no CRC; replies and outgoing frames have 0x60
    private const byte PushMarker = 0x00;

    public ushort Command { get; } = command;

    public byte Operation { get; } = operation;

    public byte[] Payload { get; } = payload;

    /// <summary>
    /// The bytes to send: header, payload and CRC.
    /// </summary>
    public byte[] Encode()
    {
        if (Payload.Length > MaxPayloadLength)
        {
            throw new InvalidOperationException("A CMF payload is at most 255 bytes.");
        }

        var bytes = new byte[HeaderLength + Payload.Length + CrcLength];
        bytes[0] = Start;
        bytes[1] = 0x60;
        bytes[2] = 0x01;
        bytes[3] = (byte)(Command & 0xFF);
        bytes[4] = (byte)(Command >> 8);
        bytes[5] = (byte)Payload.Length;
        bytes[6] = 0x00;
        bytes[7] = Operation;
        Payload.CopyTo(bytes, HeaderLength);

        var crc = Crc16(bytes.AsSpan(0, HeaderLength + Payload.Length));
        bytes[^2] = (byte)(crc & 0xFF);
        bytes[^1] = (byte)(crc >> 8);
        return bytes;
    }

    /// <summary>
    /// CRC16/MODBUS: initial value 0xFFFF, reflected polynomial 0xA001.
    /// </summary>
    public static ushort Crc16(ReadOnlySpan<byte> data)
    {
        ushort crc = 0xFFFF;
        foreach (var value in data)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc & 1) != 0 ? (ushort)((crc >> 1) ^ 0xA001) : (ushort)(crc >> 1);
            }
        }
        return crc;
    }

    /// <summary>
    /// Cuts frames out of the byte stream. Reads can end anywhere, so bytes are kept until a
    /// whole frame has arrived. Replies are checked with the CRC over the payload; pushes have none.
    /// Bytes that aren't part of a valid frame are dropped
    /// (counted in <see cref="DroppedBytes"/>) and the reader looks for the next start byte.
    /// </summary>
    public sealed class Reader
    {
        // A frame is at most 8 + 255 + 2 bytes; anything much larger means the stream is garbage
        private const int MaxBuffered = 4096;

        private readonly List<byte> _buffer = [];

        /// <summary>
        /// Bytes thrown away so far because they didn't form a valid frame.
        /// </summary>
        public int DroppedBytes { get; private set; }

        public List<CmfPacket> Push(ReadOnlySpan<byte> data)
        {
            _buffer.AddRange(data);
            if (_buffer.Count > MaxBuffered)
            {
                DroppedBytes += _buffer.Count;
                _buffer.Clear();
                return [];
            }

            var packets = new List<CmfPacket>();
            while (true)
            {
                var start = _buffer.IndexOf(Start);
                if (start < 0)
                {
                    DroppedBytes += _buffer.Count;
                    _buffer.Clear();
                    break;
                }
                if (start > 0)
                {
                    DroppedBytes += start;
                    _buffer.RemoveRange(0, start);
                }
                if (_buffer.Count < HeaderLength)
                {
                    break;
                }

                var payloadLength = _buffer[5];
                var hasCrc = _buffer[1] != PushMarker;
                var total = HeaderLength + payloadLength + (hasCrc ? CrcLength : 0);
                if (_buffer.Count < total)
                {
                    break;
                }

                var frame = _buffer.GetRange(0, total).ToArray();
                if (hasCrc)
                {
                    var expected = (ushort)(frame[total - 2] | (frame[total - 1] << 8));
                    if (Crc16(frame.AsSpan(HeaderLength, payloadLength)) != expected)
                    {
                        // Not a frame after all; skip this start byte and look for the next one
                        DroppedBytes++;
                        _buffer.RemoveAt(0);
                        continue;
                    }
                }

                var command = (ushort)(frame[3] | (frame[4] << 8));
                packets.Add(new CmfPacket(command, frame[7], frame[HeaderLength..(HeaderLength + payloadLength)]));
                _buffer.RemoveRange(0, total);
            }
            return packets;
        }
    }
}
