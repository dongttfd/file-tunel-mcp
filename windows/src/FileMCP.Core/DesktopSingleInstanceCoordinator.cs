using System.IO.Pipes;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace FileMCP.Core;

public sealed class DesktopSingleInstanceCoordinator : IDisposable
{
    private static readonly byte[] ActivationCommand = Encoding.UTF8.GetBytes("activate\n");
    private static readonly TimeSpan DefaultConnectTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(2);
    private const int MaxCommandBytes = 64;

    private readonly string _pipeName;
    private readonly CancellationTokenSource _shutdown = new();
    private NamedPipeServerStream? _server;
    private Task? _listenerTask;
    private int _disposed;

    public DesktopSingleInstanceCoordinator(string instanceKey)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Desktop single-instance coordination requires Windows.");
        if (string.IsNullOrWhiteSpace(instanceKey))
            throw new ArgumentException("Instance key cannot be empty.", nameof(instanceKey));

        _pipeName = BuildPipeName(instanceKey.Trim());

        try
        {
            _server = new NamedPipeServerStream(
                _pipeName,
                PipeDirection.In,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance | PipeOptions.CurrentUserOnly,
                MaxCommandBytes,
                MaxCommandBytes);
            IsPrimary = true;
        }
        catch (IOException)
        {
            IsPrimary = false;
        }
    }

    public bool IsPrimary { get; }

    internal string PipeName => _pipeName;

    public void StartActivationListener(Action onActivate)
    {
        ArgumentNullException.ThrowIfNull(onActivate);
        ThrowIfDisposed();
        if (!IsPrimary || _server is null)
            throw new InvalidOperationException("Only the primary FileMCP desktop instance can listen for activation.");
        if (_listenerTask is not null)
            throw new InvalidOperationException("The activation listener is already running.");

        _listenerTask = Task.Run(() => ListenLoopAsync(onActivate, _shutdown.Token));
    }

    public Task<bool> SignalPrimaryAsync(
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default) =>
        SendCommandAsync("activate\n", timeout ?? DefaultConnectTimeout, cancellationToken);

    internal Task<bool> SendCommandForTestAsync(
        string command,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default) =>
        SendCommandAsync(command, timeout ?? DefaultConnectTimeout, cancellationToken);

    private async Task<bool> SendCommandAsync(
        string command,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        if (IsPrimary) return false;
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromSeconds(30))
            throw new ArgumentOutOfRangeException(nameof(timeout));

        var payload = Encoding.UTF8.GetBytes(command ?? string.Empty);
        using var client = new NamedPipeClientStream(
            ".",
            _pipeName,
            PipeDirection.Out,
            PipeOptions.Asynchronous);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);
        try
        {
            await client.ConnectAsync(timeoutCts.Token).ConfigureAwait(false);
            await client.WriteAsync(payload, timeoutCts.Token).ConfigureAwait(false);
            await client.FlushAsync(timeoutCts.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (TimeoutException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private async Task ListenLoopAsync(Action onActivate, CancellationToken cancellationToken)
    {
        var server = _server;
        if (server is null) return;

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await server.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);

                using var readCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                readCts.CancelAfter(ReadTimeout);

                var buffer = new byte[MaxCommandBytes + 1];
                var total = 0;
                var newlineIndex = -1;
                while (total < buffer.Length)
                {
                    var read = await server.ReadAsync(
                        buffer.AsMemory(total, buffer.Length - total),
                        readCts.Token).ConfigureAwait(false);
                    if (read == 0) break;

                    var previousTotal = total;
                    total += read;
                    var relativeNewline = Array.IndexOf(buffer, (byte)'\n', previousTotal, read);
                    if (relativeNewline >= 0)
                    {
                        newlineIndex = relativeNewline;
                        break;
                    }
                }

                if (newlineIndex >= 0 &&
                    newlineIndex < MaxCommandBytes &&
                    newlineIndex == total - 1)
                {
                    var command = Encoding.UTF8.GetString(buffer, 0, newlineIndex);
                    if (string.Equals(command, "activate", StringComparison.Ordinal))
                    {
                        try { onActivate(); } catch { }
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (OperationCanceledException)
            {
                // Per-connection read timeout. Disconnect and accept the next activation.
            }
            catch (IOException) when (!cancellationToken.IsCancellationRequested)
            {
                // A client can disconnect mid-command. Keep the primary listener alive.
            }
            catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            finally
            {
                if (server.IsConnected)
                {
                    try { server.Disconnect(); } catch { }
                }
            }
        }
    }

    private static string BuildPipeName(string instanceKey)
    {
        var sid = WindowsIdentity.GetCurrent().User?.Value;
        if (string.IsNullOrWhiteSpace(sid)) sid = Environment.UserName;

        var bytes = Encoding.UTF8.GetBytes(instanceKey + "\n" + sid);
        var digest = SHA256.HashData(bytes);
        return "FileMCP.Desktop." + Convert.ToHexString(digest.AsSpan(0, 12)).ToLowerInvariant();
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        try { _shutdown.Cancel(); } catch { }
        try { _server?.Dispose(); } catch { }
        _server = null;

        try { _listenerTask?.Wait(TimeSpan.FromSeconds(1)); } catch { }
        _shutdown.Dispose();
    }
}
