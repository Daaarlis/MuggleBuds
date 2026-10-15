namespace MuggleBuds.Presentation.Headsets;

/// <summary>
/// The control link to one pair of buds. Settings a pair doesn't have fail with
/// <see cref="NotSupportedException"/>.
/// </summary>
/// <remarks>
/// Every command completes once the buds acknowledge it, and <see cref="State"/> only changes
/// after that, so callers can revert their UI to <see cref="State"/> when a command fails.
/// Events are raised on a background thread.
/// </remarks>
public interface IHeadset : IDisposable
{
    HeadsetSnapshot State { get; }

    event EventHandler<HeadsetSnapshot>? StateChanged;

    event EventHandler? Disconnected;

    Task ConnectAsync(string bluetoothAddress);

    void Disconnect();

    Task SetNoiseModeAsync(NoiseMode mode);

    Task SetConversationDetectionAsync(bool enabled);

    Task SetMultipointAsync(bool enabled);

    Task SetEqualizerAsync(EqualizerBands bands);

    Task SetTouchControlsAsync(bool enabled);

    Task SetHeadGesturesAsync(bool enabled);

    Task SetHoldActionAsync(EarbudSide side, HoldAction action);

    Task SetNoiseModeCycleAsync(NoiseModeCycle cycle);

    Task SetAncStrengthAsync(AncStrength strength);

    Task SetInEarDetectionAsync(bool enabled);

    Task SetLowLatencyAsync(bool enabled);

    Task SetEnhancedBassAsync(bool enabled);

    Task SetListeningModeAsync(ListeningMode mode);
}
