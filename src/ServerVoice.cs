using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Alta.Global;
using Alta.Networking;
using Alta.Networking.Servers;
using Alta.PlayerStates;
using Alta.PlayerStates.Transitions;
using HarmonyLib;
using MelonLoader;
using UnityEngine;
using AltaStream = Alta.Serialization.Stream;

namespace TavernVoice
{
    internal sealed class Session
    {
        internal Connection Connection;
        internal Player Player;
        internal byte[] Token;
        internal uint Tag;
        internal int SenderId;
        internal bool Fallback;
        internal bool Cvc;
        internal byte ClientFlags;
        internal Vector3? Anchor;
        internal bool AnchorEntered;
        internal double AnchorSince;
        internal Vector3? LastHead;
        internal readonly SpurtCheck Check = new SpurtCheck(Clock.Now);
    }

    internal static class ServerVoice
    {
        private const double AnchorGiveUpSeconds = 15.0;
        private static readonly Dictionary<Connection, Session> byConnection = new Dictionary<Connection, Session>();
        private static readonly List<Session> snapshot = new List<Session>();
        private static readonly RandomNumberGenerator random = RandomNumberGenerator.Create();
        private static PropertyInfo transitionPlayer;
        private static ConnectionChannel voiceChannel;
        private static ConnectionChannel reliableChannel;
        private static bool started;
        private static bool wide;
        private static bool statusDirty;
        private static double nextStatus;
        private static double nextTick;
        private static Session testPeer;
        private static Vector3? testPeerAt;
        private static byte testPeerFlags;
        private static double nextTestCheck;
        private static string lastCounts;

        internal static List<Session> Sessions
        {
            get
            {
                snapshot.Clear();
                snapshot.AddRange(byConnection.Values);
                return snapshot;
            }
        }
        internal static float RangeSquared => Settings.RangeMeters * Settings.RangeMeters;

        internal static bool IsServerProcess
        {
            get
            {
                foreach (string arg in Environment.GetCommandLineArgs())
                    if (string.Equals(arg, "/start_server", StringComparison.OrdinalIgnoreCase))
                        return true;
                return false;
            }
        }

        internal static void Initialize(HarmonyLib.Harmony harmony)
        {
            harmony.Patch(AccessTools.Method(typeof(ServerHandler), "ConnectionCreated"),
                postfix: new HarmonyMethod(typeof(ServerVoice), nameof(ConnectionCreated)));
            try
            {
                transitionPlayer = AccessTools.Property(typeof(PlayingToCustomizationTransition), "Player");
                harmony.Patch(AccessTools.Method(typeof(PlayingToCustomizationTransition), "TransitionToDestinationState"),
                    prefix: new HarmonyMethod(typeof(ServerVoice), nameof(EnteringCustomization)));
            }
            catch (Exception exception)
            {
                MelonLogger.Warning($"The customization anchor is off: {exception.Message}");
            }
            ServerCommands.Register();
            ServerHandler.Initialized += OnInitialized;
            if (ServerHandler.Current != null)
                OnInitialized(ServerHandler.Current);
        }

        private static void OnInitialized(ServerHandler handler)
        {
            if (started)
                return;
            started = true;
            wide = MessageTypes.Wide(out string detail);
            if (wide)
                MelonLogger.Msg($"Message type {Wire.GameMessage} ready ({detail})");
            else
                MelonLogger.Warning($"{detail}, so message type {Wire.GameMessage} does not fit: no sessions and no in-game fallback on this server");
            handler.PlayerJoined += PlayerJoined;
            handler.PlayerLeft += PlayerLeft;
            if (wide)
                RelayHost.Start();
        }

        internal static bool Running => started && wide;

        private static void ResolveChannels()
        {
            if (voiceChannel != null)
                return;
            ConnectionChannel[] channels = GlobalSettings<NetworkingSettings>.Instance.Channels;
            voiceChannel = channels.Where(c => !c.IsReliable).OrderBy(c => c.MaximumSendInterval).First();
            reliableChannel = channels.Where(c => c.IsReliable).OrderBy(c => c.MaximumSendInterval).FirstOrDefault() ?? voiceChannel;
        }

        private static void ConnectionCreated(Connection connection)
        {
            if (!started || !wide)
                return;
            ResolveChannels();
            connection.SetHandler((MessageType)Wire.GameMessage, Receive);
            connection.SetHandler((MessageType)Wire.CvcMessage, ReceiveCvc);
            connection.Disconnected += Disconnected;
        }

        private static void PlayerJoined(Player player)
        {
            try
            {
                Connection connection = player.ConnectionToRemotePlayer;
                if (connection == null || !wide)
                    return;
                Session session = Open(connection, player);
                if (session == null)
                    return;
                SendSession(session);
                if (TestMode.Active && testPeer == null)
                    OpenTestPeer();
            }
            catch (Exception exception)
            {
                MelonLogger.Warning($"Voice session for a joining player failed: {exception.Message}");
            }
        }

        private static Session Open(Connection connection, Player player)
        {
            if (byConnection.TryGetValue(connection, out Session existing))
            {
                existing.Player = player ?? existing.Player;
                return existing;
            }
            int senderId = connection.UserInfo.Identifier;
            if (BySender(senderId) != null)
            {
                MelonLogger.Warning($"A second connection claims user id {senderId}: no voice session for it");
                return null;
            }
            var session = new Session
            {
                Connection = connection,
                Player = player,
                Token = RandomBytes(Wire.TokenLength),
                Tag = BitConverter.ToUInt32(RandomBytes(4), 0) & 0x7FFFFFFF,
                SenderId = senderId
            };
            byConnection[connection] = session;
            RelayHost.RosterChanged();
            statusDirty = true;
            return session;
        }

        private static void PlayerLeft(Player player)
        {
            Connection connection = player?.ConnectionToRemotePlayer;
            if (connection != null)
                Close(connection);
        }

        private static void Disconnected(Connection connection)
        {
            connection.Disconnected -= Disconnected;
            Close(connection);
        }

        private static void Close(Connection connection)
        {
            if (byConnection.Remove(connection))
            {
                RelayHost.RosterChanged();
                statusDirty = true;
            }
        }

        private static byte[] RandomBytes(int count)
        {
            var b = new byte[count];
            lock (random)
                random.GetBytes(b);
            return b;
        }

        internal static Session BySender(int senderId)
        {
            foreach (Session s in byConnection.Values)
                if (s.SenderId == senderId)
                    return s;
            return null;
        }

        internal static Vector3? VoicePosition(Session session) => session.Anchor ?? InGameRelay.Head(session);

        internal static Vector3? PositionOf(int senderId)
        {
            if (testPeer != null && senderId == testPeer.SenderId)
                return testPeerAt;
            Session session = BySender(senderId);
            return session == null ? null : VoicePosition(session);
        }

        private static void EnteringCustomization(object __instance)
        {
            try
            {
                if (!(transitionPlayer?.GetValue(__instance, null) is Player player))
                    return;
                foreach (Session s in byConnection.Values)
                {
                    if (s.Player != player && s.Connection != player.ConnectionToRemotePlayer)
                        continue;
                    Vector3? at = InGameRelay.Head(s) ?? s.LastHead;
                    if (at == null)
                    {
                        MelonLogger.Msg($"Player {s.SenderId} entered customization with no known position: no voice anchor");
                        return;
                    }
                    s.Anchor = at;
                    s.AnchorEntered = false;
                    s.AnchorSince = Clock.Now;
                    statusDirty = true;
                    MelonLogger.Msg($"Player {s.SenderId} entered customization: voice stays at {Format(at.Value)}");
                    return;
                }
            }
            catch (Exception exception)
            {
                MelonLogger.Warning($"Voice anchor for customization failed: {exception.Message}");
            }
        }

        private static void TickPlayers(double now)
        {
            foreach (Session s in byConnection.Values)
            {
                Vector3? head = InGameRelay.Head(s);
                if (head != null)
                    s.LastHead = head;
                if (s.Anchor == null)
                    continue;
                PlayerStateType state = PlayerStateType.None;
                try
                {
                    state = s.Player?.StateManager?.CurrentStateType ?? PlayerStateType.None;
                }
                catch (Exception)
                {
                }
                if (state == PlayerStateType.Customization)
                {
                    s.AnchorEntered = true;
                    continue;
                }
                if (!s.AnchorEntered && now - s.AnchorSince < AnchorGiveUpSeconds)
                    continue;
                s.Anchor = null;
                statusDirty = true;
                MelonLogger.Msg($"Player {s.SenderId} left customization: voice follows them again");
            }
            if (testPeer != null && testPeerAt == null)
                PlaceTestPeer(1f);
        }

        internal static List<Wire.RosterEntry> Roster()
        {
            var list = new List<Wire.RosterEntry>();
            foreach (Session s in byConnection.Values)
                list.Add(new Wire.RosterEntry { Token = s.Token, Tag = s.Tag, SenderId = s.SenderId });
            if (testPeer != null)
                list.Add(new Wire.RosterEntry { Token = testPeer.Token, Tag = testPeer.Tag, SenderId = testPeer.SenderId });
            return list;
        }

        internal static List<KeyValuePair<int, Vector3>> Positions()
        {
            var list = new List<KeyValuePair<int, Vector3>>();
            foreach (Session s in byConnection.Values)
            {
                Vector3? at = VoicePosition(s);
                if (at != null)
                    list.Add(new KeyValuePair<int, Vector3>(s.SenderId, at.Value));
            }
            if (testPeer != null && testPeerAt != null)
                list.Add(new KeyValuePair<int, Vector3>(testPeer.SenderId, testPeerAt.Value));
            return list;
        }

        internal static List<int> FallbackSenders()
        {
            var list = new List<int>();
            foreach (Session s in byConnection.Values)
                if (s.Fallback)
                    list.Add(s.SenderId);
            return list;
        }

        private static List<Wire.StatusEntry> StatusList()
        {
            var list = new List<Wire.StatusEntry>();
            foreach (Session s in byConnection.Values)
            {
                var e = new Wire.StatusEntry { SenderId = s.SenderId, Flags = s.ClientFlags };
                if (s.Anchor != null)
                {
                    e.Flags |= Wire.FlagAnchored;
                    e.Anchor = s.Anchor.Value;
                }
                list.Add(e);
            }
            if (testPeer != null && testPeerAt != null)
                list.Add(new Wire.StatusEntry { SenderId = testPeer.SenderId, Flags = (byte)(testPeerFlags | Wire.FlagAnchored), Anchor = testPeerAt.Value });
            return list;
        }

        private static void SendStatus()
        {
            List<Wire.StatusEntry> list = StatusList();
            foreach (Session s in byConnection.Values)
            {
                if (s.Cvc)
                    continue;
                try
                {
                    s.Connection.Send(reliableChannel, (MessageType)Wire.GameMessage, (_, stream) =>
                    {
                        Wire.WriteSubtype(stream, Wire.StatusMessage);
                        Wire.SerializeStatus(stream, list);
                    });
                    ChannelFlush.Flush(s.Connection, reliableChannel);
                }
                catch (Exception exception)
                {
                    MelonLogger.Warning($"Voice status to player {s.SenderId} failed: {exception.Message}");
                }
            }
        }

        private static void SendSession(Session session)
        {
            var info = new Wire.SessionInfo
            {
                WireVersion = Wire.Version,
                RelayPort = RelayHost.Enabled ? Settings.RelayPort : 0,
                Token = session.Token,
                Tag = session.Tag,
                SenderId = session.SenderId
            };
            session.Connection.Send(voiceChannel, (MessageType)Wire.GameMessage, (_, stream) =>
            {
                Wire.WriteSubtype(stream, Wire.SessionMessage);
                Wire.SerializeSession(stream, info);
            });
            ChannelFlush.Flush(session.Connection, voiceChannel);
        }

        internal static void ResendSessions()
        {
            foreach (Session s in byConnection.Values)
            {
                if (s.Cvc)
                    continue;
                try
                {
                    SendSession(s);
                }
                catch (Exception exception)
                {
                    MelonLogger.Warning($"Voice session to player {s.SenderId} failed: {exception.Message}");
                }
            }
        }

        internal static void SendVoiceDown(Session recipient, VoicePacket packet)
        {
            if (recipient.Cvc)
            {
                recipient.Connection.Send(voiceChannel, (MessageType)Wire.CvcMessage, (_, stream) => VoiceProtocol.WriteServer(stream, packet));
            }
            else
            {
                recipient.Connection.Send(voiceChannel, (MessageType)Wire.GameMessage, (_, stream) =>
                {
                    Wire.WriteSubtype(stream, Wire.GameVoiceDownMessage);
                    VoiceProtocol.WriteServer(stream, packet);
                });
            }
            ChannelFlush.Flush(recipient.Connection, voiceChannel);
        }

        private static void ReceiveCvc(Connection connection, AltaStream stream)
        {
            if (!Settings.CvcBridge)
                return;
            VoicePacket packet;
            try
            {
                packet = VoiceProtocol.ReadClient(stream);
            }
            catch
            {
                VoiceDiagnostics.InvalidDrop();
                return;
            }
            try
            {
                byConnection.TryGetValue(connection, out Session session);
                if (session == null && connection.Player is Player player)
                    session = Open(connection, player);
                if (session == null)
                    return;
                SetCvc(session, true);
                if (Settings.InGameFallback)
                    InGameRelay.FromClient(session, packet);
            }
            catch (Exception exception)
            {
                MelonLogger.Warning($"Voice from a Circuit's Voice Chat player failed: {exception.Message}");
            }
        }

        private static void SetCvc(Session session, bool on)
        {
            if (session.Cvc == on)
                return;
            session.Cvc = on;
            session.Fallback = on;
            MelonLogger.Msg($"Player {session.SenderId} {(on ? "is on Circuit's Voice Chat: voice bridged through the game connection" : "is off the Circuit's Voice Chat bridge")}");
            RelayHost.TapListChanged();
            statusDirty = true;
        }

        internal static void SetCvcBridge(bool on)
        {
            Settings.SetCvcBridge(on);
            if (on)
                return;
            foreach (Session s in byConnection.Values.ToList())
                SetCvc(s, false);
        }

        private static void Receive(Connection connection, AltaStream stream)
        {
            int subtype = Wire.ReadSubtype(stream);
            byConnection.TryGetValue(connection, out Session session);
            if (session != null && (subtype == Wire.GameVoiceUpMessage || subtype == Wire.ModeMessage || subtype == Wire.StateMessage))
                SetCvc(session, false);
            switch (subtype)
            {
                case Wire.SessionRequestMessage:
                    try
                    {
                        if (session == null && connection.Player is Player player)
                            session = Open(connection, player);
                        if (session != null)
                            SendSession(session);
                    }
                    catch (Exception exception)
                    {
                        MelonLogger.Warning($"Voice session request failed: {exception.Message}");
                    }
                    break;
                case Wire.GameVoiceUpMessage:
                    VoicePacket packet;
                    try
                    {
                        packet = VoiceProtocol.ReadClient(stream);
                    }
                    catch
                    {
                        VoiceDiagnostics.InvalidDrop();
                        return;
                    }
                    if (session != null && Settings.InGameFallback)
                        InGameRelay.FromClient(session, packet);
                    break;
                case Wire.ModeMessage:
                    bool fallback = false;
                    Wire.SerializeMode(stream, ref fallback);
                    if (session != null && session.Fallback != fallback)
                    {
                        session.Fallback = fallback;
                        MelonLogger.Msg($"Player {session.SenderId} is on the {(fallback ? "in-game fallback" : "relay")}");
                        RelayHost.TapListChanged();
                    }
                    break;
                case Wire.StateMessage:
                    byte flags = 0;
                    Wire.SerializeState(stream, ref flags);
                    if (session != null && session.ClientFlags != flags)
                    {
                        session.ClientFlags = flags;
                        statusDirty = true;
                    }
                    break;
            }
        }

        internal static void SetRelay(bool on)
        {
            Settings.SetRelayEnabled(on);
            if (!Running)
                return;
            if (on)
                RelayHost.Start();
            else
                RelayHost.Stop();
            ResendSessions();
        }

        internal static void SetRange(float meters)
        {
            Settings.SetRangeMeters(meters);
            RelayHost.RangeChanged();
        }

        internal static string StatusText()
        {
            var text = new StringBuilder();
            text.Append($"TavernVoice {Mod.Version}: ");
            if (!Running)
                text.Append(started ? "off, message type 36 does not fit (TavernLib missing?)" : "waiting for the server to start");
            else if (!Settings.RelayEnabled)
                text.Append("relay off, voice goes through the game connection");
            else if (!RelayHost.Enabled)
                text.Append("relay stopped (a newer relay took the port, or it failed to start)");
            else
                text.Append($"relay on UDP {Settings.RelayPort}, {(RelayHost.Listening ? "listening" : "not listening yet")}, started {RelayHost.Starts} time(s)");
            text.Append($"\nrange {Settings.RangeMeters:0.#} m, positions {Settings.PositionRateHz} Hz, in-game fallback {(Settings.InGameFallback ? "on" : "off")}, Circuit's Voice Chat bridge {(Settings.CvcBridge ? "on" : "off")}");
            text.Append($"\nin-game voice: {SpurtCheck.Accepted} passed ({SpurtCheck.Late} late), {SpurtCheck.BucketDrops} over limit, {SpurtCheck.OrderDrops} out of order");
            text.Append($"\n{byConnection.Count} player(s) with voice");
            foreach (Session s in byConnection.Values)
            {
                string name = s.Player != null ? s.Player.UserInfo.Username : "?";
                text.Append($"\n  {name} ({s.SenderId}): {(s.Cvc ? "Circuit's Voice Chat" : s.Fallback ? "in-game" : "relay")}");
                if ((s.ClientFlags & Wire.FlagMuted) != 0)
                    text.Append(", muted");
                if ((s.ClientFlags & Wire.FlagNoMic) != 0)
                    text.Append(", no mic");
                if (s.Anchor != null)
                    text.Append($", in customization, voice at {Format(s.Anchor.Value)}");
            }
            return text.ToString();
        }

        private static string Format(Vector3 v) => $"{v.x:0.0} {v.y:0.0} {v.z:0.0}";

        private static void OpenTestPeer()
        {
            testPeer = new Session { Token = RandomBytes(Wire.TokenLength), Tag = BitConverter.ToUInt32(RandomBytes(4), 0) & 0x7FFFFFFF, SenderId = TestMode.PeerSender };
            File.WriteAllText(Path.Combine(TestMode.Folder, "test-peer.txt"),
                $"{BitConverter.ToString(testPeer.Token).Replace("-", "").ToLowerInvariant()} {testPeer.Tag} {testPeer.SenderId} {Settings.RelayPort}\n");
            RelayHost.RosterChanged();
            MelonLogger.Msg($"TVTEST synthetic peer {testPeer.SenderId} is in the roster");
        }

        private static void PlaceTestPeer(float meters)
        {
            foreach (Session s in byConnection.Values)
            {
                Vector3? at = VoicePosition(s);
                if (at == null)
                    continue;
                testPeerAt = at.Value + Vector3.right * meters;
                statusDirty = true;
                MelonLogger.Msg($"TVTEST synthetic peer {testPeer.SenderId} stands at {Format(testPeerAt.Value)}, {meters:0.#} m from player {s.SenderId}");
                return;
            }
        }

        private static void UpdateTest(double now)
        {
            if (now < nextTestCheck)
                return;
            nextTestCheck = now + 1.0;
            string counts = $"accepted={SpurtCheck.Accepted} late={SpurtCheck.Late} bucket={SpurtCheck.BucketDrops} order={SpurtCheck.OrderDrops}";
            if (counts != lastCounts)
            {
                lastCounts = counts;
                MelonLogger.Msg("TVTEST server in-game " + counts);
            }
            if (TestMode.Take("test-peer-here.txt", out string here) && testPeer != null)
            {
                if (!float.TryParse(here, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float meters))
                    meters = 1f;
                testPeerAt = null;
                PlaceTestPeer(meters);
            }
            if (TestMode.Take("test-peer-state.txt", out string state))
            {
                testPeerFlags = TestMode.ParseFlags(state);
                statusDirty = true;
                MelonLogger.Msg($"TVTEST synthetic peer flags {testPeerFlags}");
            }
            if (TestMode.Take("test-customize.txt", out _))
            {
                foreach (Session s in byConnection.Values)
                {
                    MelonLogger.Msg($"TVTEST sending player {s.SenderId} to customization the way the hut teleporter does");
                    PlayingToCustomizationTransition.ExternalTrigger(s.Player);
                    break;
                }
            }
            if (TestMode.Take("test-hitch.txt", out string hitch))
            {
                int.TryParse(hitch.Trim(), out int ms);
                MelonLogger.Msg($"TVTEST blocking the server's main thread for {ms} ms");
                System.Threading.Thread.Sleep(Mathf.Clamp(ms, 0, 10000));
                MelonLogger.Msg("TVTEST main thread released");
            }
        }

        internal static void Update()
        {
            if (!started)
                return;
            double now = Clock.Now;
            if (now >= nextTick)
            {
                nextTick = now + 0.25;
                TickPlayers(now);
            }
            RelayHost.Update();
            if (statusDirty && now >= nextStatus && wide)
            {
                statusDirty = false;
                nextStatus = now + 0.25;
                SendStatus();
            }
            if (TestMode.Active)
                UpdateTest(now);
        }
    }
}
