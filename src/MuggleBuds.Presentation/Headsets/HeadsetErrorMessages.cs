using System.Runtime.InteropServices;
using MuggleBuds.Presentation.Maestro;

namespace MuggleBuds.Presentation.Headsets;

/// <summary>
/// Turns control link failures into messages for people.
/// </summary>
public static class HeadsetErrorMessages
{
    // WSAEADDRINUSE, which Windows gives when something else has the buds' control channel open
    private const int AddressInUse = unchecked((int)0x80072740);

    public static string Describe(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        return exception switch
        {
            TimeoutException => "Your buds didn't respond. Try again.",
            MaestroException => "Your buds didn't accept that setting.",
            COMException { HResult: AddressInUse } => "Another app is using your buds' control connection. Close it and try again.",
            IOException => "Your buds disconnected.",
            _ => "Something went wrong talking to your buds.",
        };
    }
}
