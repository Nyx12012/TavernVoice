using System;
using System.Collections.Generic;
using UnityEngine;

namespace TavernVoice
{
    internal static class InGameRelay
    {
        private static double nextFaultLog;

        internal static Vector3? Head(Session session)
        {
            try
            {
                Player player = session.Player;
                if (player == null || player.PlayerController == null)
                    return null;
                Transform head = player.PlayerController.Head;
                return head == null ? (Vector3?)null : head.position;
            }
            catch (Exception)
            {
                return null;
            }
        }

        internal static void FromClient(Session sender, VoicePacket packet)
        {
            if (packet.Payload.Length == 0 || sender.Check.Accept(packet.Talkspurt, packet.Sequence, Clock.Now) >= Verdict.OverLimit)
            {
                VoiceDiagnostics.InvalidDrop();
                return;
            }
            packet.SenderId = sender.SenderId;
            if (TestMode.Active)
                ServerVoice.SendVoiceDown(sender, new VoicePacket { SenderId = TestMode.EchoSender, Talkspurt = packet.Talkspurt, Sequence = packet.Sequence, Payload = packet.Payload });
            Vector3? from = ServerVoice.VoicePosition(sender);
            if (from == null)
                return;
            Forward(sender.SenderId, from.Value, packet, false);
        }

        internal static void FromTap(VoicePacket packet)
        {
            if (!Settings.InGameFallback)
                return;
            Vector3? from = ServerVoice.PositionOf(packet.SenderId);
            if (from == null)
                return;
            Forward(packet.SenderId, from.Value, packet, true);
        }

        private static void Forward(int senderId, Vector3 from, VoicePacket packet, bool fallbackOnly)
        {
            List<Session> recipients = ServerVoice.Sessions;
            for (int i = 0; i < recipients.Count; i++)
            {
                Session recipient = recipients[i];
                try
                {
                    if (recipient.SenderId == senderId || (fallbackOnly && !recipient.Fallback))
                        continue;
                    Vector3? at = ServerVoice.VoicePosition(recipient);
                    if (at == null || (at.Value - from).sqrMagnitude > ServerVoice.RangeSquared)
                    {
                        VoiceDiagnostics.RangeDrop();
                        continue;
                    }
                    ServerVoice.SendVoiceDown(recipient, packet);
                    VoiceDiagnostics.Relayed(packet.Payload.Length);
                }
                catch (Exception exception)
                {
                    if (Clock.Now >= nextFaultLog)
                    {
                        nextFaultLog = Clock.Now + 10.0;
                        MelonLoader.MelonLogger.Warning($"In-game voice to a player failed: {exception.Message}");
                    }
                }
            }
        }
    }
}
