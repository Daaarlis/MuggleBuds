namespace MuggleBuds.Presentation.Devices;

/// <summary>
/// Decides whether a Bluetooth device name belongs to buds this app controls: Pixel Buds, or in
/// this CMF port, CMF Buds.
/// </summary>
/// <remarks>
/// Windows names them "CMF Buds Pro 2" (or "Pixel Buds Pro 2"), with the owner's name in front
/// once set up on a phone. Buds renamed later keep working: <see cref="HeadsetManager"/>
/// remembers them by address.
/// </remarks>
public static class PixelBudsNameFilter
{
    public static bool IsPixelBuds(string? name) =>
        !string.IsNullOrWhiteSpace(name)
        && (name.Contains("Pixel Buds", StringComparison.OrdinalIgnoreCase)
            || name.Contains("CMF Buds", StringComparison.OrdinalIgnoreCase));
}
