using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using Alta.Global;
using Alta.Networking;
using Alta.Networking.Servers;
using Alta.Voice;
using Features.Meta.Resolvers;
using HarmonyLib;
using MelonLoader;
using UnityEngine;
using AltaStream = Alta.Serialization.Stream;

namespace TavernVoice
{
    internal static class VoiceRuntime
    {
        private const string SavedMicrophoneKey = "CircuitsVoiceChat.MicrophoneDevice";
        private const string SavedOutputGainKey = "CircuitsVoiceChat.OutputGain2";
        private const double ServerSilenceCutoff = 6.0;
        private const double SessionWait = 2.0;
        private static readonly AccessTools.FieldRef<Connection, string> IpAddress = AccessTools.FieldRefAccess<Connection, string>("IpAddress");
        private static readonly ConcurrentDictionary<int, RemoteVoice> remotes = new ConcurrentDictionary<int, RemoteVoice>();
        private static readonly ReplacementVoiceChat voiceChat = new ReplacementVoiceChat();
        private static VoiceCapture capture;
        private static Connection serverConnection;
        private static ConnectionChannel channel;
        private static ConnectionChannel reliableChannel;
        private static RelayClient relay;
        private static Wire.SessionInfo session;
        private static bool wideChecked;
        private static bool wide;
        private static bool intendedMuted;
        private static bool moderatorMuted;
        private static float outputGain = 1f;
        private static double nextRemoteFaultLog;
        private static AkAudioListener boundListener;
        private static bool hasBoundListener;
        private static bool listenerSetEmptied;
        private static double lastServerPacket;
        private static bool suspended;
        private static double approvedAt;
        private static double nextSessionRequest;
        private static string savedMicrophone;
        private static byte sentFlags = 0xFF;

        internal static bool IsMuted => intendedMuted;
        internal static VoicePath? Path => session == null ? (VoicePath?)null : relay == null ? VoicePath.Fallback : relay.Path;

        internal static void Initialize(HarmonyLib.Harmony harmony)
        {
            Application.runInBackground = true;
            VoiceEncoder.SelfTest();
            MelonLogger.Msg("Concentus encode/decode self-test passed");
            MouthAnimationPatch.Apply(harmony);
            outputGain = Mathf.Clamp(PlayerPrefs.GetFloat(SavedOutputGainKey, 1f), 0f, 4f);
            savedMicrophone = PlayerPrefs.HasKey(SavedMicrophoneKey) ? PlayerPrefs.GetString(SavedMicrophoneKey, "") : null;
            harmony.Patch(AccessTools.Method(typeof(ConnectToServerUtility), "SetupConnectionToServer"),
                postfix: new HarmonyMethod(typeof(VoiceRuntime), nameof(ClientConnectionCreated)));
            harmony.Patch(AccessTools.Method(typeof(Connection), "ReceivePacket"),
                postfix: new HarmonyMethod(typeof(VoiceRuntime), nameof(ServerPacketArrived)));
            InterfaceResolver.SetInstanceOverride<IVoiceChat>(voiceChat);
            ApplicationManager.GameReload += ResetClient;
        }

        private static void ClientConnectionCreated(Connection connection)
        {
            ResetClient();
            if (!wideChecked)
            {
                wideChecked = true;
                wide = MessageTypes.Wide(out string detail);
                if (wide)
                    MelonLogger.Msg($"Message type {Wire.GameMessage} ready ({detail})");
                else
                    MelonLogger.Error($"{detail}, so message type {Wire.GameMessage} does not fit: voice stays off");
            }
            if (!wide)
                return;
            try
            {
                if (!ResolveChannels())
                    return;
            }
            catch (Exception exception)
            {
                MelonLogger.Error($"Voice channel lookup failed, voice stays off: {exception.Message}");
                return;
            }
            serverConnection = connection;
            lastServerPacket = Clock.Now;
            connection.SetHandler((MessageType)Wire.GameMessage, Receive);
            connection.Disconnected += ClientDisconnected;
            if (capture == null && !TestMode.Active)
            {
                capture = new VoiceCapture();
                if (savedMicrophone != null)
                    capture.SetDevice(savedMicrophone);
            }
        }

        private static bool ResolveChannels()
        {
            if (channel != null)
                return true;
            ConnectionChannel[] channels = GlobalSettings<NetworkingSettings>.Instance.Channels;
            ConnectionChannel fast = channels.Where(c => !c.IsReliable).OrderBy(c => c.MaximumSendInterval).FirstOrDefault();
            if (fast == null || fast.MaximumSendInterval > 0.03f)
            {
                MelonLogger.Error(fast == null ? "The game has no unreliable channel: voice stays off" : $"Fastest unreliable channel is too slow ({fast.MaximumSendInterval:0.000} s): voice stays off");
                return false;
            }
            reliableChannel = channels.Where(c => c.IsReliable).OrderBy(c => c.MaximumSendInterval).FirstOrDefault() ?? fast;
            channel = fast;
            return true;
        }

        private static void ClientDisconnected(Connection connection)
        {
            if (serverConnection == connection)
            {
                MelonLogger.Msg("Disconnected from the server: voice is idle");
                ResetClient();
            }
        }

        private static void ServerPacketArrived(Connection __instance)
        {
            if (__instance == serverConnection)
                lastServerPacket = Clock.Now;
        }

        private static void Receive(Connection connection, AltaStream stream)
        {
            switch (Wire.ReadSubtype(stream))
            {
                case Wire.SessionMessage:
                    var info = new Wire.SessionInfo();
                    Wire.SerializeSession(stream, info);
                    StartSession(info);
                    break;
                case Wire.GameVoiceDownMessage:
                    Deliver(VoiceProtocol.ReadServer(stream), false);
                    break;
                case Wire.StatusMessage:
                    var list = new List<Wire.StatusEntry>();
                    Wire.SerializeStatus(stream, list);
                    PlayerStatus.Apply(list);
                    break;
            }
        }

        private static void StartSession(Wire.SessionInfo info)
        {
            if (info.WireVersion != Wire.Version)
            {
                MelonLogger.Warning($"The server speaks voice wire {info.WireVersion}, this TavernVoice speaks {Wire.Version}: voice stays off");
                return;
            }
            if (session != null && session.Token.SequenceEqual(info.Token) && session.Tag == info.Tag && session.RelayPort == info.RelayPort)
                return;
            relay?.Dispose();
            relay = null;
            session = info;
            sentFlags = 0xFF;
            IPAddress host = ServerAddress(serverConnection);
            bool inGameOnly = info.RelayPort == 0 || Settings.ForceInGame || host == null;
            if (!inGameOnly)
            {
                try
                {
                    relay = new RelayClient(new IPEndPoint(host, info.RelayPort), info.Token, info.Tag, false, p => Deliver(p, true));
                }
                catch (Exception exception)
                {
                    MelonLogger.Warning($"The relay socket could not open, so voice stays in-game: {exception.Message}");
                    relay = null;
                    inGameOnly = true;
                }
            }
            MelonLogger.Msg(inGameOnly
                ? $"Voice session as {info.SenderId}: in-game only ({(info.RelayPort == 0 ? "the server runs no relay" : Settings.ForceInGame ? "ForceInGame is on" : "no server address")})"
                : $"Voice session as {info.SenderId}: relay {host}:{info.RelayPort}");
            if (inGameOnly)
                SendMode(true);
        }

        private static IPAddress ServerAddress(Connection connection)
        {
            try
            {
                string text = connection == null ? null : IpAddress(connection);
                if (string.IsNullOrEmpty(text))
                    return null;
                int colon = text.LastIndexOf(':');
                if (colon > 0 && text.IndexOf(':') == colon)
                    text = text.Substring(0, colon);
                text = text.Trim('[', ']');
                if (IPAddress.TryParse(text, out IPAddress parsed))
                    return parsed.IsIPv4MappedToIPv6 ? parsed.MapToIPv4() : parsed;
                return Dns.GetHostAddresses(text).FirstOrDefault(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);
            }
            catch (Exception exception)
            {
                MelonLogger.Warning($"The server's address is unknown, so voice stays in-game: {exception.Message}");
                return null;
            }
        }

        private static void Deliver(VoicePacket packet, bool viaRelay)
        {
            VoiceDiagnostics.Received(packet.Payload.Length);
            TestMode.Received(packet, viaRelay);
            remotes.GetOrAdd(packet.SenderId, id => new RemoteVoice(id)).Enqueue(packet);
        }

        private static void SendGame(int subtype, Action<AltaStream> body, ConnectionChannel on)
        {
            if (serverConnection == null || !serverConnection.IsApproved)
                return;
            serverConnection.Send(on, (MessageType)Wire.GameMessage, (_, stream) =>
            {
                Wire.WriteSubtype(stream, subtype);
                body?.Invoke(stream);
            });
            ChannelFlush.Flush(serverConnection, on);
        }

        private static void SendMode(bool fallback)
        {
            capture?.NewTalkspurt();
            TestMode.NewTalkspurt();
            SendGame(Wire.ModeMessage, stream => Wire.SerializeMode(stream, ref fallback), reliableChannel);
        }

        private static void RequestSession()
        {
            if (Clock.Now < nextSessionRequest)
                return;
            nextSessionRequest = Clock.Now + SessionWait;
            MelonLogger.Msg("Asking the server for a voice session");
            SendGame(Wire.SessionRequestMessage, null, channel);
        }

        internal static void Update()
        {
            VoiceDiagnostics.UpdateRates();
            if (serverConnection == null)
            {
                TestMode.Update(remotes);
                return;
            }
            ModAudioBank.EnsureLoaded();
            double now = Clock.Now;
            bool silent = now - lastServerPacket > ServerSilenceCutoff;
            if (silent != suspended)
            {
                suspended = silent;
                if (silent)
                {
                    MelonLogger.Msg($"No server traffic for {ServerSilenceCutoff:0} s: voice is idle");
                    relay?.Dispose();
                    relay = null;
                    session = null;
                }
                else
                {
                    MelonLogger.Msg("Server traffic resumed");
                    nextSessionRequest = 0;
                    RequestSession();
                }
            }
            if (serverConnection.IsApproved && approvedAt == 0)
                approvedAt = now;
            if (!suspended && session == null && approvedAt > 0 && now - approvedAt > SessionWait)
                RequestSession();
            if (relay != null)
            {
                bool changed = relay.Tick(now, out bool wantsSession, out bool newSpurt);
                if (wantsSession)
                    RequestSession();
                if (newSpurt)
                {
                    capture?.NewTalkspurt();
                    TestMode.NewTalkspurt();
                }
                VoicePath p = relay.Path;
                if (changed && p != VoicePath.Joining)
                {
                    MelonLogger.Msg(p == VoicePath.Relay ? $"Voice on the relay {relay.EndPoint}" : "The relay doesn't answer: voice goes through the game connection");
                    SendMode(p == VoicePath.Fallback);
                }
            }
            UpdateState();
            UpdateCapture();
            UpdateRemotes();
            TestMode.Update(remotes);
        }

        private static void UpdateState()
        {
            if (session == null || suspended || !serverConnection.IsApproved)
                return;
            byte flags = TestMode.ForcedFlags ?? LocalFlags;
            if (flags == sentFlags)
                return;
            sentFlags = flags;
            SendGame(Wire.StateMessage, stream => Wire.SerializeState(stream, ref flags), reliableChannel);
        }

        private static byte LocalFlags => (byte)((intendedMuted || moderatorMuted ? Wire.FlagMuted : 0) | (capture?.StartFailure != null ? Wire.FlagNoMic : 0));

        internal static bool LocalAnchored => session != null && PlayerStatus.AnchorOf(session.SenderId, out _);

        private static bool CanSpeakHere()
        {
            PlayerController controller = Player.Current?.PlayerController;
            if (controller == null)
                return false;
            return !(controller is CustomizationPlayerController) || LocalAnchored;
        }

        internal static bool SourcePosition(int userId, out Vector3 at, out Quaternion rotation)
        {
            rotation = Quaternion.identity;
            if (!PlayerStatus.AnchorOf(userId, out at))
            {
                if (!Player.SafeGetPlayer(userId, out Player player) || player.PlayerController == null || player.PlayerController.Head == null)
                    return false;
                Transform head = player.PlayerController.Head;
                at = head.position;
                rotation = head.rotation;
            }
            at = Heard(at);
            return true;
        }

        private static Vector3 Heard(Vector3 at)
        {
            if (session == null || !PlayerStatus.AnchorOf(session.SenderId, out Vector3 mine))
                return at;
            PlayerController controller = Player.Current?.PlayerController;
            Transform head = controller != null ? controller.Head : null;
            return head == null ? at : at - mine + head.position;
        }

        internal static bool IsSpeaking(int userId) => remotes.TryGetValue(userId, out RemoteVoice voice) && voice.IsSpeaking && voiceChat.IsAudible(userId);

        private static void UpdateCapture()
        {
            bool canSend = session != null && !suspended && serverConnection.IsApproved && !intendedMuted && !moderatorMuted
                && Path != VoicePath.Joining && CanSpeakHere();
            if (TestMode.Active)
            {
                TestMode.UpdateTone(canSend, Send);
                return;
            }
            if (capture == null)
                return;
            if (session == null || suspended || !serverConnection.IsApproved || intendedMuted || moderatorMuted)
            {
                capture.Stop();
                return;
            }
            capture.Start();
            if (canSend)
                capture.Update(Send);
            else
                capture.Discard();
        }

        private static void UpdateRemotes()
        {
            var listenerList = AkAudioListener.DefaultListeners.ListenerList;
            AkAudioListener currentListener = listenerList.Count > 0 ? listenerList[0] : null;
            if (currentListener == null)
            {
                listenerSetEmptied = true;
            }
            else
            {
                if ((listenerSetEmptied || !ReferenceEquals(currentListener, boundListener)) && hasBoundListener && !remotes.IsEmpty)
                {
                    foreach (KeyValuePair<int, RemoteVoice> pair in remotes)
                        pair.Value.RebindPlayback();
                    MelonLogger.Msg($"Audio listener re-registered, rebinding {remotes.Count} voice emitter(s)");
                }
                listenerSetEmptied = false;
                boundListener = currentListener;
                hasBoundListener = true;
            }
            foreach (KeyValuePair<int, RemoteVoice> pair in remotes)
            {
                try
                {
                    pair.Value.Update(!voiceChat.IsAudible(pair.Key));
                }
                catch (Exception exception)
                {
                    if (Clock.Now >= nextRemoteFaultLog)
                    {
                        nextRemoteFaultLog = Clock.Now + 10.0;
                        MelonLogger.Warning($"Voice update for user {pair.Key} failed: {exception.Message}");
                    }
                }
            }
        }

        private static void Send(VoicePacket packet)
        {
            if (relay != null && relay.Path == VoicePath.Relay)
                relay.SendVoice(packet);
            else if (relay == null || relay.Path == VoicePath.Fallback)
                SendGame(Wire.GameVoiceUpMessage, stream => VoiceProtocol.WriteClient(stream, packet), channel);
            else
                return;
            VoiceDiagnostics.Sent(packet.Payload.Length);
            TestMode.Sent(relay != null && relay.Path == VoicePath.Relay);
        }

        internal static void SetMuted(bool value) => intendedMuted = value;
        internal static void ToggleMuted() => intendedMuted = !intendedMuted;
        internal static void SetModeratorMuted(bool value) => moderatorMuted = value;
        internal static void SetMicrophone(string value)
        {
            savedMicrophone = string.IsNullOrEmpty(value) ? null : value;
            capture?.SetDevice(savedMicrophone);
        }
        internal static string GetMicrophone() => capture?.SelectedDevice ?? savedMicrophone ?? "";
        internal static string SavedMicrophoneDescription
        {
            get
            {
                if (!PlayerPrefs.HasKey(SavedMicrophoneKey)) return "Windows default (not overridden)";
                string saved = PlayerPrefs.GetString(SavedMicrophoneKey, "");
                return string.IsNullOrEmpty(saved) ? "Windows default" : saved;
            }
        }
        internal static void SaveMicrophone()
        {
            PlayerPrefs.SetString(SavedMicrophoneKey, GetMicrophone());
            PlayerPrefs.Save();
        }
        internal static void UseWindowsDefaultMicrophone()
        {
            SetMicrophone("");
            PlayerPrefs.DeleteKey(SavedMicrophoneKey);
            PlayerPrefs.Save();
        }
        internal static float GetAudioEnergy(int userId) => remotes.TryGetValue(userId, out RemoteVoice voice) ? voice.Energy : 0f;
        internal static bool IsConnected => serverConnection != null && serverConnection.IsApproved;
        internal static bool IsModeratorMuted => moderatorMuted;
        internal static bool IsCapturing => capture?.IsRunning ?? false;
        internal static string CaptureFailure => capture?.StartFailure;
        internal static float LocalEnergy => capture?.LocalEnergy ?? 0f;
        internal static float OutputGain => outputGain;
        internal static void SetOutputGain(float value) => outputGain = Mathf.Clamp(value, 0f, 4f);
        internal static void SaveOutputGain()
        {
            PlayerPrefs.SetFloat(SavedOutputGainKey, outputGain);
            PlayerPrefs.Save();
        }
        internal static int RemoteCount => remotes.Count;
        internal static string PathDescription
        {
            get
            {
                if (serverConnection == null) return "idle";
                if (suspended) return "idle (server silent)";
                if (session == null) return "waiting for a session";
                if (relay == null) return "in-game";
                return relay.Path == VoicePath.Relay ? $"relay {relay.EndPoint}" : relay.Path == VoicePath.Joining ? "joining the relay" : "in-game (relay unreachable)";
            }
        }
        internal static bool IsBankLoaded => ModAudioBank.Loaded;
        internal static string[] GetRemoteDebugLines()
        {
            Transform localHead = Player.Current != null && Player.Current.PlayerController != null ? Player.Current.PlayerController.Head : null;
            return remotes.Values.Select(remote =>
            {
                string name = Player.SafeGetPlayer(remote.UserId, out Player player) ? player.UserInfo.Username : "not spawned";
                string distance = localHead != null && remote.HasEmitter ? Vector3.Distance(localHead.position, remote.EmitterPosition).ToString("0.00") + "m" : "n/a";
                return $"{name} ({remote.UserId}) | {(remote.IsPlaying ? "playing" : "waiting")} | level {remote.Energy:0.0000} | buffer {remote.BufferedMilliseconds:0}/{remote.TargetMilliseconds:0} ms | distance {distance} | PLC {remote.PlcFrames} FEC {remote.FecFrames} | drops {remote.LatencyDrops}";
            }).ToArray();
        }
        internal static void ClearMutedAudio()
        {
            foreach (KeyValuePair<int, RemoteVoice> pair in remotes)
                if (!voiceChat.IsAudible(pair.Key)) pair.Value.ClearAudio();
        }

        private static void ResetClient()
        {
            capture?.Stop();
            relay?.Dispose();
            relay = null;
            session = null;
            serverConnection = null;
            approvedAt = 0;
            nextSessionRequest = 0;
            suspended = false;
            sentFlags = 0xFF;
            PlayerStatus.Clear();
            foreach (RemoteVoice remote in remotes.Values) remote.Dispose();
            remotes.Clear();
        }

        internal static void Shutdown()
        {
            ResetClient();
            ApplicationManager.GameReload -= ResetClient;
        }
    }
}
