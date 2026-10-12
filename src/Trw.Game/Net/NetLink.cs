using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Trw.Game.Net;

/// <summary>
/// One framed TCP connection to another peer (see <see cref="NetWire"/>): a reader thread that hands every message to
/// <see cref="Received"/> (on that thread) and a writer thread fed by <see cref="Send"/>, so a slow socket never blocks the game loop.
/// The stream is either a direct connection or a jsNetwork relay leg after its "OK" line: the relay copies bytes blindly, so both look the same.
/// </summary>
public sealed class NetLink : IDisposable
{
    private readonly TcpClient client;
    private readonly NetworkStream stream;
    private readonly BlockingCollection<byte[]> outbox = new(new ConcurrentQueue<byte[]>());
    private int closed;

    /// <summary>"direct", "lan" or "relay": how the link was made (shown in the lobby).</summary>
    public readonly string Via;
    public readonly string Remote;
    /// <summary>Called on the reader thread for every message body (type byte first).</summary>
    public Action<NetLink, byte[]>? Received;
    /// <summary>Called once, on whichever thread noticed, when the link ends.</summary>
    public Action<NetLink, string>? Closed;
    /// <summary>Wall-clock time of the last message received (keepalive / timeout).</summary>
    public long LastHeardMs = Environment.TickCount64;
    /// <summary>Free for the owner (the host keeps its member here).</summary>
    public object? Tag;
    public bool IsClosed => Volatile.Read(ref closed) != 0;
    public long BytesIn, BytesOut;

    private NetLink(TcpClient c, string via)
    {
        client = c;
        client.NoDelay = true;
        stream = c.GetStream();
        Via = via;
        Remote = c.Client.RemoteEndPoint?.ToString() ?? "?";
    }

    /// <summary>Starts the reader and writer threads (after the owner has set <see cref="Received"/> and <see cref="Closed"/>).</summary>
    public void Start()
    {
        new Thread(ReadLoop) { IsBackground = true, Name = "net read " + Remote }.Start();
        new Thread(WriteLoop) { IsBackground = true, Name = "net write " + Remote }.Start();
    }

    public void Send(byte[] body)
    {
        if (IsClosed) return;
        try { outbox.Add(body); } catch (InvalidOperationException) { }
    }

    private void ReadLoop()
    {
        var head = new byte[2];
        try
        {
            for (;;)
            {
                stream.ReadExactly(head);
                int len = BinaryPrimitives.ReadUInt16LittleEndian(head);
                if (len == 0 || len > NetWire.MaxBody) throw new InvalidDataException($"bad frame length {len}");
                var body = new byte[len];
                stream.ReadExactly(body);
                BytesIn += len + 2;
                LastHeardMs = Environment.TickCount64;
                Received?.Invoke(this, body);
            }
        }
        catch (Exception e) when (e is IOException or SocketException or ObjectDisposedException or EndOfStreamException or InvalidDataException or InvalidOperationException)
        {
            Close(e is EndOfStreamException ? "connection closed" : e.Message);
        }
    }

    private void WriteLoop()
    {
        var head = new byte[2];
        try
        {
            foreach (var body in outbox.GetConsumingEnumerable())
            {
                BinaryPrimitives.WriteUInt16LittleEndian(head, (ushort)body.Length);
                // one write per message: header and body together, so TCP_NODELAY sends one segment
                var frame = new byte[body.Length + 2];
                head.CopyTo(frame, 0); body.CopyTo(frame, 2);
                stream.Write(frame);
                BytesOut += frame.Length;
            }
        }
        catch (Exception e) when (e is IOException or SocketException or ObjectDisposedException or InvalidOperationException)
        {
            Close(e.Message);
        }
    }

    /// <summary>Sends what is queued (best effort, up to <paramref name="flushMs"/>), then closes.</summary>
    public void Close(string reason, int flushMs = 0)
    {
        if (Interlocked.Exchange(ref closed, 1) != 0) return;
        outbox.CompleteAdding();
        if (flushMs > 0)
        {
            var until = Environment.TickCount64 + flushMs;
            while (outbox.Count > 0 && Environment.TickCount64 < until) Thread.Sleep(5);
            Thread.Sleep(20);
        }
        try { client.Client.Shutdown(SocketShutdown.Both); } catch (Exception) { }
        client.Dispose();
        Closed?.Invoke(this, reason);
    }

    public void Dispose() => Close("disposed");

    // ------------------------------------------------------------------------------------------ making links

    public static NetLink FromAccepted(TcpClient c) => new(c, "direct");

    /// <summary>A direct TCP connection; null when it cannot be made within the timeout.</summary>
    public static NetLink? ConnectDirect(string host, int port, int timeoutMs, string via)
    {
        var c = new TcpClient();
        try
        {
            using var cts = new CancellationTokenSource(timeoutMs);
            c.ConnectAsync(host, port, cts.Token).AsTask().GetAwaiter().GetResult();
            return new NetLink(c, via);
        }
        catch (Exception e) when (e is SocketException or OperationCanceledException or IOException)
        {
            c.Dispose();
            return null;
        }
    }

    /// <summary>
    /// One leg of a jsNetwork relay pair: connect, send "JSNRELAY1 &lt;ticket&gt;\n", wait for "OK\n" (the relay answers once the other
    /// player's leg has arrived, up to its pair timeout) or "ER &lt;reason&gt;\n". Returns the link, or null with the reason.
    /// </summary>
    public static NetLink? ConnectRelay(string host, int port, string ticket, int timeoutMs, out string error)
    {
        error = "";
        var c = new TcpClient();
        try
        {
            using (var cts = new CancellationTokenSource(Math.Min(timeoutMs, 8000)))
                c.ConnectAsync(host, port, cts.Token).AsTask().GetAwaiter().GetResult();
            var s = c.GetStream();
            s.Write(Encoding.ASCII.GetBytes("JSNRELAY1 " + ticket + "\n"));
            s.ReadTimeout = timeoutMs;
            var line = new StringBuilder();
            for (int b; line.Length < 256 && (b = s.ReadByte()) >= 0 && b != '\n';) line.Append((char)b);
            s.ReadTimeout = Timeout.Infinite;
            var text = line.ToString().TrimEnd('\r');
            if (text == "OK") return new NetLink(c, "relay");
            error = text.StartsWith("ER ") ? text[3..] : "the relay did not answer";
        }
        catch (Exception e) when (e is SocketException or OperationCanceledException or IOException)
        {
            error = "cannot reach the relay: " + e.Message;
        }
        c.Dispose();
        return null;
    }

    /// <summary>This machine's first IPv4 LAN address (sent to the master so a joiner behind the same router can connect directly).</summary>
    public static string? LanAddress()
    {
        try
        {
            foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up || ni.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback) continue;
                foreach (var a in ni.GetIPProperties().UnicastAddresses)
                    if (a.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a.Address)) return a.Address.ToString();
            }
        }
        catch (Exception) { }
        return null;
    }
}
