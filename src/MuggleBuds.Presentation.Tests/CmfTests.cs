using MuggleBuds.Presentation.Cmf;
using MuggleBuds.Presentation.Headsets;

namespace MuggleBuds.Presentation.Tests;

// Reference frames were produced by running ear-web's own send() and crc16() (res/js/bluetooth_socket.js)
[TestClass]
public sealed class CmfPacketTests
{
    [TestMethod]
    public void Crc16MatchesTheStandardCheckValue() =>
        Assert.AreEqual((ushort)0x4B37, CmfPacket.Crc16("123456789"u8));

    [TestMethod]
    [DataRow((ushort)49159, 1, "", "55600107c0000001acdf")]
    [DataRow((ushort)49182, 3, "", "5560011ec000000330dc")]
    [DataRow((ushort)61455, 2, "010500", "5560010ff0030002010500fb53")]
    [DataRow((ushort)61444, 4, "010101", "55600104f00300040101017968")]
    [DataRow((ushort)61504, 5, "0100", "55600140f0020005010037fd")]
    public void EncodesLikeEarWeb(ushort command, int operation, string payloadHex, string expectedHex)
    {
        var packet = new CmfPacket(command, (byte)operation, Convert.FromHexString(payloadHex));

        Assert.AreEqual(expectedHex, Convert.ToHexStringLower(packet.Encode()));
    }

    // Frames the buds really sent (CMF Buds Pro 2), captured from the app's debug log. The CRC of a
    // reply covers the payload only, unlike the CRC of the frames sent to the buds; the pushes
    // the buds send on their own (55 00 ...) have no CRC.
    private const string BatteryFrame  = "5560010740070001030264036404641a9a";
    private const string FirmwareFrame = "5560014240080002312e302e312e3734d835";
    private const string AncFrame      = "5560011e400600030203000105001769";
    private const string LatencyFrame  = "5560014140010005023e81";
    private const string BassFrame     = "5560014e40020006000681b2";
    private const string AncPush       = "55000103e0030000010400";
    private const string BatteryPush   = "55000101e00500000202640364";

    [TestMethod]
    public void ReaderParsesAFrameFromTheBuds()
    {
        var reader = new CmfPacket.Reader();

        var packets = reader.Push(Convert.FromHexString(BatteryFrame));

        Assert.AreEqual(1, packets.Count);
        Assert.AreEqual((ushort)0x4007, packets[0].Command);
        Assert.AreEqual((byte)1, packets[0].Operation);
        CollectionAssert.AreEqual(Convert.FromHexString("03026403640464"), packets[0].Payload);
        Assert.AreEqual(0, reader.DroppedBytes);
    }

    [TestMethod]
    [DataRow(FirmwareFrame, (ushort)0x4042)]
    [DataRow(AncFrame, (ushort)0x401E)]
    [DataRow(LatencyFrame, (ushort)0x4041)]
    [DataRow(BassFrame, (ushort)0x404E)]
    public void ReaderParsesEveryCapturedFrame(string frameHex, ushort command)
    {
        var reader = new CmfPacket.Reader();

        var packets = reader.Push(Convert.FromHexString(frameHex));

        Assert.AreEqual(1, packets.Count);
        Assert.AreEqual(command, packets[0].Command);
        Assert.AreEqual(0, reader.DroppedBytes);
    }

    [TestMethod]
    public void ReaderParsesPushesWithoutACrc()
    {
        var reader = new CmfPacket.Reader();

        var packets = reader.Push(Convert.FromHexString(AncPush + BatteryPush));

        Assert.AreEqual(2, packets.Count);
        Assert.AreEqual((ushort)0xE003, packets[0].Command);
        CollectionAssert.AreEqual(Convert.FromHexString("010400"), packets[0].Payload);
        Assert.AreEqual((ushort)0xE001, packets[1].Command);
        CollectionAssert.AreEqual(Convert.FromHexString("0202640364"), packets[1].Payload);
        Assert.AreEqual(0, reader.DroppedBytes);
    }

    [TestMethod]
    public void ReaderJoinsAPushSplitAcrossReads()
    {
        var frame = Convert.FromHexString(AncPush);
        var reader = new CmfPacket.Reader();

        Assert.AreEqual(0, reader.Push(frame.AsSpan(0, 9)).Count);
        var packets = reader.Push(frame.AsSpan(9));

        Assert.AreEqual(1, packets.Count);
        Assert.AreEqual((ushort)0xE003, packets[0].Command);
    }

    [TestMethod]
    public void ReaderSeparatesAReplyFromAPushAfterIt()
    {
        var reader = new CmfPacket.Reader();

        var packets = reader.Push(Convert.FromHexString(LatencyFrame + AncPush));

        Assert.AreEqual(2, packets.Count);
        Assert.AreEqual((ushort)0x4041, packets[0].Command);
        Assert.AreEqual((ushort)0xE003, packets[1].Command);
    }

    [TestMethod]
    public void ReaderJoinsAFrameSplitAcrossReads()
    {
        var frame = Convert.FromHexString(BatteryFrame);
        var reader = new CmfPacket.Reader();

        Assert.AreEqual(0, reader.Push(frame.AsSpan(0, 5)).Count);
        Assert.AreEqual(0, reader.Push(frame.AsSpan(5, 4)).Count);
        var packets = reader.Push(frame.AsSpan(9));

        Assert.AreEqual(1, packets.Count);
        Assert.AreEqual((ushort)0x4007, packets[0].Command);
    }

    [TestMethod]
    public void ReaderSplitsTwoFramesInOneRead()
    {
        var reader = new CmfPacket.Reader();

        var packets = reader.Push(Convert.FromHexString(BatteryFrame + LatencyFrame));

        Assert.AreEqual(2, packets.Count);
        Assert.AreEqual((ushort)0x4007, packets[0].Command);
        Assert.AreEqual((ushort)0x4041, packets[1].Command);
    }

    [TestMethod]
    public void ReaderSkipsBytesBeforeAFrame()
    {
        var reader = new CmfPacket.Reader();

        var packets = reader.Push(Convert.FromHexString("0013" + LatencyFrame));

        Assert.AreEqual(1, packets.Count);
        Assert.AreEqual(2, reader.DroppedBytes);
    }

    [TestMethod]
    public void ReaderDropsAFrameWithABadCrc()
    {
        var reader = new CmfPacket.Reader();

        // The latency frame with its last CRC byte changed
        var packets = reader.Push(Convert.FromHexString("5560014140010005023e82"));

        Assert.AreEqual(0, packets.Count);
        Assert.IsTrue(reader.DroppedBytes > 0);
    }
}

[TestClass]
public sealed class CmfProtocolTests
{
    [TestMethod]
    [DataRow((ushort)49159, (ushort)16391)]
    [DataRow((ushort)49182, (ushort)16414)]
    [DataRow((ushort)49232, (ushort)16464)]
    public void AnswersAReadWithItsCommandMinusTheTopBit(ushort request, ushort response) =>
        Assert.AreEqual(response, CmfClient.ResponseTo(request));

    [TestMethod]
    public void RecognizesBatteryAndNoiseControlPackets()
    {
        Assert.IsTrue(CmfProtocol.IsBattery(16391));
        Assert.IsTrue(CmfProtocol.IsBattery(57345));
        Assert.IsFalse(CmfProtocol.IsBattery(16414));
        Assert.IsTrue(CmfProtocol.IsAnc(16414));
        Assert.IsTrue(CmfProtocol.IsAnc(57347));
        Assert.IsTrue(CmfProtocol.IsAnc(24579));
        Assert.IsFalse(CmfProtocol.IsAnc(16391));
    }

    [TestMethod]
    public void ParsesBatteryOfBothBudsAndTheCase()
    {
        // Three cells: left 75, right 72 and charging, case 100
        var battery = CmfProtocol.ParseBattery(Convert.FromHexString("03024b03c80464"));

        Assert.IsNotNull(battery);
        Assert.AreEqual(75, battery.Left);
        Assert.AreEqual(72, battery.Right);
        Assert.AreEqual(100, battery.Case);
        Assert.IsTrue(battery.Charging);
        Assert.IsNull(battery.Main);
    }

    [TestMethod]
    public void LeavesAMissingCellUnknown()
    {
        var battery = CmfProtocol.ParseBattery(Convert.FromHexString("01024b"));

        Assert.IsNotNull(battery);
        Assert.AreEqual(75, battery.Left);
        Assert.IsNull(battery.Right);
        Assert.IsNull(battery.Case);
        Assert.IsFalse(battery.Charging);
    }

    [TestMethod]
    public void IgnoresAShortBatteryPayload()
    {
        // Claims two cells but only carries one
        var battery = CmfProtocol.ParseBattery(Convert.FromHexString("02024b"));

        Assert.IsNotNull(battery);
        Assert.AreEqual(75, battery.Left);
        Assert.IsNull(battery.Right);
        Assert.IsNull(CmfProtocol.ParseBattery([]));
    }

    [TestMethod]
    [DataRow("010100", NoiseMode.NoiseCancellation, AncStrength.High)]
    [DataRow("010200", NoiseMode.NoiseCancellation, AncStrength.Mid)]
    [DataRow("010300", NoiseMode.NoiseCancellation, AncStrength.Low)]
    [DataRow("010400", NoiseMode.Adaptive, null)]
    [DataRow("010500", NoiseMode.Off, null)]
    [DataRow("010700", NoiseMode.Transparency, null)]
    public void ParsesNoiseControl(string payloadHex, NoiseMode expectedMode, AncStrength? expectedStrength)
    {
        var (mode, strength) = CmfProtocol.ParseAnc(Convert.FromHexString(payloadHex))
            ?? throw new AssertFailedException("The payload should parse.");

        Assert.AreEqual(expectedMode, mode);
        Assert.AreEqual(expectedStrength, strength);
    }

    [TestMethod]
    [DataRow("020300010700", NoiseMode.Transparency)]
    [DataRow("020300010400", NoiseMode.Adaptive)]
    [DataRow("020300010500", NoiseMode.Off)]
    public void TakesTheModeFromSettingOneWhenTheReplyHasMoreEntries(string payloadHex, NoiseMode expectedMode)
    {
        var (mode, _) = CmfProtocol.ParseAnc(Convert.FromHexString(payloadHex))
            ?? throw new AssertFailedException("The payload should parse.");

        Assert.AreEqual(expectedMode, mode);
    }

    [TestMethod]
    public void IgnoresAnUnknownNoiseControlValue()
    {
        Assert.IsNull(CmfProtocol.ParseAnc(Convert.FromHexString("010900")));
        Assert.IsNull(CmfProtocol.ParseAnc(Convert.FromHexString("01")));
        Assert.IsNull(CmfProtocol.ParseAnc(Convert.FromHexString("020300")));
    }

    [TestMethod]
    [DataRow(NoiseMode.Off, AncStrength.High, "010500")]
    [DataRow(NoiseMode.Transparency, AncStrength.High, "010700")]
    [DataRow(NoiseMode.Adaptive, AncStrength.Low, "010400")]
    [DataRow(NoiseMode.NoiseCancellation, AncStrength.High, "010100")]
    [DataRow(NoiseMode.NoiseCancellation, AncStrength.Mid, "010200")]
    [DataRow(NoiseMode.NoiseCancellation, AncStrength.Low, "010300")]
    public void BuildsTheNoiseControlPayload(NoiseMode mode, AncStrength strength, string expectedHex) =>
        Assert.AreEqual(expectedHex, Convert.ToHexStringLower(CmfProtocol.AncPayload(mode, strength)));

    [TestMethod]
    public void ParsesTheFirmwareVersion() =>
        Assert.AreEqual("2.0.5.20", CmfProtocol.ParseFirmware("2.0.5.20"u8.ToArray()));

    [TestMethod]
    public void ParsesInEarDetection()
    {
        Assert.IsTrue(CmfProtocol.ParseInEar(Convert.FromHexString("010101")) is true);
        Assert.IsTrue(CmfProtocol.ParseInEar(Convert.FromHexString("010100")) is false);
        Assert.IsNull(CmfProtocol.ParseInEar(Convert.FromHexString("0101")));
    }

    [TestMethod]
    public void ParsesLowLatency()
    {
        Assert.IsTrue(CmfProtocol.ParseLatency(Convert.FromHexString("0100")) is true);
        Assert.IsTrue(CmfProtocol.ParseLatency(Convert.FromHexString("0200")) is false);
        Assert.IsNull(CmfProtocol.ParseLatency(Convert.FromHexString("0000")));
    }

    [TestMethod]
    public void ParsesEnhancedBassAndKeepsTheLevelAsReported()
    {
        var (enabled, level) = CmfProtocol.ParseBass(Convert.FromHexString("0104"))
            ?? throw new AssertFailedException("The payload should parse.");

        Assert.IsTrue(enabled);
        Assert.AreEqual((byte)4, level);
        Assert.IsNull(CmfProtocol.ParseBass(Convert.FromHexString("01")));
    }

    [TestMethod]
    public void ParsesWhatTheBudsReallySent()
    {
        // Payloads of the frames captured from CMF Buds Pro 2
        var battery = CmfProtocol.ParseBattery(Convert.FromHexString("03026403640464"));
        Assert.IsNotNull(battery);
        Assert.AreEqual(100, battery.Left);
        Assert.AreEqual(100, battery.Right);
        Assert.AreEqual(100, battery.Case);
        Assert.IsFalse(battery.Charging);

        // Setting 2 holds 3; the mode is in setting 1, which is 5 (off)
        var (mode, strength) = CmfProtocol.ParseAnc(Convert.FromHexString("020300010500"))
            ?? throw new AssertFailedException("The payload should parse.");
        Assert.AreEqual(NoiseMode.Off, mode);
        Assert.IsNull(strength);

        Assert.AreEqual("1.0.1.74", CmfProtocol.ParseFirmware(Convert.FromHexString("312e302e312e3734")));
        Assert.IsTrue(CmfProtocol.ParseLatency(Convert.FromHexString("02")) is false);

        var (enabled, level) = CmfProtocol.ParseBass(Convert.FromHexString("0006"))
            ?? throw new AssertFailedException("The payload should parse.");
        Assert.IsFalse(enabled);
        Assert.AreEqual((byte)6, level);

        // The reply to the in-ear read is a count followed by (setting, value) pairs
        Assert.IsTrue(CmfProtocol.ParseInEar(Convert.FromHexString("0e010102000301070109010a000b000c010e010f011201150116011801")) is true);
    }

    [TestMethod]
    [DataRow("00", ListeningMode.DiracOpteo)]
    [DataRow("01", ListeningMode.Rock)]
    [DataRow("02", ListeningMode.Electronic)]
    [DataRow("03", ListeningMode.Pop)]
    [DataRow("04", ListeningMode.EnhanceVocals)]
    [DataRow("05", ListeningMode.Classical)]
    [DataRow("06", ListeningMode.Custom)]
    public void ParsesAndBuildsListeningModes(string numberHex, ListeningMode expected)
    {
        Assert.AreEqual(expected, CmfProtocol.ParseListeningMode(Convert.FromHexString(numberHex)));
        Assert.AreEqual(numberHex + "00", Convert.ToHexStringLower(CmfProtocol.ListeningModePayload(expected)));
    }

    [TestMethod]
    public void IgnoresAnUnknownListeningMode()
    {
        Assert.IsNull(CmfProtocol.ParseListeningMode(Convert.FromHexString("07")));
        Assert.IsNull(CmfProtocol.ParseListeningMode([]));
    }

    [TestMethod]
    public void BuildsTheSettingPayloads()
    {
        Assert.AreEqual("010101", Convert.ToHexStringLower(CmfProtocol.InEarPayload(true)));
        Assert.AreEqual("010100", Convert.ToHexStringLower(CmfProtocol.InEarPayload(false)));
        Assert.AreEqual("0100", Convert.ToHexStringLower(CmfProtocol.LatencyPayload(true)));
        Assert.AreEqual("0200", Convert.ToHexStringLower(CmfProtocol.LatencyPayload(false)));
        Assert.AreEqual("0104", Convert.ToHexStringLower(CmfProtocol.BassPayload(true, 4)));
        Assert.AreEqual("0004", Convert.ToHexStringLower(CmfProtocol.BassPayload(false, 4)));
    }
}
