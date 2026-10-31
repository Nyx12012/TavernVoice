using System.Collections.Generic;
using Alta.Networking;
using Alta.Networking.Internal;
using HarmonyLib;

namespace TavernVoice
{

    internal static class ChannelFlush
    {
        private static readonly AccessTools.FieldRef<Connection, Dictionary<ConnectionChannel, OutgoingPacketManager>> ManagerMap =
            AccessTools.FieldRefAccess<Connection, Dictionary<ConnectionChannel, OutgoingPacketManager>>("packetManagerMap");

        internal static void Flush(Connection connection, ConnectionChannel channel)
        {
            if (connection == null || !connection.IsApproved)
                return;
            if (ManagerMap(connection).TryGetValue(channel, out OutgoingPacketManager manager))
                manager.SendAll();
        }
    }
}
