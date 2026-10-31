using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace TavernVoice
{
    internal enum VoicePath { Joining, Relay, Fallback }

    internal sealed class RelayClient : IDisposable
    {
        private const double JoinTimeout = 3.0;
        private const double SilenceTimeout = 3.0;
        private const double UnknownPatience = 5.0;
        private static readonly double[] Probes = { 1, 2, 4, 8, 16 };
        private readonly object sync = new object();
        private readonly Socket socket;
        private readonly IPEndPoint relay;
        private readonly byte[] hello;
        private readonly byte[] bye;
        private readonly uint tag;
        private readonly bool inGameOnly;
        private readonly Action<VoicePacket> deliver;
        private readonly Thread thread;
        private readonly byte[] sendBuffer = new byte[Wire.VoiceHeader + Wire.MaximumPayload];
        private readonly System.Random jitter = new System.Random();
        private volatile bool running = true;
        private VoicePath path;
        private double pathSince;
        private double lastHeard;
        private double lastBound;
        private double unknownSince;
        private bool unknownSeen;
        private bool rebound;
        private double nextHello;
        private int probeStep;
        private long sent;
        private long received;

        internal RelayClient(IPEndPoint relay, byte[] token, uint tag, bool inGameOnly, Action<VoicePacket> deliver)
        {
            this.relay = relay;
            this.tag = tag;
            this.inGameOnly = inGameOnly;
            this.deliver = deliver;
            hello = Wire.HelloPacket(token);
            bye = Wire.ByePacket(tag);
            pathSince = Clock.Now;
            path = inGameOnly ? VoicePath.Fallback : VoicePath.Joining;
            socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            RelayHost.DisableConnectionReset(socket);
            socket.Bind(new IPEndPoint(IPAddress.Any, 0));
            socket.ReceiveTimeout = 200;
            thread = new Thread(Run) { IsBackground = true, Name = "TavernVoice relay client" };
            thread.Start();
        }

        internal VoicePath Path { get { lock (sync) return path; } }
        internal long Sent => Interlocked.Read(ref sent);
        internal long Received => Interlocked.Read(ref received);
        internal IPEndPoint EndPoint => relay;

        private void Run()
        {
            var buffer = new byte[2048];
            while (running)
            {
                double now = Clock.Now;
                bool sayHello;
                lock (sync)
                {
                    sayHello = !inGameOnly && now >= nextHello;
                    if (sayHello)
                        nextHello = now + NextHelloDelay();
                }
                if (sayHello)
                    SendRaw(hello, hello.Length);
                EndPoint from = new IPEndPoint(IPAddress.Any, 0);
                int count;
                try
                {
                    count = socket.ReceiveFrom(buffer, ref from);
                }
                catch (SocketException e)
                {
                    if (e.SocketErrorCode != SocketError.TimedOut)
                        Thread.Sleep(10);
                    continue;
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
                var source = (IPEndPoint)from;
                if (count < 2 || buffer[0] != Wire.Version || source.Port != relay.Port || !source.Address.Equals(relay.Address))
                    continue;
                now = Clock.Now;
                switch (buffer[1])
                {
                    case Wire.Bound:
                        lock (sync)
                        {
                            lastHeard = lastBound = now;
                            unknownSince = 0;
                            if (unknownSeen)
                            {
                                unknownSeen = false;
                                rebound = true;
                            }
                        }
                        break;
                    case Wire.Unknown:
                        lock (sync)
                        {
                            lastHeard = now;
                            if (unknownSince == 0)
                                unknownSince = now;
                            unknownSeen = true;
                            if (path != VoicePath.Fallback)
                                nextHello = Math.Min(nextHello, now + 0.25);
                        }
                        break;
                    case Wire.VoiceDown:
                        if (!Wire.ReadVoice(buffer, 0, count, out VoicePacket packet))
                            break;
                        lock (sync)
                            lastHeard = now;
                        Interlocked.Increment(ref received);
                        deliver(packet);
                        break;
                }
            }
        }

        private double NextHelloDelay()
        {
            if (path != VoicePath.Fallback)
                return 1.0;
            double delay = probeStep < Probes.Length ? Probes[probeStep] : 30.0;
            probeStep++;
            return delay * (0.8 + 0.4 * jitter.NextDouble());
        }

        internal bool Tick(double now, out bool wantsSession, out bool newSpurt)
        {
            wantsSession = false;
            lock (sync)
            {
                newSpurt = rebound;
                rebound = false;
                VoicePath before = path;
                switch (path)
                {
                    case VoicePath.Joining:
                        if (lastBound > 0)
                            path = VoicePath.Relay;
                        else if (now - pathSince > JoinTimeout)
                            path = VoicePath.Fallback;
                        break;
                    case VoicePath.Relay:
                        if (now - lastHeard > SilenceTimeout)
                            path = VoicePath.Fallback;
                        break;
                    case VoicePath.Fallback:
                        if (!inGameOnly && lastBound > pathSince)
                            path = VoicePath.Relay;
                        break;
                }
                if (unknownSince > 0 && now - unknownSince > UnknownPatience && lastBound < unknownSince)
                {
                    wantsSession = true;
                    unknownSince = now;
                }
                if (path == before)
                    return false;
                pathSince = now;
                if (path == VoicePath.Fallback)
                {
                    probeStep = 0;
                    nextHello = now + NextHelloDelay();
                }
                else
                    nextHello = now;
                return true;
            }
        }

        internal void SendVoice(VoicePacket packet)
        {
            int length = Wire.WriteVoice(sendBuffer, Wire.VoiceUp, tag, packet);
            if (SendRaw(sendBuffer, length))
                Interlocked.Increment(ref sent);
        }

        private bool SendRaw(byte[] data, int length)
        {
            try
            {
                socket.SendTo(data, 0, length, SocketFlags.None, relay);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        public void Dispose()
        {
            if (!running)
                return;
            running = false;
            if (!inGameOnly)
                SendRaw(bye, bye.Length);
            try
            {
                socket.Close();
            }
            catch (Exception)
            {
            }
            thread.Join(500);
        }
    }
}
