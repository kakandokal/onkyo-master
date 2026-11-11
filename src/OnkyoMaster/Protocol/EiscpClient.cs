using System.IO;
using System.Net;
using System.Net.Sockets;

namespace OnkyoMaster.Protocol;

/// <summary>
/// Keeps a TCP connection to the receiver alive, reconnecting as needed.
/// Events are raised on thread-pool threads.
/// </summary>
public sealed class EiscpClient : IDisposable
{
    /// <summary>The disconnect detail reported when the receiver itself closed the connection.</summary>
    public const string ClosedByReceiver = "соединение закрыто ресивером";

    private const int UrgentGapMs = 60;
    private const int ReplyTimeoutMs = 900;
    private const int StableSessionMs = 30_000;

    private sealed record Pending(string Command, bool AwaitReply, string? CoalesceKey);

    private readonly object _gate = new();
    private readonly LinkedList<Pending> _urgent = new();
    private readonly LinkedList<Pending> _background = new();
    private readonly SemaphoreSlim _signal = new(0);
    private CancellationTokenSource? _cts;
    private Socket? _socket;
    private string? _awaitedPrefix;
    private long _awaitDeadline;

    public event Action<string>? MessageReceived;
    public event Action<string>? MessageSent;

    /// <summary>Connected flag plus a detail: the bound local address on success, the error text on failure.</summary>
    public event Action<bool, string?>? ConnectionChanged;

    /// <summary>Decides, per connection attempt, which local address to bind to for a given receiver address.</summary>
    public Func<IPAddress, BindChoice> BindResolver { get; set; } = _ => BindChoice.Unbound;

    public void Start(IPAddress address)
    {
        Stop();
        var cts = new CancellationTokenSource();
        _cts = cts;
        _ = Task.Run(() => RunAsync(address, cts.Token));
    }

    public void Stop()
    {
        _cts?.Cancel();
        _cts = null;
        DropConnection();
    }

    /// <summary>Closes the current socket; the connection loop then reconnects.</summary>
    public void DropConnection()
    {
        Socket? socket;
        lock (_gate) socket = _socket;
        try { socket?.Close(); } catch (SocketException) { }
    }

    /// <summary>
    /// Queues a user command. Commands with the same <paramref name="coalesceKey"/> replace each other
    /// while still queued, so dragging a slider only ever sends the latest value.
    /// </summary>
    public bool Send(string command, string? coalesceKey = null) => Enqueue(new Pending(command, false, coalesceKey), _urgent);

    /// <summary>Queues a status query. Queries go out one at a time, each waiting for its answer.</summary>
    public bool Query(string command) => Enqueue(new Pending(command, true, command), _background);

    /// <summary>Writes a command straight to the socket, bypassing the queue. For "the PC is about to sleep".</summary>
    public bool SendNow(string command)
    {
        Socket? socket;
        lock (_gate) socket = _socket;
        if (socket is null) return false;
        try
        {
            socket.Send(EiscpPacket.Encode(command));
            MessageSent?.Invoke(command);
            return true;
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
        {
            return false;
        }
    }

    public void Dispose() => Stop();

    private bool Enqueue(Pending item, LinkedList<Pending> queue)
    {
        if (item.Command.Length < 3) return false;
        lock (_gate)
        {
            if (_socket is null) return false;
            var replaced = false;
            if (item.CoalesceKey is not null)
            {
                for (var node = queue.First; node is not null; node = node.Next)
                {
                    if (node.Value.CoalesceKey != item.CoalesceKey) continue;
                    node.Value = item;
                    replaced = true;
                    break;
                }
            }
            if (!replaced) queue.AddLast(item);
        }
        _signal.Release();
        return true;
    }

    private async Task RunAsync(IPAddress address, CancellationToken ct)
    {
        var delay = 1000;
        while (!ct.IsCancellationRequested)
        {
            var connectedAt = 0L;
            string? error;
            try
            {
                var bind = BindResolver(address);
                if (bind.Available)
                {
                    using var socket = await ConnectAsync(address, bind.Local, ct);
                    lock (_gate)
                    {
                        _socket = socket;
                        _urgent.Clear();
                        _background.Clear();
                        _awaitedPrefix = null;
                    }
                    connectedAt = Environment.TickCount64;
                    ConnectionChanged?.Invoke(true, bind.Local?.ToString());

                    using var session = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    using var stream = new NetworkStream(socket, ownsSocket: false);
                    var reader = ReadLoopAsync(stream, session.Token);
                    var writer = WriteLoopAsync(stream, session.Token);
                    var finished = await Task.WhenAny(reader, writer);
                    session.Cancel();
                    try { await Task.WhenAll(reader, writer); } catch (Exception) { /* reported below */ }
                    error = finished.Exception?.GetBaseException().Message ?? ClosedByReceiver;
                }
                else
                {
                    error = "нет сетевого адаптера для привязки";
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex) when (ex is SocketException or IOException or OperationCanceledException or ObjectDisposedException)
            {
                error = ex is OperationCanceledException ? "нет ответа" : ex.Message;
            }
            finally
            {
                lock (_gate)
                {
                    _socket = null;
                    _urgent.Clear();
                    _background.Clear();
                }
            }

            if (ct.IsCancellationRequested) break;

            // A session that held for a while means the link is healthy, so the next retry is prompt.
            // One that is cut within seconds usually means another program took over: a TX-RZ50 was seen
            // to drop a connection when a second one arrives from the same PC. Reconnecting at once would
            // only cut that program off in turn, so the pause keeps growing instead.
            var wasConnected = connectedAt != 0;
            if (wasConnected && Environment.TickCount64 - connectedAt > StableSessionMs) delay = 1000;
            ConnectionChanged?.Invoke(false, error);
            try { await Task.Delay(delay, ct); } catch (OperationCanceledException) { break; }
            delay = Math.Min(delay * 2, wasConnected ? 30_000 : 8000);
        }
    }

    private static async Task<Socket> ConnectAsync(IPAddress address, IPAddress? local, CancellationToken ct)
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            // Keep-alive notices a receiver that vanished (unplugged, PC woke up on another network).
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
            socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime, 20);
            socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval, 5);
            socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveRetryCount, 3);
            if (local is not null) socket.Bind(new IPEndPoint(local, 0));

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(4000);
            await socket.ConnectAsync(new IPEndPoint(address, EiscpPacket.Port), timeout.Token);
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private async Task ReadLoopAsync(NetworkStream stream, CancellationToken ct)
    {
        var buffer = new byte[32 * 1024];
        var length = 0;
        while (true)
        {
            if (length == buffer.Length)
            {
                if (buffer.Length >= 8 * 1024 * 1024) throw new IOException("слишком большое сообщение");
                Array.Resize(ref buffer, buffer.Length * 2);
            }

            var read = await stream.ReadAsync(buffer.AsMemory(length), ct);
            if (read == 0) return;
            length += read;

            var offset = 0;
            while (EiscpPacket.TryDecode(buffer.AsSpan(offset, length - offset), out var message, out var consumed))
            {
                offset += consumed;
                if (message is not { Length: >= 3 }) continue;
                NoteReply(message);
                MessageReceived?.Invoke(message);
            }

            if (offset > 0)
            {
                Buffer.BlockCopy(buffer, offset, buffer, 0, length - offset);
                length -= offset;
            }
        }
    }

    private void NoteReply(string message)
    {
        lock (_gate)
        {
            if (_awaitedPrefix is null || !message.StartsWith(_awaitedPrefix, StringComparison.Ordinal)) return;
            _awaitedPrefix = null;
        }
        _signal.Release();
    }

    private async Task WriteLoopAsync(NetworkStream stream, CancellationToken ct)
    {
        while (true)
        {
            Pending? item = null;
            var wait = Timeout.Infinite;
            lock (_gate)
            {
                if (_urgent.First is { } urgent)
                {
                    item = urgent.Value;
                    _urgent.RemoveFirst();
                }
                else if (_background.First is { } background)
                {
                    // Hold the next query back until the previous one is answered: in standby the receiver
                    // needs ~0.4 s per query and user commands must not queue up behind them.
                    var remaining = _awaitDeadline - Environment.TickCount64;
                    if (_awaitedPrefix is null || remaining <= 0)
                    {
                        item = background.Value;
                        _background.RemoveFirst();
                    }
                    else
                    {
                        wait = (int)remaining;
                    }
                }
            }

            if (item is null)
            {
                await _signal.WaitAsync(wait, ct);
                continue;
            }

            await stream.WriteAsync(EiscpPacket.Encode(item.Command), ct);
            MessageSent?.Invoke(item.Command);
            if (item.AwaitReply)
            {
                lock (_gate)
                {
                    _awaitedPrefix = item.Command[..3];
                    _awaitDeadline = Environment.TickCount64 + ReplyTimeoutMs;
                }
            }
            else
            {
                await Task.Delay(UrgentGapMs, ct);
            }
        }
    }
}
