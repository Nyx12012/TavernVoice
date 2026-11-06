using System;
using System.Collections.Generic;
using System.Reflection;
using Alta.Networking;
using HarmonyLib;
using MelonLoader;

namespace TavernVoice
{
    internal static class Settings
    {
        private static MelonPreferences_Category category;
        private static MelonPreferences_Entry<int> relayPort;
        private static MelonPreferences_Entry<bool> relayEnabled;
        private static MelonPreferences_Entry<float> rangeMeters;
        private static MelonPreferences_Entry<int> positionRateHz;
        private static MelonPreferences_Entry<bool> inGameFallback;
        private static MelonPreferences_Entry<bool> cvcBridge;
        private static MelonPreferences_Entry<bool> forceInGame;

        internal static int RelayPort => Math.Max(1, Math.Min(65535, relayPort.Value));
        internal static bool RelayEnabled => relayEnabled.Value;
        internal static float RangeMeters => Math.Max(1f, rangeMeters.Value);
        internal static int PositionRateHz => Math.Max(1, Math.Min(20, positionRateHz.Value));
        internal static bool InGameFallback => inGameFallback.Value;
        internal static bool CvcBridge => cvcBridge.Value;
        internal static bool ForceInGame => forceInGame.Value;

        internal static void SetRelayEnabled(bool value) => Save(relayEnabled, value);
        internal static void SetRangeMeters(float value) => Save(rangeMeters, value);
        internal static void SetPositionRateHz(int value) => Save(positionRateHz, value);
        internal static void SetInGameFallback(bool value) => Save(inGameFallback, value);
        internal static void SetCvcBridge(bool value) => Save(cvcBridge, value);

        private static void Save<T>(MelonPreferences_Entry<T> entry, T value)
        {
            entry.Value = value;
            category.SaveToFile(false);
        }

        internal static void Load()
        {
            MelonPreferences_Category c = category = MelonPreferences.CreateCategory("TavernVoice");
            relayPort = c.CreateEntry("RelayPort", 1759, description: "Server: UDP port of the voice relay. Players need it open (forwarded) to get lag proof voice.");
            if (relayPort.Value == 1770)
                relayPort.Value = 1759;
            relayEnabled = c.CreateEntry("RelayEnabled", true, description: "Server: start the voice relay next to the server.");
            rangeMeters = c.CreateEntry("RangeMeters", 50f, description: "Server: how far a voice carries, in meters.");
            positionRateHz = c.CreateEntry("PositionRateHz", 4, description: "Server: how often player positions go to the relay.");
            inGameFallback = c.CreateEntry("InGameFallback", true, description: "Server: carry voice over the game connection for players who can't reach the relay.");
            cvcBridge = c.CreateEntry("CvcBridge", true, description: "Server: let players still on Circuit's Voice Chat talk with TavernVoice players, through the game connection.");
            forceInGame = c.CreateEntry("ForceInGame", false, description: "Client: never use the relay (for testing).");
            c.SaveToFile(false);
        }
    }

    internal static class MessageTypes
    {
        internal static bool Wide(out string detail)
        {
            try
            {
                Type reader = AccessTools.TypeByName("Alta.Networking.Internal.MessageProcessor");
                Type writer = AccessTools.TypeByName("Alta.Networking.Internal.OutgoingPacketManager");
                MethodBase read = reader == null ? null : AccessTools.Method(reader, "ProcessSingleMessageFromData",
                    new[] { typeof(Connection), typeof(ArraySegment<byte>), typeof(ConnectionChannel), typeof(MessageType).MakeByRefType() });
                MethodBase write = writer == null ? null : AccessTools.Method(writer, "StartSerialize",
                    new[] { typeof(Connection), typeof(MessageType) });
                if (read == null || write == null)
                {
                    detail = "the game's message " + (read == null ? "reader" : "writer") + " was not found";
                    return false;
                }
                string r = Owners(read), w = Owners(write);
                if (r == null || w == null)
                {
                    detail = "the 8 bit message type patch (TavernLib) is missing on the " + (r == null ? "reading" : "writing") + " side";
                    return false;
                }
                detail = "type patch: " + (r == w ? r : r + " / " + w);
                return true;
            }
            catch (Exception e)
            {
                detail = "the message type check failed: " + e.Message;
                return false;
            }
        }

        private static string Owners(MethodBase m)
        {
            Patches p = HarmonyLib.Harmony.GetPatchInfo(m);
            if (p == null || p.Prefixes.Count == 0)
                return null;
            var owners = new List<string>();
            foreach (Patch x in p.Prefixes)
                if (!owners.Contains(x.owner))
                    owners.Add(x.owner);
            return string.Join(", ", owners.ToArray());
        }
    }
}
