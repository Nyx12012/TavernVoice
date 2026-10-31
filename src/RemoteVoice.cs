using System;
using System.Collections.Generic;
using Concentus.Structs;
using UnityEngine;

namespace TavernVoice
{
    internal sealed class RemoteVoice : IDisposable
    {
        private const float FrameMilliseconds = 1000f * VoiceEncoder.FrameSamples / VoiceEncoder.SampleRate;
        private const float MinimumTargetMilliseconds = FrameMilliseconds * 2f;
        private const float MaximumTargetMilliseconds = 240f;
        private const float InitialTargetMilliseconds = 60f;
        private const float TrimSlackMilliseconds = 40f;
        private const double TrimHoldSeconds = 1.5;
        private const double TargetGrowCooldownSeconds = 1.0;
        private const double TargetShrinkAfterSeconds = 10.0;
        private const int MaximumStoredPackets = 64;
        private const int MaximumPlcFrames = 3;
        private const int FreshSpurtSequenceWindow = 25;
        private const int SessionRestartSpurtJump = 16;
        private const float SessionRestartGapMilliseconds = 10000f;
        private const double PostRetrySeconds = 5.0;
        private const double PlaybackStallSeconds = 2.0;
        private readonly int userId;
        private readonly object stateSync = new object();
        private readonly bool integrationReceiver = TestMode.Active;
        private readonly OpusDecoder decoder = OpusDecoder.Create(VoiceEncoder.SampleRate, 1);
        private readonly SortedDictionary<ushort, byte[]> packets = new SortedDictionary<ushort, byte[]>();
        private readonly PcmRingBuffer pcm = new PcmRingBuffer(VoiceEncoder.SampleRate);
        private float appliedGain = -1f;
        private readonly short[] decoded = new short[VoiceEncoder.FrameSamples];
        private readonly float[] integrationDrain = new float[VoiceEncoder.FrameSamples];
        private ushort talkspurt;
        private ushort expected;
        private bool initialized;
        private volatile bool started;
        private volatile bool playbackMuted;
        private int consecutiveLoss;
        private float targetMs = InitialTargetMilliseconds;
        private double lastTargetGrowAt;
        private double lastTargetShrinkAt;
        private double excessSince;
        private GameObject emitter;
        private AkGameObj akObj;
        private uint playingId;
        private double lastPacketAt;
        private double nextPostAttempt;
        private long watchdogReadCallbacks = -1;
        private double watchdogChangedAt;
        private volatile bool audioFaulted;

        internal float Energy { get; private set; }
        internal int LastDecodedSequence { get; private set; } = -1;
        internal int FecFrames { get; private set; }
        internal int PlcFrames { get; private set; }
        internal int UserId => userId;
        internal int QueuedPackets { get { lock (stateSync) return packets.Count; } }
        internal bool HasEmitter => emitter != null;
        internal bool IsPlaying => started && (integrationReceiver || playingId != 0);
        internal bool IsSpeaking => started && Clock.Now - lastPacketAt < 0.3;
        internal Vector3 EmitterPosition => emitter == null ? Vector3.zero : emitter.transform.position;
        internal int LatePackets { get; private set; }
        internal int JitterWaits { get; private set; }
        internal int LatencyDrops { get; private set; }
        internal float TargetMilliseconds => targetMs;
        internal float EncodedBufferedMilliseconds { get { lock (stateSync) return packets.Count * FrameMilliseconds; } }
        internal float PcmBufferedMilliseconds => pcm.BufferedSamples * 1000f / VoiceEncoder.SampleRate;
        internal float BufferedMilliseconds => EncodedBufferedMilliseconds + PcmBufferedMilliseconds;
        internal int LastReceivedSequence { get; private set; } = -1;
        internal float LastPacketGapMilliseconds { get; private set; }
        internal float LargestPacketGapMilliseconds { get; private set; }
        internal long AudioReadCallbacks => pcm.ReadCallbacks;
        internal long AudioRequestedSamples => pcm.RequestedSamples;
        internal long AudioZeroFilledSamples => pcm.ZeroFilledSamples;
        internal int AudioLastRequestSamples => pcm.LastRequestSamples;

        internal RemoteVoice(int userId) => this.userId = userId;

        internal void ClearAudio()
        {
            lock (stateSync)
            {
                pcm.Clear();
                Energy = 0f;
            }
        }

        internal void DrainOneFrame() => ReadAudio(integrationDrain);

        internal void Enqueue(VoicePacket packet)
        {
            lock (stateSync)
            {
                double now = Clock.Now;
                if (lastPacketAt > 0)
                {
                    LastPacketGapMilliseconds = (float)((now - lastPacketAt) * 1000.0);
                    if (LastPacketGapMilliseconds > LargestPacketGapMilliseconds)
                        LargestPacketGapMilliseconds = LastPacketGapMilliseconds;
                }
                lastPacketAt = now;
                LastReceivedSequence = packet.Sequence;
                if (!initialized)
                {
                    ResetStream(packet);
                }
                else if (packet.Talkspurt != talkspurt)
                {

                    short spurtDelta = (short)(packet.Talkspurt - talkspurt);
                    if (spurtDelta <= 0
                        && (packet.Sequence >= FreshSpurtSequenceWindow
                            || (spurtDelta > -SessionRestartSpurtJump && LastPacketGapMilliseconds <= SessionRestartGapMilliseconds)))
                        return;
                    ResetStream(packet);
                }
                if (IsOlder(packet.Sequence, expected))
                {

                    LatePackets++;
                    VoiceDiagnostics.LatePacket();
                    if (now - lastTargetGrowAt > TargetGrowCooldownSeconds)
                    {
                        targetMs = Mathf.Min(MaximumTargetMilliseconds, targetMs + FrameMilliseconds);
                        lastTargetGrowAt = now;
                    }
                    return;
                }
                if (!packets.ContainsKey(packet.Sequence) && packets.Count < MaximumStoredPackets)
                    packets.Add(packet.Sequence, packet.Payload);
                else if (packets.Count >= MaximumStoredPackets)
                    VoiceDiagnostics.QueueDrop();
                if (!started && packets.Count * FrameMilliseconds >= targetMs)
                    started = true;
            }
        }

        private void ResetStream(VoicePacket packet)
        {
            talkspurt = packet.Talkspurt;
            expected = packet.Sequence;
            packets.Clear();
            pcm.Clear();
            started = false;
            consecutiveLoss = 0;
            excessSince = 0;
            initialized = true;
        }

        internal void Update(bool muted)
        {
            EnsureEmitter();
            lock (stateSync)
            {
                playbackMuted = muted;
                if (muted)
                {

                    if (initialized)
                    {
                        initialized = false;
                        started = false;
                        packets.Clear();
                        pcm.Clear();
                        Energy = 0f;
                    }
                    return;
                }
                AdaptLatency();
            }
        }

        private void AdaptLatency()
        {
            double now = Clock.Now;
            if (targetMs > MinimumTargetMilliseconds
                && now - lastTargetGrowAt > TargetShrinkAfterSeconds
                && now - lastTargetShrinkAt > TargetShrinkAfterSeconds)
            {
                targetMs = Mathf.Max(MinimumTargetMilliseconds, targetMs - FrameMilliseconds);
                lastTargetShrinkAt = now;
            }
            if (!started)
                return;
            float buffered = packets.Count * FrameMilliseconds + PcmBufferedMilliseconds;
            if (buffered <= targetMs + TrimSlackMilliseconds)
            {
                excessSince = 0;
                return;
            }
            if (excessSince == 0)
            {
                excessSince = now;
                return;
            }
            if (now - excessSince < TrimHoldSeconds)
                return;
            int dropFrames = (int)((buffered - targetMs) / FrameMilliseconds);
            for (int i = 0; i < dropFrames; i++)
            {
                if (packets.Remove(expected))
                {
                    LatencyDrops++;
                    VoiceDiagnostics.LatencyDrop();
                }
                expected++;
            }
            consecutiveLoss = 0;
            excessSince = 0;
        }

        private bool HasFuturePacket()
        {
            foreach (ushort sequence in packets.Keys)
                if ((short)(sequence - expected) > 0) return true;
            return false;
        }

        private static bool IsOlder(ushort sequence, ushort reference) => (short)(sequence - reference) < 0;

        private void ReadAudio(float[] destination)
        {
            if (!started || playbackMuted)
            {
                pcm.Clear();
                pcm.Read(destination);
                return;
            }
            while (pcm.BufferedSamples < destination.Length)
            {
                if (!DecodeNext())
                    break;
            }
            pcm.Read(destination);
        }

        private bool DecodeNext()
        {
            byte[] payload;
            bool fec = false;
            bool plc = false;
            lock (stateSync)
            {
                if (packets.TryGetValue(expected, out payload))
                {
                    packets.Remove(expected);
                    LastDecodedSequence = expected;
                    consecutiveLoss = 0;
                }
                else if (packets.TryGetValue((ushort)(expected + 1), out payload))
                {
                    fec = true;
                    FecFrames++;
                    VoiceDiagnostics.Fec();
                    consecutiveLoss = 0;
                }
                else if (consecutiveLoss < MaximumPlcFrames)
                {
                    plc = true;
                    PlcFrames++;
                    VoiceDiagnostics.Plc();
                    consecutiveLoss++;
                }
                else if (!HasFuturePacket())
                {
                    return false;
                }
                expected++;
            }
            int count;
            if (payload != null)
                count = decoder.Decode(payload, 0, payload.Length, decoded, 0, VoiceEncoder.FrameSamples, fec);
            else if (plc)
                count = decoder.Decode(null, 0, 0, decoded, 0, VoiceEncoder.FrameSamples, false);
            else
            {
                Array.Clear(decoded, 0, decoded.Length);
                count = decoded.Length;
            }
            double sum = 0;
            for (int i = 0; i < count; i++) sum += (double)decoded[i] * decoded[i];
            Energy = (float)Math.Sqrt(sum / count) / short.MaxValue;
            pcm.Write(decoded, count);
            return true;
        }

        private void EnsureEmitter()
        {

            if (integrationReceiver)
                return;
            if (!VoiceRuntime.SourcePosition(userId, out Vector3 at, out Quaternion rotation))
                return;
            if (emitter == null)
            {
                if (!ModAudioBank.Loaded)
                    return;
                emitter = new GameObject($"Voice-{userId}");
                akObj = emitter.AddComponent<AkGameObj>();
                akObj.Register();
                akObj.AttachToTransform(emitter.transform);
                appliedGain = -1f;
                PostVoiceEvent();
            }
            emitter.transform.SetPositionAndRotation(at, rotation);
            MonitorPlayback();
            ApplyOutputGain();
        }

        private void PostVoiceEvent()
        {
            if (Clock.Now < nextPostAttempt)
                return;
            nextPostAttempt = Clock.Now + PostRetrySeconds;
            watchdogReadCallbacks = -1;
            playingId = AkAudioInputManager.PostAudioInputEvent(ModAudioBank.VoiceEvent, emitter, ProvideSamples, ProvideFormat);
            if (playingId == 0)
                MelonLoader.MelonLogger.Warning($"Failed to post voice event for user {userId}, retrying in {PostRetrySeconds:0} s");
        }

        private void MonitorPlayback()
        {
            if (audioFaulted)
            {
                audioFaulted = false;
                MelonLoader.MelonLogger.Warning($"Voice decode fault for user {userId}, resynced on the live stream");
            }
            if (playingId == 0)
            {
                PostVoiceEvent();
                return;
            }
            bool expectingAudio;
            lock (stateSync)
                expectingAudio = started && !playbackMuted && Clock.Now - lastPacketAt < 1.0;
            long callbacks = pcm.ReadCallbacks;
            if (callbacks != watchdogReadCallbacks)
            {
                watchdogReadCallbacks = callbacks;
                watchdogChangedAt = Clock.Now;
                return;
            }
            if (!expectingAudio)
            {
                watchdogChangedAt = Clock.Now;
                return;
            }
            if (Clock.Now - watchdogChangedAt < PlaybackStallSeconds)
                return;
            MelonLoader.MelonLogger.Warning($"Voice playback for user {userId} stalled, reposting audio event");
            AkSoundEngine.StopPlayingID(playingId);
            playingId = 0;
            nextPostAttempt = 0;
            PostVoiceEvent();
        }

        private void ApplyOutputGain()
        {
            float gain = VoiceRuntime.OutputGain;
            if (gain == appliedGain)
                return;
            appliedGain = gain;
            AkSoundEngine.SetGameObjectOutputBusVolume(emitter, null, gain);
        }

        private bool ProvideSamples(uint id, uint channelIndex, float[] samples)
        {
            try
            {
                ReadAudio(samples);
            }
            catch (Exception)
            {

                Array.Clear(samples, 0, samples.Length);
                lock (stateSync)
                {
                    packets.Clear();
                    pcm.Clear();
                    initialized = false;
                    started = false;
                }
                audioFaulted = true;
            }
            return true;
        }

        private void ProvideFormat(uint id, AkAudioFormat format)
        {
            format.channelConfig.SetStandard((uint)AkSoundEngine.AK_SPEAKER_SETUP_MONO);
            format.uSampleRate = (uint)VoiceEncoder.SampleRate;
        }

        internal void RebindPlayback()
        {
            if (integrationReceiver)
                return;
            if (playingId != 0)
            {
                AkSoundEngine.StopPlayingID(playingId);
                playingId = 0;
            }
            if (emitter != null)
                UnityEngine.Object.Destroy(emitter);
            emitter = null;
            akObj = null;
            nextPostAttempt = 0;
        }

        public void Dispose()
        {
            if (playingId != 0)
            {
                AkSoundEngine.StopPlayingID(playingId);
                playingId = 0;
            }
            if (emitter != null)
                UnityEngine.Object.Destroy(emitter);
        }
    }
}
