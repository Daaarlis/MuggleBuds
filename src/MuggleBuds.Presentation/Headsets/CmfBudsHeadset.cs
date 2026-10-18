using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using MuggleBuds.Presentation.Cmf;
using MuggleBuds.Presentation.Logging;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Rfcomm;
using Windows.Networking.Sockets;

namespace MuggleBuds.Presentation.Headsets;

/// <summary>
/// <see cref="IHeadset"/> for Nothing CMF Buds Pro 2, over the Nothing/CMF control protocol.
/// </summary>
/// <remarks>
/// Connecting opens the RFCOMM socket and reads every setting the flyout shows. A setting the
/// buds don't answer keeps its default rather than failing the connect; only a link that
/// answers nothing at all does. Battery and noise control changes made on the buds arrive as
/// pushes. The protocol was derived from radiance-project/ear-web (see <see cref="CmfProtocol"/>).
/// Settings that only exist on Pixel Buds (conversation detection, multipoint, the 5-band
/// equalizer, touch controls, head gestures, hold actions, the noise control cycle) aren't
/// supported and fail with <see cref="NotSupportedException"/>.
/// </remarks>
public sealed class CmfBudsHeadset : IHeadset
{
    // The buds apply a set a moment after receiving it; reading it back sooner can show the old value
    private static readonly TimeSpan ReadBackDelay = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan CloseTimeout  = TimeSpan.FromSeconds(1);

    // WSAEADDRINUSE: "Only one usage of each socket address is normally permitted"
    private const int ChannelBusy = unchecked((int)0x80072740);

    // Read in this order when the link opens (ear-web reads them in much the same order)
    private static readonly (ushort Command, string Setting)[] StartupReads =
    [
        (CmfProtocol.ReadBattery, "battery"),
        (CmfProtocol.ReadFirmware, "firmware"),
        (CmfProtocol.ReadAnc, "noise control"),
        (CmfProtocol.ReadInEar, "in-ear detection"),
        (CmfProtocol.ReadLatency, "low latency"),
        (CmfProtocol.ReadBass, "enhanced bass"),
        (CmfProtocol.ReadListeningMode, "listening mode"),
    ];

    private readonly ILogger _logger;
    private readonly Lock _gate = new();
    private HeadsetSnapshot _state = HeadsetSnapshot.Empty;
    private byte? _bassLevel;
    private Link? _link;

    /// <param name="logger">Gets every packet at Debug level, and settings the buds won't answer.</param>
    public CmfBudsHeadset(ILogger logger)
    {
        _logger = logger;
    }

    public HeadsetSnapshot State
    {
        get
        {
            lock (_gate)
            {
                return _state;
            }
        }
    }

    public event EventHandler<HeadsetSnapshot>? StateChanged;

    public event EventHandler? Disconnected;

    // =========================================================================
    // CONNECTION
    // =========================================================================

    /// <summary>
    /// Opens the control link and loads the buds' current state.
    /// </summary>
    /// <param name="bluetoothAddress">Address like "AC:80:0A:12:34:56".</param>
    /// <exception cref="IOException">The buds or their control service couldn't be reached, or nothing answered.</exception>
    public async Task ConnectAsync(string bluetoothAddress)
    {
        Disconnect();

        // Socket
        var device = await BluetoothDevice.FromBluetoothAddressAsync(ParseAddress(bluetoothAddress))
            ?? throw new IOException("Windows doesn't know these buds.");
        var link = new Link(device);
        try
        {
            var serviceId = RfcommServiceId.FromUuid(CmfProtocol.ServiceId);
            var services  = await device.GetRfcommServicesForIdAsync(serviceId, BluetoothCacheMode.Uncached);
            if (services.Services.Count == 0)
            {
                services = await device.GetRfcommServicesForIdAsync(serviceId, BluetoothCacheMode.Cached);
            }
            if (services.Services.Count == 0)
            {
                throw new IOException("The buds don't offer their control service right now.");
            }
            var service = services.Services[0];
            foreach (var offered in services.Services)
            {
                var description = $"{offered.ConnectionHostName} {offered.ConnectionServiceName} {offered.ServiceId.Uuid}";
                LogMessages.ServiceOffered(_logger, description);
            }

            await link.Socket.ConnectAsync(service.ConnectionHostName, service.ConnectionServiceName);
            link.Client = new CmfClient(link.Socket.InputStream.AsStreamForRead(0), link.Socket.OutputStream.AsStreamForWrite(0), _logger);
            link.Client.PacketReceived += OnPacket;
            link.Client.Closed += OnClientClosed;

            lock (_gate)
            {
                _link      = link;
                _state     = HeadsetSnapshot.Empty;
                _bassLevel = null;
            }

            // Settings. Each answer updates the snapshot through OnPacket.
            var answered = 0;
            foreach (var (command, setting) in StartupReads)
            {
                if (await ReadAsync(link, command, setting).ConfigureAwait(false))
                {
                    answered++;
                }
            }
            if (answered == 0)
            {
                throw new IOException("The buds didn't answer on their control service.");
            }
        }
        catch (Exception ex)
        {
            LogIfChannelBusy(ex);

            // The link may not be published yet (the socket failed to connect), so close it here too
            Disconnect();
            _ = link.Close(OnClientClosed);
            throw;
        }
    }

    // Windows says "address in use" when something already has the buds' control channel open: another
    // app, or another copy of this one that hasn't ended
    private void LogIfChannelBusy(Exception exception)
    {
        if (exception is not COMException { HResult: ChannelBusy })
        {
            return;
        }

        var others = 0;
        var name = Path.GetFileNameWithoutExtension(Environment.ProcessPath);
        if (name is not null)
        {
            foreach (var process in Process.GetProcessesByName(name))
            {
                using (process)
                {
                    if (process.Id != Environment.ProcessId)
                    {
                        others++;
                    }
                }
            }
        }
        LogMessages.CmfChannelBusy(_logger, others);
    }

    /// <summary>
    /// Closes the control link. Doesn't raise <see cref="Disconnected"/>.
    /// </summary>
    public void Disconnect() => _ = Close();

    public void Dispose() => Close().Wait(CloseTimeout);

    private Task Close()
    {
        Link? link;
        lock (_gate)
        {
            link  = _link;
            _link = null;
        }
        return link?.Close(OnClientClosed) ?? Task.CompletedTask;
    }

    // =========================================================================
    // SETTINGS
    // =========================================================================

    public Task SetNoiseModeAsync(NoiseMode mode) =>
        WriteAsync(
            CmfProtocol.SetAnc,
            CmfProtocol.AncPayload(mode, State.AncStrength),
            CmfProtocol.ReadAnc,
            state => state with { NoiseMode = mode });

    /// <summary>
    /// Sets how strongly noise cancellation works; this also turns noise cancellation on.
    /// </summary>
    public Task SetAncStrengthAsync(AncStrength strength) =>
        WriteAsync(
            CmfProtocol.SetAnc,
            CmfProtocol.AncPayload(NoiseMode.NoiseCancellation, strength),
            CmfProtocol.ReadAnc,
            state => state with { NoiseMode = NoiseMode.NoiseCancellation, AncStrength = strength });

    public Task SetInEarDetectionAsync(bool enabled) =>
        WriteAsync(
            CmfProtocol.SetInEar,
            CmfProtocol.InEarPayload(enabled),
            CmfProtocol.ReadInEar,
            state => state with { InEarDetection = enabled });

    public Task SetLowLatencyAsync(bool enabled) =>
        WriteAsync(
            CmfProtocol.SetLatency,
            CmfProtocol.LatencyPayload(enabled),
            CmfProtocol.ReadLatency,
            state => state with { LowLatency = enabled });

    public Task SetListeningModeAsync(ListeningMode mode) =>
        WriteAsync(
            CmfProtocol.SetListeningMode,
            CmfProtocol.ListeningModePayload(mode),
            CmfProtocol.ReadListeningMode,
            state => state with { ListeningMode = mode });

    /// <summary>
    /// Turns enhanced bass on or off at the level the buds last reported, since the level's
    /// units differ between hardware variants.
    /// </summary>
    public Task SetEnhancedBassAsync(bool enabled)
    {
        byte level;
        lock (_gate)
        {
            level = _bassLevel ?? throw new InvalidOperationException("The buds haven't reported their bass level.");
        }
        if (enabled && level == 0)
        {
            // A reported level of 0 means no boost; there's no known-good level to turn it on at
            throw new InvalidOperationException("Pick a bass level in the Nothing X app first.");
        }

        return WriteAsync(
            CmfProtocol.SetBass,
            CmfProtocol.BassPayload(enabled, level),
            CmfProtocol.ReadBass,
            state => state with { EnhancedBass = enabled });
    }

    // Pixel Buds only
    public Task SetConversationDetectionAsync(bool enabled) => NotSupported();

    public Task SetMultipointAsync(bool enabled) => NotSupported();

    public Task SetEqualizerAsync(EqualizerBands bands) => NotSupported();

    public Task SetTouchControlsAsync(bool enabled) => NotSupported();

    public Task SetHeadGesturesAsync(bool enabled) => NotSupported();

    public Task SetHoldActionAsync(EarbudSide side, HoldAction action) => NotSupported();

    public Task SetNoiseModeCycleAsync(NoiseModeCycle cycle) => NotSupported();

    private static Task NotSupported() =>
        Task.FromException(new NotSupportedException("CMF Buds don't have this setting."));

    // =========================================================================
    // MESSAGE MAPPING
    // =========================================================================

    private void OnPacket(object? sender, CmfPacket packet)
    {
        var payload = packet.Payload;
        if (CmfProtocol.IsBattery(packet.Command))
        {
            if (CmfProtocol.ParseBattery(payload) is { } battery)
            {
                Update(state => state with { Battery = battery });
            }
            else
            {
                LogMessages.CmfReplyUnreadable(_logger, "battery");
            }
        }
        else if (CmfProtocol.IsAnc(packet.Command))
        {
            if (CmfProtocol.ParseAnc(payload) is { } anc)
            {
                Update(state => state with
                {
                    NoiseMode   = anc.Mode,
                    AncStrength = anc.Strength ?? state.AncStrength,
                });
            }
            else
            {
                // A value this app doesn't know (or none while the buds are out of the ears)
                Update(state => state with { NoiseMode = null });
                LogMessages.CmfReplyUnreadable(_logger, "noise control");
            }
        }
        else if (packet.Command == CmfClient.ResponseTo(CmfProtocol.ReadFirmware))
        {
            var firmware = CmfProtocol.ParseFirmware(payload);
            Update(state => state with { Firmware = firmware });
        }
        else if (packet.Command == CmfClient.ResponseTo(CmfProtocol.ReadInEar))
        {
            if (CmfProtocol.ParseInEar(payload) is { } inEar)
            {
                Update(state => state with { InEarDetection = inEar });
            }
        }
        else if (packet.Command == CmfClient.ResponseTo(CmfProtocol.ReadLatency))
        {
            if (CmfProtocol.ParseLatency(payload) is { } lowLatency)
            {
                Update(state => state with { LowLatency = lowLatency });
            }
        }
        else if (packet.Command == CmfClient.ResponseTo(CmfProtocol.ReadListeningMode))
        {
            if (CmfProtocol.ParseListeningMode(payload) is { } listeningMode)
            {
                Update(state => state with { ListeningMode = listeningMode });
            }
        }
        else if (packet.Command == CmfClient.ResponseTo(CmfProtocol.ReadBass))
        {
            if (CmfProtocol.ParseBass(payload) is { } bass)
            {
                lock (_gate)
                {
                    _bassLevel = bass.Level;
                }
                Update(state => state with { EnhancedBass = bass.Enabled });
            }
        }
    }

    private static ulong ParseAddress(string address) =>
        ulong.Parse(address.Replace(":", "", StringComparison.Ordinal), NumberStyles.HexNumber, CultureInfo.InvariantCulture);

    // =========================================================================
    // LINK
    // =========================================================================

    private async Task<bool> ReadAsync(Link link, ushort command, string setting)
    {
        try
        {
            await link.Client!.RequestAsync(command, []).ConfigureAwait(false);
            return true;
        }
        catch (TimeoutException)
        {
            // The buds don't answer this setting; keep the default
            LogMessages.CmfSettingUnanswered(_logger, setting);
            return false;
        }
    }

    // Sends a set, applies it to the snapshot, then reads the setting back so the snapshot
    // shows what the buds actually ended up with
    private async Task WriteAsync(ushort command, byte[] payload, ushort readBack, Func<HeadsetSnapshot, HeadsetSnapshot> apply)
    {
        CmfClient client;
        lock (_gate)
        {
            client = _link?.Client ?? throw new IOException("The buds aren't connected.");
        }

        await client.SendAsync(command, payload).ConfigureAwait(false);
        Update(apply);

        await Task.Delay(ReadBackDelay).ConfigureAwait(false);
        try
        {
            await client.RequestAsync(readBack, []).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            LogMessages.CmfSettingUnanswered(_logger, "read-back");
        }
    }

    private void Update(Func<HeadsetSnapshot, HeadsetSnapshot> change)
    {
        HeadsetSnapshot state;
        lock (_gate)
        {
            if (_link is null)
            {
                return;
            }
            state = change(_state);
            if (state == _state)
            {
                return;
            }
            _state = state;
        }
        StateChanged?.Invoke(this, state);
    }

    private void OnClientClosed(object? sender, EventArgs e)
    {
        lock (_gate)
        {
            // Only the current link's close counts; an old one closing after a reconnect doesn't
            if (_link?.Client != sender)
            {
                return;
            }
        }
        Disconnect();
        Disconnected?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// The open socket and everything riding on it.
    /// </summary>
    private sealed class Link(BluetoothDevice device)
    {
        public BluetoothDevice Device { get; } = device;

        public StreamSocket Socket { get; } = new();

        public CmfClient? Client { get; set; }

        private Task? _closing;

        // Closes once: a failed connect closes the link both through Disconnect and directly
        public Task Close(EventHandler onClosed)
        {
            if (Client is not null)
            {
                Client.Closed -= onClosed;
            }
            return _closing ??= CloseAsync();
        }

        private async Task CloseAsync()
        {
            // The socket goes first, and before anything is awaited: that frees the buds' control
            // channel at once (so the next start can open it) and ends the read the client is waiting on
            Socket.Dispose();
            try
            {
                if (Client is not null)
                {
                    await Client.DisposeAsync().AsTask().WaitAsync(CloseTimeout).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is TimeoutException or ObjectDisposedException or IOException)
            {
                // Close anyway
            }
            finally
            {
                Device.Dispose();
            }
        }
    }
}
