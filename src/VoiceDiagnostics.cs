using System.Threading;

namespace TavernVoice
{
    internal static class VoiceDiagnostics
    {
        private static long capturedFrames;
        private static long silenceDropped;
        private static long encodedFrames;
        private static long sentPackets;
        private static long sentBytes;
        private static long receivedPackets;
        private static long receivedBytes;
        private static long relayedPackets;
        private static long relayedBytes;
        private static long rangeDropped;
        private static long invalidDropped;
        private static long fecFrames;
        private static long plcFrames;
        private static long audioUnderruns;
        private static long queueDropped;
        private static long playbackRestarts;
        private static long latePackets;
        private static long jitterWaits;
        private static long latencyDrops;
        private static long lastSentBytes;
        private static long lastReceivedBytes;
        private static long lastRelayedBytes;
        private static long lastSentPackets;
        private static long lastReceivedPackets;
        private static long lastRelayedPackets;
        private static double lastRateTime;

        internal static float SendBytesPerSecond { get; private set; }
        internal static float ReceiveBytesPerSecond { get; private set; }
        internal static float RelayBytesPerSecond { get; private set; }
        internal static float SendPacketsPerSecond { get; private set; }
        internal static float ReceivePacketsPerSecond { get; private set; }
        internal static float RelayPacketsPerSecond { get; private set; }

        internal static long CapturedFrames => Interlocked.Read(ref capturedFrames);
        internal static long SilenceDropped => Interlocked.Read(ref silenceDropped);
        internal static long EncodedFrames => Interlocked.Read(ref encodedFrames);
        internal static long SentPackets => Interlocked.Read(ref sentPackets);
        internal static long SentBytes => Interlocked.Read(ref sentBytes);
        internal static long ReceivedPackets => Interlocked.Read(ref receivedPackets);
        internal static long ReceivedBytes => Interlocked.Read(ref receivedBytes);
        internal static long RelayedPackets => Interlocked.Read(ref relayedPackets);
        internal static long RelayedBytes => Interlocked.Read(ref relayedBytes);
        internal static long RangeDropped => Interlocked.Read(ref rangeDropped);
        internal static long InvalidDropped => Interlocked.Read(ref invalidDropped);
        internal static long FecFrames => Interlocked.Read(ref fecFrames);
        internal static long PlcFrames => Interlocked.Read(ref plcFrames);
        internal static long AudioUnderruns => Interlocked.Read(ref audioUnderruns);
        internal static long QueueDropped => Interlocked.Read(ref queueDropped);
        internal static long PlaybackRestarts => Interlocked.Read(ref playbackRestarts);
        internal static long LatePackets => Interlocked.Read(ref latePackets);
        internal static long JitterWaits => Interlocked.Read(ref jitterWaits);
        internal static long LatencyDrops => Interlocked.Read(ref latencyDrops);

        internal static void Captured(bool encoded)
        {
            Interlocked.Increment(ref capturedFrames);
            if (encoded) Interlocked.Increment(ref encodedFrames);
            else Interlocked.Increment(ref silenceDropped);
        }

        internal static void Sent(int bytes) { Interlocked.Increment(ref sentPackets); Interlocked.Add(ref sentBytes, bytes); }
        internal static void Received(int bytes) { Interlocked.Increment(ref receivedPackets); Interlocked.Add(ref receivedBytes, bytes); }
        internal static void Relayed(int bytes) { Interlocked.Increment(ref relayedPackets); Interlocked.Add(ref relayedBytes, bytes); }
        internal static void RangeDrop() => Interlocked.Increment(ref rangeDropped);
        internal static void InvalidDrop() => Interlocked.Increment(ref invalidDropped);
        internal static void Fec() => Interlocked.Increment(ref fecFrames);
        internal static void Plc() => Interlocked.Increment(ref plcFrames);
        internal static void Underrun() => Interlocked.Increment(ref audioUnderruns);
        internal static void QueueDrop() => Interlocked.Increment(ref queueDropped);
        internal static void PlaybackRestart() => Interlocked.Increment(ref playbackRestarts);
        internal static void LatePacket() => Interlocked.Increment(ref latePackets);
        internal static void JitterWait() => Interlocked.Increment(ref jitterWaits);
        internal static void LatencyDrop() => Interlocked.Increment(ref latencyDrops);

        internal static void UpdateRates()
        {
            double now = Clock.Now;
            if (lastRateTime == 0) { lastRateTime = now; return; }
            double elapsed = now - lastRateTime;
            if (elapsed < 1f) return;
            long sent = SentBytes;
            long received = ReceivedBytes;
            long relayed = RelayedBytes;
            long sentCount = SentPackets;
            long receivedCount = ReceivedPackets;
            long relayedCount = RelayedPackets;
            SendBytesPerSecond = (float)((sent - lastSentBytes) / elapsed);
            ReceiveBytesPerSecond = (float)((received - lastReceivedBytes) / elapsed);
            RelayBytesPerSecond = (float)((relayed - lastRelayedBytes) / elapsed);
            SendPacketsPerSecond = (float)((sentCount - lastSentPackets) / elapsed);
            ReceivePacketsPerSecond = (float)((receivedCount - lastReceivedPackets) / elapsed);
            RelayPacketsPerSecond = (float)((relayedCount - lastRelayedPackets) / elapsed);
            lastSentBytes = sent;
            lastReceivedBytes = received;
            lastRelayedBytes = relayed;
            lastSentPackets = sentCount;
            lastReceivedPackets = receivedCount;
            lastRelayedPackets = relayedCount;
            lastRateTime = now;
        }

        internal static void Reset()
        {
            capturedFrames = silenceDropped = encodedFrames = 0;
            sentPackets = sentBytes = receivedPackets = receivedBytes = 0;
            relayedPackets = relayedBytes = rangeDropped = invalidDropped = 0;
            fecFrames = plcFrames = audioUnderruns = queueDropped = playbackRestarts = 0;
            latePackets = jitterWaits = latencyDrops = 0;
            lastSentBytes = lastReceivedBytes = lastRelayedBytes = 0;
            lastSentPackets = lastReceivedPackets = lastRelayedPackets = 0;
            SendBytesPerSecond = ReceiveBytesPerSecond = RelayBytesPerSecond = 0f;
            SendPacketsPerSecond = ReceivePacketsPerSecond = RelayPacketsPerSecond = 0f;
            lastRateTime = Clock.Now;
        }
    }
}
