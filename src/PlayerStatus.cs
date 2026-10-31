using System.Collections.Generic;
using UnityEngine;

namespace TavernVoice
{
    internal enum StatusIcon { None, Speaking, Muted, NoMic }

    internal static class PlayerStatus
    {
        private static Dictionary<int, Wire.StatusEntry> entries = new Dictionary<int, Wire.StatusEntry>();

        internal static void Apply(List<Wire.StatusEntry> list)
        {
            var next = new Dictionary<int, Wire.StatusEntry>(list.Count);
            foreach (Wire.StatusEntry e in list)
                next[e.SenderId] = e;
            entries = next;
        }

        internal static void Clear() => entries = new Dictionary<int, Wire.StatusEntry>();

        internal static byte FlagsOf(int senderId) => entries.TryGetValue(senderId, out Wire.StatusEntry e) ? e.Flags : (byte)0;

        internal static bool AnchorOf(int senderId, out Vector3 anchor)
        {
            if (entries.TryGetValue(senderId, out Wire.StatusEntry e) && (e.Flags & Wire.FlagAnchored) != 0)
            {
                anchor = e.Anchor;
                return true;
            }
            anchor = Vector3.zero;
            return false;
        }

        internal static StatusIcon IconOf(int senderId)
        {
            byte flags = FlagsOf(senderId);
            if ((flags & Wire.FlagNoMic) != 0)
                return StatusIcon.NoMic;
            if ((flags & Wire.FlagMuted) != 0)
                return StatusIcon.Muted;
            return VoiceRuntime.IsSpeaking(senderId) ? StatusIcon.Speaking : StatusIcon.None;
        }
    }
}
