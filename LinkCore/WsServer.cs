using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace NOPlannerLink.Core
{
    /// <summary>
    /// Minimal RFC 6455 WebSocket server bound to 127.0.0.1 only (no external DLL in the game).
    /// The game thread calls <see cref="Broadcast"/> (never blocks); a sender thread writes to sockets.
    /// Text messages from clients land in <see cref="Incoming"/>; new connections raise <see cref="ClientJoined"/>.
    /// </summary>
    public sealed class WsServer : IDisposable
    {
        private const string Guid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";
        private const int MaxQueued = 512;
        private const int MaxClients = 8;

        private readonly int port;
        private readonly Func<string?, bool> originAllowed;
        private readonly Action<string> log;
        private TcpListener? listener;
        private Thread? acceptThread, sendThread;
        private volatile bool running;
        private readonly List<Client> clients = new List<Client>();
        private readonly ConcurrentQueue<string> outbox = new ConcurrentQueue<string>();
        private readonly AutoResetEvent outboxSignal = new AutoResetEvent(false);

        /// <summary>Text messages received from any client (drained by the game thread).</summary>
        public readonly ConcurrentQueue<string> Incoming = new ConcurrentQueue<string>();
        /// <summary>Set when a client connects; the game thread clears it and sends hello + snapshot.</summary>
        public volatile bool ClientJoined;

        public int Port => port;
        public int ClientCount { get { lock (clients) return clients.Count; } }

        public WsServer(int port, Func<string?, bool> originAllowed, Action<string> log)
        {
            this.port = port;
            this.originAllowed = originAllowed;
            this.log = log;
        }

        public void Start()
        {
            listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            running = true;
            acceptThread = new Thread(AcceptLoop) { IsBackground = true, Name = "NOPlannerLink-accept" };
            sendThread = new Thread(SendLoop) { IsBackground = true, Name = "NOPlannerLink-send" };
            acceptThread.Start();
            sendThread.Start();
        }

        /// <summary>Queue a text message for every connected client. Safe from any thread.</summary>
        public void Broadcast(string text)
        {
            if (ClientCount == 0) return;
            if (outbox.Count > MaxQueued)
            {
                // A stalled client must not grow memory: drop the backlog, clients resync on next snapshot.
                while (outbox.TryDequeue(out _)) { }
                ClientJoined = true;
            }
            outbox.Enqueue(text);
            outboxSignal.Set();
        }

        private void AcceptLoop()
        {
            while (running)
            {
                TcpClient tcp;
                try { tcp = listener!.AcceptTcpClient(); }
                catch { if (!running) return; continue; }
                var t = new Thread(() => Serve(tcp)) { IsBackground = true, Name = "NOPlannerLink-client" };
                t.Start();
            }
        }

        private void Serve(TcpClient tcp)
        {
            Client? c = null;
            try
            {
                tcp.NoDelay = true;
                var stream = tcp.GetStream();
                var headers = ReadHeaders(stream);
                if (headers == null) { tcp.Close(); return; }
                headers.TryGetValue("origin", out var origin);
                if (!headers.TryGetValue("sec-websocket-key", out var key) || !originAllowed(origin))
                {
                    WriteAscii(stream, "HTTP/1.1 403 Forbidden\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
                    log("Refused connection from origin " + (origin ?? "(none)"));
                    tcp.Close();
                    return;
                }
                lock (clients)
                {
                    if (clients.Count >= MaxClients)
                    {
                        WriteAscii(stream, "HTTP/1.1 503 Service Unavailable\r\nContent-Length: 0\r\n\r\n");
                        tcp.Close();
                        return;
                    }
                }
                string accept;
                using (var sha = SHA1.Create())
                    accept = Convert.ToBase64String(sha.ComputeHash(Encoding.ASCII.GetBytes(key.Trim() + Guid)));
                WriteAscii(stream,
                    "HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n" +
                    "Sec-WebSocket-Accept: " + accept + "\r\n\r\n");

                c = new Client(tcp, stream);
                lock (clients) clients.Add(c);
                ClientJoined = true;
                log("Planner connected (" + (origin ?? "no origin") + ")");
                ReadLoop(c);
            }
            catch (Exception) { /* client went away */ }
            finally
            {
                if (c != null) Drop(c);
                else try { tcp.Close(); } catch { }
            }
        }

        private void ReadLoop(Client c)
        {
            var s = c.Stream;
            var message = new MemoryStream();
            while (running)
            {
                int b0 = s.ReadByte(), b1 = s.ReadByte();
                if (b0 < 0 || b1 < 0) return;
                bool fin = (b0 & 0x80) != 0;
                int opcode = b0 & 0x0F;
                bool masked = (b1 & 0x80) != 0;
                long len = b1 & 0x7F;
                if (len == 126)
                {
                    var l = ReadExact(s, 2);
                    len = (l[0] << 8) | l[1];
                }
                else if (len == 127) return; // commands are tiny; refuse huge frames
                if (len > 65536) return;
                var mask = masked ? ReadExact(s, 4) : null;
                var payload = ReadExact(s, (int)len);
                if (mask != null) for (int i = 0; i < payload.Length; i++) payload[i] ^= mask[i % 4];

                switch (opcode)
                {
                    case 0x8: // close
                        c.Send(0x8, Array.Empty<byte>());
                        return;
                    case 0x9: // ping
                        c.Send(0xA, payload);
                        break;
                    case 0xA: // pong
                        break;
                    case 0x0: // continuation
                    case 0x1: // text
                        message.Write(payload, 0, payload.Length);
                        if (message.Length > 65536) return;
                        if (fin)
                        {
                            if (Incoming.Count < 64) Incoming.Enqueue(Encoding.UTF8.GetString(message.ToArray()));
                            message.SetLength(0);
                        }
                        break;
                    default:
                        return; // binary or unknown: not part of the protocol
                }
            }
        }

        private void SendLoop()
        {
            while (running)
            {
                outboxSignal.WaitOne(500);
                while (outbox.TryDequeue(out var text))
                {
                    var bytes = Encoding.UTF8.GetBytes(text);
                    Client[] snapshot;
                    lock (clients) snapshot = clients.ToArray();
                    foreach (var c in snapshot)
                    {
                        try { c.Send(0x1, bytes); }
                        catch { Drop(c); }
                    }
                }
            }
        }

        private void Drop(Client c)
        {
            bool removed;
            lock (clients) removed = clients.Remove(c);
            try { c.Tcp.Close(); } catch { }
            if (removed) log("Planner disconnected");
        }

        public void Dispose()
        {
            running = false;
            try { listener?.Stop(); } catch { }
            Client[] all;
            lock (clients) { all = clients.ToArray(); clients.Clear(); }
            foreach (var c in all) try { c.Tcp.Close(); } catch { }
            outboxSignal.Set();
        }

        // ---- helpers ----

        private static Dictionary<string, string>? ReadHeaders(NetworkStream s)
        {
            var sb = new StringBuilder();
            s.ReadTimeout = 5000;
            while (sb.Length < 8192)
            {
                int b = s.ReadByte();
                if (b < 0) return null;
                sb.Append((char)b);
                if (sb.Length >= 4 && sb[sb.Length - 1] == '\n' && sb[sb.Length - 2] == '\r' && sb[sb.Length - 3] == '\n' && sb[sb.Length - 4] == '\r') break;
            }
            s.ReadTimeout = Timeout.Infinite;
            var lines = sb.ToString().Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length == 0 || !lines[0].StartsWith("GET ", StringComparison.Ordinal)) return null;
            var h = new Dictionary<string, string>();
            for (int i = 1; i < lines.Length; i++)
            {
                var idx = lines[i].IndexOf(':');
                if (idx > 0) h[lines[i].Substring(0, idx).Trim().ToLowerInvariant()] = lines[i].Substring(idx + 1).Trim();
            }
            return h;
        }

        internal static byte[] ReadExact(Stream s, int n)
        {
            var buf = new byte[n];
            int off = 0;
            while (off < n)
            {
                int r = s.Read(buf, off, n - off);
                if (r <= 0) throw new EndOfStreamException();
                off += r;
            }
            return buf;
        }

        private static void WriteAscii(Stream s, string text)
        {
            var b = Encoding.ASCII.GetBytes(text);
            s.Write(b, 0, b.Length);
        }

        /// <summary>Builds an unmasked server frame (exposed for tests).</summary>
        public static byte[] Frame(int opcode, byte[] payload)
        {
            int headerLen = payload.Length < 126 ? 2 : payload.Length <= 0xFFFF ? 4 : 10;
            var f = new byte[headerLen + payload.Length];
            f[0] = (byte)(0x80 | opcode);
            if (payload.Length < 126) f[1] = (byte)payload.Length;
            else if (payload.Length <= 0xFFFF)
            {
                f[1] = 126;
                f[2] = (byte)(payload.Length >> 8);
                f[3] = (byte)payload.Length;
            }
            else
            {
                f[1] = 127;
                ulong len = (ulong)payload.Length;
                for (int i = 0; i < 8; i++) f[2 + i] = (byte)(len >> (56 - 8 * i));
            }
            Buffer.BlockCopy(payload, 0, f, headerLen, payload.Length);
            return f;
        }

        private sealed class Client
        {
            public readonly TcpClient Tcp;
            public readonly NetworkStream Stream;
            private readonly object writeLock = new object();
            public Client(TcpClient tcp, NetworkStream stream) { Tcp = tcp; Stream = stream; }
            public void Send(int opcode, byte[] payload)
            {
                var f = Frame(opcode, payload);
                lock (writeLock) Stream.Write(f, 0, f.Length);
            }
        }
    }
}
