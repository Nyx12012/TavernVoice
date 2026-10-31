using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Alta.Global;
using Alta.QuickAccessActions;
using MelonLoader;
using UnityEngine;

namespace TavernVoice
{
    internal static class TestMode
    {
        internal const int PeerSender = 990001;
        internal const int EchoSender = 990002;
        internal const string PeerName = "TavernVoice Peer";
        internal static readonly bool Active = Array.Exists(Environment.GetCommandLineArgs(), a => string.Equals(a, "/tavernvoice_test", StringComparison.OrdinalIgnoreCase));

        private sealed class Stats
        {
            internal long Relay;
            internal long InGame;
            internal double Last;
            internal double MaxGap;
            internal float Energy;
            internal int Spurt = -1;
        }

        private static readonly object sync = new object();
        private static readonly Dictionary<int, Stats> stats = new Dictionary<int, Stats>();
        private static readonly Dictionary<int, double> drainAt = new Dictionary<int, double>();
        private static VoiceEncoder encoder;
        private static readonly float[] frame = new float[VoiceEncoder.FrameSamples];
        private static double nextFrame = -1;
        private static ushort spurt = 1;
        private static ushort sequence;
        private static int frameIndex;
        private static long sentRelay;
        private static long sentInGame;
        private static double nextReport;
        private static double nextFiles;
        private static bool sending;
        private static NameBoard board;

        internal static byte? ForcedFlags { get; private set; }
        internal static string BoardName { get; private set; } = PeerName;

        internal static string Folder
        {
            get
            {
                string dir = Path.Combine(MelonLoader.Utils.MelonEnvironment.UserDataDirectory, "TavernVoice");
                Directory.CreateDirectory(dir);
                return dir;
            }
        }

        internal static bool Take(string name, out string content)
        {
            content = null;
            string path = Path.Combine(Folder, name);
            if (!File.Exists(path))
                return false;
            try
            {
                content = File.ReadAllText(path).Trim();
                File.Delete(path);
                return true;
            }
            catch (IOException)
            {
                return false;
            }
        }

        internal static byte ParseFlags(string text)
        {
            byte flags = 0;
            foreach (string word in (text ?? "").ToLowerInvariant().Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (word == "muted")
                    flags |= Wire.FlagMuted;
                else if (word == "nomic")
                    flags |= Wire.FlagNoMic;
            }
            return flags;
        }

        internal static void Received(VoicePacket packet, bool viaRelay)
        {
            if (!Active)
                return;
            double now = Clock.Now;
            lock (sync)
            {
                if (!stats.TryGetValue(packet.SenderId, out Stats s))
                    stats[packet.SenderId] = s = new Stats();
                if (viaRelay) s.Relay++; else s.InGame++;
                if (s.Last > 0 && s.Spurt == packet.Talkspurt && now - s.Last > s.MaxGap)
                    s.MaxGap = now - s.Last;
                s.Last = now;
                s.Spurt = packet.Talkspurt;
            }
        }

        internal static void NewTalkspurt()
        {
            if (encoder == null)
                return;
            spurt++;
            sequence = 0;
            encoder.ResetTalkspurt();
        }

        internal static void Sent(bool viaRelay)
        {
            if (viaRelay) sentRelay++; else sentInGame++;
        }

        internal static void UpdateTone(bool canSend, Action<VoicePacket> send)
        {
            double now = Clock.Now;
            sending = canSend;
            if (!canSend)
            {
                nextFrame = -1;
                return;
            }
            if (encoder == null)
                encoder = new VoiceEncoder();
            if (nextFrame < 0)
            {
                nextFrame = now;
                NewTalkspurt();
            }
            int sentNow = 0;
            while (now >= nextFrame && sentNow < 5)
            {
                nextFrame += 0.02;
                sentNow++;
                for (int i = 0; i < frame.Length; i++)
                    frame[i] = (float)Math.Sin((frameIndex * frame.Length + i) * 2.0 * Math.PI * 440.0 / VoiceEncoder.SampleRate) * 0.25f;
                frameIndex++;
                if (encoder.Encode(frame, out byte[] payload, out _, out _))
                    send(new VoicePacket { Talkspurt = spurt, Sequence = sequence++, Payload = payload });
            }
            if (now - nextFrame > 0.5)
                nextFrame = now;
        }

        private static void UpdateFiles(double now)
        {
            if (now < nextFiles)
                return;
            nextFiles = now + 1.0;
            if (Take("test-state.txt", out string state))
            {
                ForcedFlags = state.Length == 0 || state == "free" ? (byte?)null : ParseFlags(state);
                MelonLogger.Msg($"TVTEST reporting flags {(ForcedFlags.HasValue ? ForcedFlags.Value.ToString() : "from the mic")}");
            }
            if (Take("test-board.txt", out string boardText))
            {
                if (board != null)
                {
                    board.Release();
                    board = null;
                }
                if (boardText != "off")
                {
                    BoardName = boardText.Length == 0 || boardText == "on" ? PeerName : boardText;
                    board = GlobalSettings<NameBoardsSettings>.Instance.NameBoardDefinition.Get() as NameBoard;
                    board?.Setup(BoardName, false);
                    MelonLogger.Msg($"TVTEST name board for {BoardName} {(board != null ? "shown" : "failed")}");
                }
            }
        }

        private static void UpdateBoard()
        {
            if (board == null)
                return;
            PlayerController controller = Player.Current?.PlayerController;
            Transform head = controller != null ? controller.Head : null;
            if (head == null)
                return;
            Vector3 forward = head.forward;
            forward.y = 0f;
            if (forward.sqrMagnitude < 0.0001f)
                forward = Vector3.forward;
            forward.Normalize();
            board.transform.position = head.position + forward * 1.6f;
            board.transform.rotation = Quaternion.LookRotation(-forward, Vector3.up);
        }

        private static string Where(int sender)
        {
            PlayerController controller = Player.Current?.PlayerController;
            Transform head = controller != null ? controller.Head : null;
            if (head == null || !VoiceRuntime.SourcePosition(sender, out Vector3 at, out _))
                return "n/a";
            return Vector3.Distance(head.position, at).ToString("0.0") + "m";
        }

        internal static void Update(ConcurrentDictionary<int, RemoteVoice> remotes)
        {
            if (!Active)
                return;
            double now = Clock.Now;
            UpdateFiles(now);
            UpdateBoard();
            foreach (KeyValuePair<int, RemoteVoice> pair in remotes)
            {
                RemoteVoice remote = pair.Value;
                if (!remote.IsPlaying)
                {
                    drainAt.Remove(pair.Key);
                    continue;
                }
                if (!drainAt.TryGetValue(pair.Key, out double at))
                    at = now;
                int drained = 0;
                while (now >= at && drained < 10)
                {
                    remote.DrainOneFrame();
                    at += 0.02;
                    drained++;
                }
                drainAt[pair.Key] = now - at > 0.5 ? now : at;
                lock (sync)
                {
                    if (stats.TryGetValue(pair.Key, out Stats s))
                        s.Energy = Math.Max(s.Energy, remote.Energy);
                }
            }
            if (now < nextReport)
                return;
            nextReport = now + 2.0;
            var line = new StringBuilder();
            line.Append($"TVTEST path={VoiceRuntime.PathDescription} sent relay={sentRelay} ingame={sentInGame} sending={(sending ? "yes" : "no")} local={(VoiceRuntime.LocalAnchored ? "anchored" : "free")}");
            if (board != null)
            {
                VoiceIcon icon = board.GetComponent<VoiceIcon>();
                line.Append($" board={(icon == null ? "no icon" : icon.Shown.ToString())}");
            }
            lock (sync)
            {
                foreach (KeyValuePair<int, Stats> pair in stats)
                {
                    Stats s = pair.Value;
                    line.Append($" | from {pair.Key}: relay={s.Relay} ingame={s.InGame} maxgap={s.MaxGap * 1000:0}ms energy={s.Energy:0.000} at={Where(pair.Key)} flags={PlayerStatus.FlagsOf(pair.Key)} icon={PlayerStatus.IconOf(pair.Key)}");
                    s.MaxGap = 0;
                    s.Energy = 0;
                }
            }
            MelonLogger.Msg(line.ToString());
        }
    }
}
