using System.Security;
using Microsoft.Win32;

namespace MuggleBuds.App;

/// <summary>
/// Moves this app's notification-area icon between the taskbar's visible tray area ("pinned")
/// and the overflow flyout behind the ^ arrow.
/// </summary>
/// <remarks>
/// Windows has no API for it. Windows 11 keeps one key per icon under
/// <c>HKCU\Control Panel\NotifyIconSettings</c>, with the owning exe in <c>ExecutablePath</c>,
/// and <c>IsPromoted</c> (1 = shown in the taskbar, 0 = in the overflow) is what the Settings
/// page "Other system tray icons" switches. The key only exists once the icon has been added,
/// so a request retries for a while. Does nothing where the key doesn't exist (Windows 10).
/// </remarks>
internal static class TrayIconPinning
{
    private const string SettingsKeyPath = @"Control Panel\NotifyIconSettings";
    private const string ExecutablePathValue = "ExecutablePath";
    private const string PromotedValue = "IsPromoted";
    private const int Attempts = 10;

    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(1);
    private static readonly SemaphoreSlim Gate = new(1, 1);

    private static int _latest;

    /// <summary>
    /// Pins or unpins the icon. A newer call replaces one still waiting for the icon's key.
    /// </summary>
    public static async Task SetPromotedAsync(bool promoted)
    {
        var request = Interlocked.Increment(ref _latest);
        await Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            for (var attempt = 0; attempt < Attempts; attempt++)
            {
                if (request != Volatile.Read(ref _latest) || TrySet(promoted))
                {
                    return;
                }
                await Task.Delay(RetryDelay).ConfigureAwait(false);
            }
        }
        finally
        {
            Gate.Release();
        }
    }

    // True when this app's key was found (whether or not it needed changing)
    private static bool TrySet(bool promoted)
    {
        if (Environment.ProcessPath is not { } executable)
        {
            return false;
        }

        try
        {
            using var root = Registry.CurrentUser.OpenSubKey(SettingsKeyPath);
            if (root is null)
            {
                return false;
            }

            var found = false;
            var wanted = promoted ? 1 : 0;
            foreach (var name in root.GetSubKeyNames())
            {
                using var key = root.OpenSubKey(name, writable: true);
                if (key is null || key.GetValue(ExecutablePathValue) is not string registered || !IsThisApp(registered, executable))
                {
                    continue;
                }

                found = true;
                if (key.GetValue(PromotedValue) is not int current || current != wanted)
                {
                    key.SetValue(PromotedValue, wanted, RegistryValueKind.DWord);
                }
            }
            return found;
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    // Windows stores the path as is, or with a known folder's GUID in front for places like
    // Program Files: "{GUID}\Dir\App.exe"
    private static bool IsThisApp(string registered, string executable)
    {
        if (string.Equals(registered, executable, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (registered.StartsWith('{'))
        {
            var end = registered.IndexOf('}');
            if (end > 0 && end + 1 < registered.Length && registered[end + 1] == '\\')
            {
                return executable.EndsWith(registered[(end + 1)..], StringComparison.OrdinalIgnoreCase);
            }
        }
        return false;
    }
}
