using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace TavernVoice
{

    internal sealed class VoiceCapture
    {
        private const int BufferCount = 4;
        private const int BufferBytes = VoiceEncoder.FrameSamples * 2;

        private readonly VoiceEncoder encoder = new VoiceEncoder();
        private readonly float[] pending = new float[VoiceEncoder.FrameSamples];
        private readonly object bufferSync = new object();
        private readonly float[] ring = new float[VoiceEncoder.SampleRate];
        private int ringRead;
        private int ringWrite;
        private int ringCount;

        private IntPtr hWaveIn;
        private IntPtr captureEvent;
        private IntPtr[] headerPtrs;
        private IntPtr[] bufferPtrs;
        private Thread drainThread;
        private volatile bool capturing;

        private long nextStartAttempt;
        private ushort talkspurt;
        private ushort sequence;

        internal string SelectedDevice { get; private set; }
        internal string StartFailure { get; private set; }
        internal float LocalEnergy { get; private set; }
        internal bool IsRunning => capturing;

        internal static string[] DeviceNames()
        {
            var names = new List<string>();
            try
            {
                int count = waveInGetNumDevs();
                for (uint i = 0; i < count; i++)
                    if (waveInGetDevCaps((UIntPtr)i, out WAVEINCAPS caps, (uint)Marshal.SizeOf(typeof(WAVEINCAPS))) == 0)
                        names.Add(caps.szPname);
            }
            catch (Exception exception)
            {
                MelonLoader.MelonLogger.Warning($"Failed to enumerate capture devices: {exception.Message}");
            }
            return names.ToArray();
        }

        internal void SetDevice(string device)
        {
            if (SelectedDevice == device)
                return;
            Stop();
            SelectedDevice = device;
        }

        internal void Start()
        {
            if (capturing)
                return;
            long now = Stopwatch.GetTimestamp();
            if (now < nextStartAttempt)
                return;

            try
            {
                OpenDevice();
            }
            catch (Exception exception)
            {
                CloseDevice();
                nextStartAttempt = now + Stopwatch.Frequency * 10;
                if (StartFailure != exception.Message)
                {
                    StartFailure = exception.Message;
                    MelonLoader.MelonLogger.Warning($"Microphone '{SelectedDevice ?? "default"}' unavailable, retrying every 10 s: {exception.Message}");
                }
                return;
            }
            if (StartFailure != null)
            {
                StartFailure = null;
                MelonLoader.MelonLogger.Msg("Microphone recovered");
            }
            MelonLoader.MelonLogger.Msg($"Capturing microphone '{SelectedDevice ?? "default"}' (48 kHz mono via waveIn)");
        }

        private void OpenDevice()
        {
            uint deviceId = ResolveDeviceId(SelectedDevice);
            var format = new WAVEFORMATEX
            {
                wFormatTag = WAVE_FORMAT_PCM,
                nChannels = 1,
                nSamplesPerSec = VoiceEncoder.SampleRate,
                wBitsPerSample = 16,
                nBlockAlign = 2,
                nAvgBytesPerSec = VoiceEncoder.SampleRate * 2,
                cbSize = 0
            };

            captureEvent = CreateEvent(IntPtr.Zero, false, false, null);
            if (captureEvent == IntPtr.Zero)
                throw new InvalidOperationException("CreateEvent failed");

            Check(waveInOpen(out hWaveIn, deviceId, ref format, captureEvent, IntPtr.Zero, CALLBACK_EVENT), "waveInOpen");

            int headerSize = Marshal.SizeOf(typeof(WAVEHDR));
            headerPtrs = new IntPtr[BufferCount];
            bufferPtrs = new IntPtr[BufferCount];
            for (int i = 0; i < BufferCount; i++)
            {
                bufferPtrs[i] = Marshal.AllocHGlobal(BufferBytes);
                headerPtrs[i] = Marshal.AllocHGlobal(headerSize);
                var header = new WAVEHDR { lpData = bufferPtrs[i], dwBufferLength = BufferBytes };
                Marshal.StructureToPtr(header, headerPtrs[i], false);
                Check(waveInPrepareHeader(hWaveIn, headerPtrs[i], (uint)headerSize), "waveInPrepareHeader");
                Check(waveInAddBuffer(hWaveIn, headerPtrs[i], (uint)headerSize), "waveInAddBuffer");
            }

            lock (bufferSync) { ringRead = ringWrite = ringCount = 0; }
            capturing = true;
            drainThread = new Thread(DrainLoop) { IsBackground = true, Name = "VoipCapture" };
            drainThread.Start();
            Check(waveInStart(hWaveIn), "waveInStart");
        }

        internal void Stop()
        {
            if (!capturing && hWaveIn == IntPtr.Zero)
                return;
            capturing = false;
            if (captureEvent != IntPtr.Zero)
                SetEvent(captureEvent);
            try { drainThread?.Join(500); }
            catch { }
            drainThread = null;
            CloseDevice();
            Discard();
        }

        internal void NewTalkspurt() => encoder.ResetTalkspurt();

        internal void Discard()
        {
            lock (bufferSync) { ringRead = ringWrite = ringCount = 0; }
            encoder.ResetTalkspurt();
            LocalEnergy = 0f;
        }

        private void CloseDevice()
        {
            if (hWaveIn != IntPtr.Zero)
            {
                waveInReset(hWaveIn);
                if (headerPtrs != null)
                {
                    int headerSize = Marshal.SizeOf(typeof(WAVEHDR));
                    for (int i = 0; i < headerPtrs.Length; i++)
                        if (headerPtrs[i] != IntPtr.Zero)
                            waveInUnprepareHeader(hWaveIn, headerPtrs[i], (uint)headerSize);
                }
                waveInClose(hWaveIn);
                hWaveIn = IntPtr.Zero;
            }
            if (headerPtrs != null)
            {
                foreach (IntPtr ptr in headerPtrs)
                    if (ptr != IntPtr.Zero) Marshal.FreeHGlobal(ptr);
                headerPtrs = null;
            }
            if (bufferPtrs != null)
            {
                foreach (IntPtr ptr in bufferPtrs)
                    if (ptr != IntPtr.Zero) Marshal.FreeHGlobal(ptr);
                bufferPtrs = null;
            }
            if (captureEvent != IntPtr.Zero)
            {
                CloseHandle(captureEvent);
                captureEvent = IntPtr.Zero;
            }
        }

        private static uint ResolveDeviceId(string name)
        {
            if (string.IsNullOrEmpty(name))
                return WAVE_MAPPER;
            int count = waveInGetNumDevs();
            for (uint i = 0; i < count; i++)
                if (waveInGetDevCaps((UIntPtr)i, out WAVEINCAPS caps, (uint)Marshal.SizeOf(typeof(WAVEINCAPS))) == 0 && caps.szPname == name)
                    return i;
            return WAVE_MAPPER;
        }

        private void DrainLoop()
        {
            int headerSize = Marshal.SizeOf(typeof(WAVEHDR));
            while (capturing)
            {
                WaitForSingleObject(captureEvent, 100);
                for (int i = 0; i < BufferCount && capturing; i++)
                {
                    var header = (WAVEHDR)Marshal.PtrToStructure(headerPtrs[i], typeof(WAVEHDR));
                    if ((header.dwFlags & WHDR_DONE) == 0)
                        continue;
                    Ingest(bufferPtrs[i], (int)header.dwBytesRecorded);
                    header.dwFlags &= ~WHDR_DONE;
                    Marshal.StructureToPtr(header, headerPtrs[i], false);
                    if (capturing)
                        waveInAddBuffer(hWaveIn, headerPtrs[i], (uint)headerSize);
                }
            }
        }

        private void Ingest(IntPtr data, int bytes)
        {
            int samples = bytes / 2;
            if (samples == 0)
                return;
            lock (bufferSync)
            {
                for (int i = 0; i < samples; i++)
                    Push(Marshal.ReadInt16(data, i * 2) / 32768f);
            }
        }

        private void Push(float sample)
        {
            if (ringCount == ring.Length)
            {
                ringRead = (ringRead + 1) % ring.Length;
                ringCount--;
            }
            ring[ringWrite] = sample;
            ringWrite = (ringWrite + 1) % ring.Length;
            ringCount++;
        }

        internal void Update(Action<VoicePacket> send)
        {
            if (!capturing)
                return;
            while (true)
            {
                lock (bufferSync)
                {
                    if (ringCount < VoiceEncoder.FrameSamples)
                        break;
                    for (int i = 0; i < VoiceEncoder.FrameSamples; i++)
                    {
                        pending[i] = ring[ringRead];
                        ringRead = (ringRead + 1) % ring.Length;
                        ringCount--;
                    }
                }
                Encode(send);
            }
        }

        private void Encode(Action<VoicePacket> send)
        {
            bool encodedFrame = encoder.Encode(pending, out byte[] payload, out bool newTalkspurt, out float energy);
            VoiceDiagnostics.Captured(encodedFrame);
            if (encodedFrame)
            {
                if (newTalkspurt)
                {
                    talkspurt++;
                    sequence = 0;
                }
                send(new VoicePacket { Talkspurt = talkspurt, Sequence = sequence++, Payload = payload });
            }
            LocalEnergy = energy;
        }

        private static void Check(int mmResult, string call)
        {
            if (mmResult == MMSYSERR_NOERROR)
                return;
            var text = new StringBuilder(256);
            waveInGetErrorText(mmResult, text, text.Capacity);
            throw new InvalidOperationException($"{call} failed ({mmResult}): {text}");
        }

        private const ushort WAVE_FORMAT_PCM = 1;
        private const uint WAVE_MAPPER = 0xFFFFFFFF;
        private const uint CALLBACK_EVENT = 0x00050000;
        private const uint WHDR_DONE = 0x00000001;
        private const int MMSYSERR_NOERROR = 0;

        [StructLayout(LayoutKind.Sequential)]
        private struct WAVEFORMATEX
        {
            public ushort wFormatTag;
            public ushort nChannels;
            public uint nSamplesPerSec;
            public uint nAvgBytesPerSec;
            public ushort nBlockAlign;
            public ushort wBitsPerSample;
            public ushort cbSize;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WAVEHDR
        {
            public IntPtr lpData;
            public uint dwBufferLength;
            public uint dwBytesRecorded;
            public IntPtr dwUser;
            public uint dwFlags;
            public uint dwLoops;
            public IntPtr lpNext;
            public IntPtr reserved;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WAVEINCAPS
        {
            public ushort wMid;
            public ushort wPid;
            public uint vDriverVersion;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string szPname;
            public uint dwFormats;
            public ushort wChannels;
            public ushort wReserved1;
        }

        [DllImport("winmm.dll")] private static extern int waveInGetNumDevs();
        [DllImport("winmm.dll", CharSet = CharSet.Unicode)] private static extern int waveInGetDevCaps(UIntPtr uDeviceID, out WAVEINCAPS pwic, uint cbwic);
        [DllImport("winmm.dll")] private static extern int waveInOpen(out IntPtr phwi, uint uDeviceID, ref WAVEFORMATEX pwfx, IntPtr dwCallback, IntPtr dwInstance, uint fdwOpen);
        [DllImport("winmm.dll")] private static extern int waveInPrepareHeader(IntPtr hwi, IntPtr pwh, uint cbwh);
        [DllImport("winmm.dll")] private static extern int waveInUnprepareHeader(IntPtr hwi, IntPtr pwh, uint cbwh);
        [DllImport("winmm.dll")] private static extern int waveInAddBuffer(IntPtr hwi, IntPtr pwh, uint cbwh);
        [DllImport("winmm.dll")] private static extern int waveInStart(IntPtr hwi);
        [DllImport("winmm.dll")] private static extern int waveInStop(IntPtr hwi);
        [DllImport("winmm.dll")] private static extern int waveInReset(IntPtr hwi);
        [DllImport("winmm.dll")] private static extern int waveInClose(IntPtr hwi);
        [DllImport("winmm.dll", CharSet = CharSet.Unicode)] private static extern int waveInGetErrorText(int mmrError, StringBuilder pszText, int cchText);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr CreateEvent(IntPtr lpEventAttributes, bool bManualReset, bool bInitialState, string lpName);
        [DllImport("kernel32.dll")] private static extern bool SetEvent(IntPtr hEvent);
        [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr hObject);
        [DllImport("kernel32.dll")] private static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);
    }
}
