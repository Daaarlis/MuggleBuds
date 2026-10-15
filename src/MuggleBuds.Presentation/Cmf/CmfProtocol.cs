using System.Text;
using MuggleBuds.Presentation.Headsets;

namespace MuggleBuds.Presentation.Cmf;

/// <summary>
/// Command IDs and payload layouts of the Nothing/CMF protocol for CMF Buds Pro 2 (support id
/// B172), as used by radiance-project/ear-web (res/js/bluetooth_socket.js).
/// </summary>
/// <remarks>
/// Payload offsets here are relative to the payload, which starts at byte 8 of a frame. The
/// ear-web sources count from the frame start, so ear-web's index 9 is payload index 1.
/// </remarks>
internal static class CmfProtocol
{
    /// <summary>
    /// RFCOMM service the buds' control protocol runs on.
    /// </summary>
    public static readonly Guid ServiceId = new("aeac4a03-dff5-498f-843a-34487cf133eb");

    // Reads: answered with CmfClient.ResponseTo(command)
    public const ushort ReadBattery  = 49159;
    public const ushort ReadInEar    = 49166;
    public const ushort ReadAnc      = 49182;
    public const ushort ReadLatency  = 49217;
    public const ushort ReadFirmware = 49218;
    public const ushort ReadBass     = 49230;
    public const ushort ReadListeningMode = 49232;

    // Sets
    public const ushort SetInEar   = 61444;
    public const ushort SetAnc     = 61455;
    public const ushort SetLatency = 61504;
    public const ushort SetBass    = 61521;
    public const ushort SetListeningMode = 61469;

    // Changes the buds report on their own (ear-web handles the 0x6003 form of the ANC push too)
    public const ushort BatteryPush = 57345;
    public const ushort AncPush     = 57347;
    public const ushort AncPushAlt  = 24579;

    // Battery cells
    private const byte LeftCell  = 0x02;
    private const byte RightCell = 0x03;
    private const byte CaseCell  = 0x04;

    private const int LevelMask        = 0x7F;
    private const int ChargingMask     = 0x80;
    private const int MaxBatteryLevel  = 100;

    // Entry of a noise control payload that holds the mode
    private const byte ModeSetting = 0x01;

    // The wire value of each noise control state. The same values are read and written (ear-web
    // readANC and setANC_BT map them to and from the same six levels)
    private const byte AncHigh         = 0x01;
    private const byte AncMid          = 0x02;
    private const byte AncLow          = 0x03;
    private const byte AncAdaptive     = 0x04;
    private const byte AncOff          = 0x05;
    private const byte AncTransparency = 0x07;

    /// <summary>
    /// Whether a packet's command carries battery levels (the reply to <see cref="ReadBattery"/> or a push).
    /// </summary>
    public static bool IsBattery(ushort command) =>
        command == CmfClient.ResponseTo(ReadBattery) || command == BatteryPush;

    public static bool IsAnc(ushort command) =>
        command == CmfClient.ResponseTo(ReadAnc) || command == AncPush || command == AncPushAlt;

    // =========================================================================
    // READING
    // =========================================================================

    /// <summary>
    /// Battery levels: a count, then (cell, value) pairs. The value's low 7 bits are the
    /// percentage and the top bit means charging. Null when the payload is empty.
    /// </summary>
    public static BatteryLevels? ParseBattery(byte[] payload)
    {
        if (payload.Length < 1)
        {
            return null;
        }

        int? left = null;
        int? right = null;
        int? caseLevel = null;
        var charging = false;
        var cells = payload[0];
        for (var i = 0; i < cells && 2 + (i * 2) < payload.Length; i++)
        {
            var cell  = payload[1 + (i * 2)];
            var value = payload[2 + (i * 2)];
            var level = Math.Min(value & LevelMask, MaxBatteryLevel);
            var isCharging = (value & ChargingMask) != 0;
            switch (cell)
            {
                case LeftCell:
                    left = level;
                    charging |= isCharging;
                    break;
                case RightCell:
                    right = level;
                    charging |= isCharging;
                    break;
                case CaseCell:
                    caseLevel = level;
                    break;
            }
        }
        return new BatteryLevels(null, left, right, caseLevel, charging);
    }

    /// <summary>
    /// Noise control state, or null when the payload has none or a value this app doesn't know.
    /// The strength is only set for noise cancellation.
    /// </summary>
    /// <remarks>
    /// The payload is a list of 3-byte entries (setting, value, 0), like the set command's single
    /// entry <c>01 mode 00</c>. Setting 1 is the noise control mode, in the same values the set
    /// command takes. A read's reply carries more entries, for example <c>02 03 00 01 05 00</c>
    /// from buds that were off: setting 2 is something else (it stays 3) and setting 1 is 5.
    /// ear-web reads the byte after the first entry's setting whatever it is, which is only right
    /// while the mode entry comes first; reading it as the mode made the app show noise
    /// cancellation after choosing off, transparency or adaptive.
    /// </remarks>
    public static (NoiseMode Mode, AncStrength? Strength)? ParseAnc(byte[] payload)
    {
        for (var i = 0; i + 1 < payload.Length; i += 3)
        {
            if (payload[i] != ModeSetting)
            {
                continue;
            }

            return payload[i + 1] switch
            {
                AncHigh         => (NoiseMode.NoiseCancellation, AncStrength.High),
                AncMid          => (NoiseMode.NoiseCancellation, AncStrength.Mid),
                AncLow          => (NoiseMode.NoiseCancellation, AncStrength.Low),
                AncAdaptive     => (NoiseMode.Adaptive, null),
                AncOff          => (NoiseMode.Off, null),
                AncTransparency => (NoiseMode.Transparency, null),
                _               => null,
            };
        }
        return null;
    }

    /// <summary>
    /// Firmware version as text: the whole payload is the ASCII version string.
    /// </summary>
    public static string ParseFirmware(byte[] payload) => Encoding.ASCII.GetString(payload).Trim('\0').Trim();

    /// <summary>
    /// In-ear detection: payload index 2 is 1 when on. Null when the payload is too short.
    /// </summary>
    public static bool? ParseInEar(byte[] payload) => payload.Length >= 3 ? payload[2] == 1 : null;

    /// <summary>
    /// Low latency mode: payload index 0 is 1 for on and 2 for off. Null for anything else.
    /// </summary>
    public static bool? ParseLatency(byte[] payload) => payload.Length >= 1 && (payload[0] == 1 || payload[0] == 2) ? payload[0] == 1 : null;

    /// <summary>
    /// Enhanced bass: payload index 0 is 1 when on and index 1 is the level in the buds' own
    /// units, which differ between hardware variants, so it's only ever echoed back.
    /// </summary>
    public static (bool Enabled, byte Level)? ParseBass(byte[] payload) =>
        payload.Length >= 2 ? (payload[0] == 1, payload[1]) : null;

    /// <summary>
    /// Listening mode: payload index 0 is the mode. Null for a number the buds don't define.
    /// </summary>
    public static ListeningMode? ParseListeningMode(byte[] payload) =>
        payload.Length >= 1 && Enum.IsDefined((ListeningMode)payload[0]) ? (ListeningMode)payload[0] : null;

    // =========================================================================
    // WRITING
    // =========================================================================

    /// <summary>
    /// The payload that sets a noise control mode; noise cancellation uses the given strength.
    /// </summary>
    public static byte[] AncPayload(NoiseMode mode, AncStrength strength)
    {
        var wire = mode switch
        {
            NoiseMode.Off          => AncOff,
            NoiseMode.Transparency => AncTransparency,
            NoiseMode.Adaptive     => AncAdaptive,
            _ => strength switch
            {
                AncStrength.Mid => AncMid,
                AncStrength.Low => AncLow,
                _               => AncHigh,
            },
        };
        return [0x01, wire, 0x00];
    }

    public static byte[] ListeningModePayload(ListeningMode mode) => [(byte)mode, 0x00];

    public static byte[] InEarPayload(bool enabled) => [0x01, 0x01, enabled ? (byte)0x01 : (byte)0x00];

    public static byte[] LatencyPayload(bool enabled) => [enabled ? (byte)0x01 : (byte)0x02, 0x00];

    public static byte[] BassPayload(bool enabled, byte level) => [enabled ? (byte)0x01 : (byte)0x00, level];
}
