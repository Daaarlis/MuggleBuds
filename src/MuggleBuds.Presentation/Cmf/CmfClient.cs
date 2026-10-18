using Microsoft.Extensions.Logging;
using MuggleBuds.Presentation.Logging;

namespace MuggleBuds.Presentation.Cmf;

/// <summary>
/// Talks the Nothing/CMF control protocol over the RFCOMM byte stream.
/// </summary>
/// <remarks>
/// Reads are requests the buds answer with a packet whose command is the request's with the top
/// bit cleared (0xC007 is answered by 0x4007); <see cref="RequestAsync"/> waits for that
/// packet. Sets get no answer this client knows of, so <see cref="SendAsync"/> only writes.
/// Every packet received, replies and unsolicited pushes alike, is raised through
/// <see cref="PacketReceived"/> on the read thread before a waiting request completes, so
/// state derived from it is already up to date when the request returns.
/// </remarks>
internal sealed class CmfClient : IAsyncDisposable
{
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(3);

    private readonly Stream _input;
    private readonly Stream _output;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly SemaphoreSlim _requestLock = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Task _readLoop;

    private Pending? _pending;
    private int _operation;

    /// <param name="input">Bytes from the buds.</param>
    /// <param name="output">Bytes to the buds.</param>
    /// <param name="logger">Gets every packet at Debug level.</param>
    public CmfClient(Stream input, Stream output, ILogger logger)
    {
        _input    = input;
        _output   = output;
        _logger   = logger;
        _readLoop = Task.Run(ReadLoopAsync);
    }

    /// <summary>
    /// Raised for every packet received, on the read thread.
    /// </summary>
    public event EventHandler<CmfPacket>? PacketReceived;

    /// <summary>
    /// Raised once when the byte stream ends or fails.
    /// </summary>
    public event EventHandler? Closed;

    /// <summary>
    /// The command a request is answered with.
    /// </summary>
    public static ushort ResponseTo(ushort request) => (ushort)(request & 0x7FFF);

    /// <summary>
    /// Writes one packet without waiting for anything back.
    /// </summary>
    public async Task SendAsync(ushort command, byte[] payload, CancellationToken cancellationToken = default)
    {
        try
        {
            await SendCoreAsync(command, payload, cancellationToken).ConfigureAwait(false);
        }
        catch (ObjectDisposedException ex)
        {
            throw new IOException("The control link is closed.", ex);
        }
    }

    /// <summary>
    /// Sends a read command and returns the buds' answer. One request runs at a time.
    /// </summary>
    /// <exception cref="TimeoutException">No answer within <see cref="RequestTimeout"/>.</exception>
    /// <exception cref="IOException">The link closed.</exception>
    public async Task<CmfPacket> RequestAsync(ushort command, byte[] payload, CancellationToken cancellationToken = default)
    {
        try
        {
            return await RequestCoreAsync(command, payload, cancellationToken).ConfigureAwait(false);
        }
        catch (ObjectDisposedException ex)
        {
            throw new IOException("The control link is closed.", ex);
        }
    }

    private async Task SendCoreAsync(ushort command, byte[] payload, CancellationToken cancellationToken)
    {
        var operation = (byte)Interlocked.Increment(ref _operation);
        var bytes = new CmfPacket(command, operation, payload).Encode();
        if (_logger.IsEnabled(LogLevel.Debug))
        {
            var sent = $"command=0x{command:x4} operation={operation} payload={Convert.ToHexStringLower(payload)}";
            var written = Convert.ToHexStringLower(bytes);
            LogMessages.PacketSent(_logger, sent);
            LogMessages.BytesWritten(_logger, written);
        }

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _output.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            await _output.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ReleaseQuietly(_writeLock);
        }
    }

    private async Task<CmfPacket> RequestCoreAsync(ushort command, byte[] payload, CancellationToken cancellationToken)
    {
        await _requestLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var pending = new Pending(ResponseTo(command));
            Volatile.Write(ref _pending, pending);
            try
            {
                await SendAsync(command, payload, cancellationToken).ConfigureAwait(false);
                return await pending.Completion.Task.WaitAsync(RequestTimeout, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                Interlocked.CompareExchange(ref _pending, null, pending);
            }
        }
        finally
        {
            ReleaseQuietly(_requestLock);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _lifetime.CancelAsync().ConfigureAwait(false);
        DisposeQuietly(_input);
        DisposeQuietly(_output);
        try
        {
            await _readLoop.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Already reported through Closed
        }
        _lifetime.Dispose();
        _writeLock.Dispose();
        _requestLock.Dispose();
    }

    private async Task ReadLoopAsync()
    {
        var reader = new CmfPacket.Reader();
        var buffer = new byte[1024];
        Exception? error = null;
        try
        {
            while (true)
            {
                var count = await _input.ReadAsync(buffer, _lifetime.Token).ConfigureAwait(false);
                if (count == 0)
                {
                    break;
                }
                if (_logger.IsEnabled(LogLevel.Debug))
                {
                    var bytes = Convert.ToHexStringLower(buffer.AsSpan(0, count));
                    LogMessages.BytesRead(_logger, bytes);
                }

                var droppedBefore = reader.DroppedBytes;
                foreach (var packet in reader.Push(buffer.AsSpan(0, count)))
                {
                    Dispatch(packet);
                }
                if (reader.DroppedBytes != droppedBefore)
                {
                    LogMessages.CmfBytesDropped(_logger, reader.DroppedBytes - droppedBefore);
                }
            }
        }
        catch (Exception ex)
        {
            error = ex;
        }

        // Fail the request still waiting, then report the close once
        Volatile.Read(ref _pending)?.Completion.TrySetException(new IOException("The buds closed the control link.", error));
        if (!_lifetime.IsCancellationRequested)
        {
            Closed?.Invoke(this, EventArgs.Empty);
        }
    }

    private void Dispatch(CmfPacket packet)
    {
        if (_logger.IsEnabled(LogLevel.Debug))
        {
            var received = $"command=0x{packet.Command:x4} operation={packet.Operation} payload={Convert.ToHexStringLower(packet.Payload)}";
            LogMessages.PacketReceived(_logger, received);
        }

        // One unreadable message mustn't end the read loop (and with it the link)
        try
        {
            PacketReceived?.Invoke(this, packet);
        }
        catch (Exception ex)
        {
            LogMessages.StreamMessageFailed(_logger, ex, $"0x{packet.Command:x4}");
        }

        var pending = Volatile.Read(ref _pending);
        if (pending is not null && pending.Response == packet.Command)
        {
            pending.Completion.TrySetResult(packet);
        }
    }

    // The streams may already be closed: the socket under them is closed first when a link ends
    private static void DisposeQuietly(Stream stream)
    {
        try
        {
            stream.Dispose();
        }
        catch (Exception ex) when (ex is ObjectDisposedException or IOException or InvalidOperationException)
        {
            // Already closed with the socket
        }
    }

    // The client can be disposed while a call is still running; the call then fails with an IOException
    private static void ReleaseQuietly(SemaphoreSlim gate)
    {
        try
        {
            gate.Release();
        }
        catch (ObjectDisposedException)
        {
            // Disposed with the client
        }
    }

    private sealed class Pending(ushort response)
    {
        public ushort Response { get; } = response;

        public TaskCompletionSource<CmfPacket> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
