using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading;
using MelonLoader;

namespace TavernVoice
{
    internal static class RelayHost
    {
        private const string ExeName = "TavernVoiceRelay.exe";
        private const int ExitReplaced = 3;
        private const int ExitPortHeld = 2;
        private static readonly Queue<VoicePacket> tapped = new Queue<VoicePacket>();
        private static Run current;
        private static int sentGeneration = -1;
        private static bool rosterDirty = true;
        private static bool tapListDirty = true;
        private static bool rangeDirty;
        private static double nextRoster;
        private static double nextPositions;
        private static double nextTapList;

        private sealed class Run
        {
            internal byte[] Key;
            internal UdpClient Control;
            internal IPEndPoint Relay;
            internal volatile bool Stopping;
            internal volatile Process Process;
            internal volatile int Generation;
            internal volatile int Starts;
            internal volatile bool Listening;
        }

        internal static bool Enabled => current != null && !current.Stopping;
        internal static bool Listening => Enabled && current.Listening;
        internal static int Starts => current?.Starts ?? 0;

        internal static void RosterChanged() => rosterDirty = true;
        internal static void TapListChanged() => tapListDirty = true;
        internal static void RangeChanged() => rangeDirty = true;

        internal static void Start()
        {
            if (!Settings.RelayEnabled)
            {
                MelonLogger.Msg("RelayEnabled is off: voice goes through the game connection only");
                return;
            }
            if (Enabled)
                return;
            var run = new Run();
            try
            {
                run.Key = new byte[16];
                using (RandomNumberGenerator rng = RandomNumberGenerator.Create())
                    rng.GetBytes(run.Key);
                run.Control = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
                DisableConnectionReset(run.Control.Client);
                run.Relay = new IPEndPoint(IPAddress.Loopback, Settings.RelayPort);
                string path = Unpack();
                current = run;
                sentGeneration = -1;
                new Thread(() => Supervise(run, path)) { IsBackground = true, Name = "TavernVoice relay" }.Start();
                new Thread(() => ReadTaps(run)) { IsBackground = true, Name = "TavernVoice tap" }.Start();
            }
            catch (Exception exception)
            {
                run.Stopping = true;
                run.Control?.Close();
                current = null;
                MelonLogger.Error($"The voice relay could not start, so voice uses the game connection only: {exception.Message}");
            }
        }

        internal static void DisableConnectionReset(Socket socket)
        {
            try
            {
                socket.IOControl(unchecked((int)0x9800000C), new byte[4], null);
            }
            catch (Exception)
            {
            }
        }

        private static string Unpack()
        {
            byte[] want;
            using (Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream("TavernVoice." + ExeName))
            {
                if (s == null)
                    throw new InvalidOperationException("the relay is not inside TavernVoice.dll");
                want = new byte[s.Length];
                int read = 0;
                while (read < want.Length)
                    read += s.Read(want, read, want.Length - read);
            }
            string dir = Path.Combine(MelonLoader.Utils.MelonEnvironment.UserDataDirectory, "TavernVoice");
            Directory.CreateDirectory(dir);
            string hash = Hash(want);
            string path = Path.Combine(dir, "TavernVoiceRelay-" + hash.Substring(0, 8) + ".exe");
            if (File.Exists(path) && Hash(File.ReadAllBytes(path)) == hash)
                return path;
            string tmp = path + ".tmp";
            File.WriteAllBytes(tmp, want);
            if (File.Exists(path))
                File.Delete(path);
            File.Move(tmp, path);
            MelonLogger.Msg($"Relay unpacked to {path}");
            foreach (string old in Directory.GetFiles(dir, "TavernVoiceRelay*.exe"))
            {
                if (string.Equals(old, path, StringComparison.OrdinalIgnoreCase))
                    continue;
                try
                {
                    File.Delete(old);
                }
                catch (Exception)
                {
                }
            }
            return path;
        }

        private static string Hash(byte[] data)
        {
            using (SHA256 sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(data)).Replace("-", "").ToLowerInvariant();
        }

        private static void Supervise(Run run, string path)
        {
            int backoff = 1;
            while (!run.Stopping)
            {
                int code;
                double startedAt = Clock.Now;
                try
                {
                    code = RunOnce(run, path);
                }
                catch (Exception exception)
                {
                    MelonLogger.Warning($"Starting the relay failed: {exception.Message}");
                    code = -1;
                }
                run.Listening = false;
                if (run.Stopping)
                {
                    MelonLogger.Msg($"Relay stopped (exit code {code})");
                    return;
                }
                if (code == ExitReplaced)
                {
                    run.Stopping = true;
                    MelonLogger.Warning("A newer relay took the port over, so this server stops running one (another copy of this server is up?)");
                    return;
                }
                if (code == ExitPortHeld)
                    MelonLogger.Warning($"Something else holds UDP port {run.Relay.Port}; voice stays on the game connection until the relay can bind it");
                if (Clock.Now - startedAt > 60)
                    backoff = 1;
                MelonLogger.Msg($"Relay exited with code {code}, starting it again in {backoff} s");
                for (int waited = 0; waited < backoff * 10 && !run.Stopping; waited++)
                    Thread.Sleep(100);
                backoff = Math.Min(60, backoff * 2);
            }
        }

        private static int RunOnce(Run run, string path)
        {
            var si = new ProcessStartInfo
            {
                FileName = path,
                Arguments = $"--port {run.Relay.Port} --parent {Process.GetCurrentProcess().Id} --key {BitConverter.ToString(run.Key).Replace("-", "").ToLowerInvariant()} --range {Settings.RangeMeters.ToString(System.Globalization.CultureInfo.InvariantCulture)} --tap {((IPEndPoint)run.Control.Client.LocalEndPoint).Port}",
                WorkingDirectory = Path.GetDirectoryName(path),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using (Process process = Process.Start(si))
            {
                run.Process = process;
                run.Starts++;
                Pipe(process.StandardError);
                string line;
                while ((line = process.StandardOutput.ReadLine()) != null)
                {
                    if (line.StartsWith("listening on", StringComparison.Ordinal))
                    {
                        run.Listening = true;
                        run.Generation++;
                    }
                    MelonLogger.Msg("[relay] " + line);
                }
                process.WaitForExit();
                run.Process = null;
                return process.ExitCode;
            }
        }

        private static void Pipe(StreamReader reader)
        {
            new Thread(() =>
            {
                try
                {
                    string line;
                    while ((line = reader.ReadLine()) != null)
                        MelonLogger.Warning("[relay] " + line);
                }
                catch (Exception)
                {
                }
            }) { IsBackground = true }.Start();
        }

        private static void ReadTaps(Run run)
        {
            var from = new IPEndPoint(IPAddress.Any, 0);
            while (!run.Stopping)
            {
                try
                {
                    byte[] data = run.Control.Receive(ref from);
                    if (!IPAddress.IsLoopback(from.Address) || !Wire.ReadTap(data, data.Length, run.Key, out VoicePacket packet))
                        continue;
                    lock (tapped)
                    {
                        if (tapped.Count < 256)
                            tapped.Enqueue(packet);
                    }
                }
                catch (SocketException)
                {
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
            }
        }

        private static void Send(Run run, byte[] data)
        {
            try
            {
                run.Control.Send(data, data.Length, run.Relay);
            }
            catch (Exception)
            {
            }
        }

        internal static void Update()
        {
            Run run = current;
            if (run == null || run.Stopping)
                return;
            double now = Clock.Now;
            if (sentGeneration != run.Generation)
            {
                sentGeneration = run.Generation;
                rosterDirty = tapListDirty = true;
                rangeDirty = false;
            }
            if (rangeDirty)
            {
                rangeDirty = false;
                Send(run, Wire.RangePacket(run.Key, Settings.RangeMeters));
            }
            if (rosterDirty || now >= nextRoster)
            {
                rosterDirty = false;
                nextRoster = now + 5.0;
                Send(run, Wire.RosterPacket(run.Key, ServerVoice.Roster()));
            }
            if (tapListDirty || now >= nextTapList)
            {
                tapListDirty = false;
                nextTapList = now + 5.0;
                Send(run, Wire.TapListPacket(run.Key, ServerVoice.FallbackSenders()));
            }
            if (now >= nextPositions)
            {
                nextPositions = now + 1.0 / Settings.PositionRateHz;
                Send(run, Wire.PosPacket(run.Key, ServerVoice.Positions()));
            }
            while (true)
            {
                VoicePacket packet;
                lock (tapped)
                {
                    if (tapped.Count == 0)
                        break;
                    packet = tapped.Dequeue();
                }
                InGameRelay.FromTap(packet);
            }
        }

        internal static void Stop()
        {
            Run run = current;
            current = null;
            if (run == null)
                return;
            run.Stopping = true;
            Send(run, new byte[] { Wire.Version, Wire.Quit });
            try
            {
                Process process = run.Process;
                if (process != null && !process.WaitForExit(2000))
                    process.Kill();
            }
            catch (Exception)
            {
            }
            try
            {
                run.Control.Close();
            }
            catch (Exception)
            {
            }
            lock (tapped)
                tapped.Clear();
        }
    }
}
