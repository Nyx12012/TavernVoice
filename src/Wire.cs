using System;
using System.Collections.Generic;
using Alta.Serialization;
using UnityEngine;

namespace TavernVoice
{
    internal static class Wire
    {
        internal const byte Version = 2;
        internal const byte Hello = 1;
        internal const byte Bound = 2;
        internal const byte Unknown = 3;
        internal const byte VoiceUp = 4;
        internal const byte VoiceDown = 5;
        internal const byte Bye = 6;
        internal const byte Roster = 16;
        internal const byte Pos = 17;
        internal const byte Quit = 18;
        internal const byte TapList = 19;
        internal const byte Tap = 20;
        internal const byte Range = 21;

        internal const int GameMessage = 36;
        internal const int CvcMessage = 30;
        internal const int SessionMessage = 0;
        internal const int SessionRequestMessage = 1;
        internal const int GameVoiceUpMessage = 2;
        internal const int GameVoiceDownMessage = 3;
        internal const int ModeMessage = 4;
        internal const int StateMessage = 5;
        internal const int StatusMessage = 6;

        internal const byte FlagMuted = 1;
        internal const byte FlagNoMic = 2;
        internal const byte FlagAnchored = 4;
        internal const int MaximumStatusEntries = 1024;

        internal const int MaximumPayload = 512;
        internal const int VoiceHeader = 12;
        internal const int TokenLength = 16;

        internal static int Put16(byte[] b, int at, int v)
        {
            b[at] = (byte)v;
            b[at + 1] = (byte)(v >> 8);
            return at + 2;
        }

        internal static int Put32(byte[] b, int at, uint v)
        {
            b[at] = (byte)v;
            b[at + 1] = (byte)(v >> 8);
            b[at + 2] = (byte)(v >> 16);
            b[at + 3] = (byte)(v >> 24);
            return at + 4;
        }

        internal static ushort Get16(byte[] b, int at) => (ushort)(b[at] | b[at + 1] << 8);

        internal static uint Get32(byte[] b, int at) => (uint)(b[at] | b[at + 1] << 8 | b[at + 2] << 16 | b[at + 3] << 24);

        internal static byte[] HelloPacket(byte[] token)
        {
            var b = new byte[2 + TokenLength];
            b[0] = Version;
            b[1] = Hello;
            Buffer.BlockCopy(token, 0, b, 2, TokenLength);
            return b;
        }

        internal static byte[] ByePacket(uint tag)
        {
            var b = new byte[6];
            b[0] = Version;
            b[1] = Bye;
            Put32(b, 2, tag);
            return b;
        }

        internal static int WriteVoice(byte[] b, byte type, uint id, VoicePacket p)
        {
            b[0] = Version;
            b[1] = type;
            int at = Put32(b, 2, id);
            at = Put16(b, at, p.Talkspurt);
            at = Put16(b, at, p.Sequence);
            at = Put16(b, at, p.Payload.Length);
            Buffer.BlockCopy(p.Payload, 0, b, at, p.Payload.Length);
            return at + p.Payload.Length;
        }

        internal static bool ReadVoice(byte[] b, int offset, int count, out VoicePacket p)
        {
            p = null;
            if (count < VoiceHeader + 1)
                return false;
            int length = Get16(b, offset + 10);
            if (length == 0 || length > MaximumPayload || count != VoiceHeader + length)
                return false;
            var payload = new byte[length];
            Buffer.BlockCopy(b, offset + VoiceHeader, payload, 0, length);
            p = new VoicePacket
            {
                SenderId = (int)Get32(b, offset + 2),
                Talkspurt = Get16(b, offset + 6),
                Sequence = Get16(b, offset + 8),
                Payload = payload
            };
            return true;
        }

        private static byte[] Control(byte type, byte[] key, int count, int itemSize)
        {
            var b = new byte[2 + 16 + 2 + count * itemSize];
            b[0] = Version;
            b[1] = type;
            Buffer.BlockCopy(key, 0, b, 2, 16);
            Put16(b, 18, count);
            return b;
        }

        internal static byte[] RosterPacket(byte[] key, List<RosterEntry> entries)
        {
            byte[] b = Control(Roster, key, entries.Count, 24);
            int at = 20;
            foreach (RosterEntry e in entries)
            {
                Buffer.BlockCopy(e.Token, 0, b, at, TokenLength);
                at = Put32(b, at + TokenLength, e.Tag);
                at = Put32(b, at, (uint)e.SenderId);
            }
            return b;
        }

        internal static byte[] PosPacket(byte[] key, List<KeyValuePair<int, Vector3>> positions)
        {
            byte[] b = Control(Pos, key, positions.Count, 16);
            int at = 20;
            foreach (KeyValuePair<int, Vector3> p in positions)
            {
                at = Put32(b, at, (uint)p.Key);
                at = Put32(b, at, FloatBits(p.Value.x));
                at = Put32(b, at, FloatBits(p.Value.y));
                at = Put32(b, at, FloatBits(p.Value.z));
            }
            return b;
        }

        internal static byte[] TapListPacket(byte[] key, List<int> senders)
        {
            byte[] b = Control(TapList, key, senders.Count, 4);
            int at = 20;
            foreach (int id in senders)
                at = Put32(b, at, (uint)id);
            return b;
        }

        internal static byte[] RangePacket(byte[] key, float meters)
        {
            byte[] b = Control(Range, key, 1, 4);
            Put32(b, 20, FloatBits(meters));
            return b;
        }

        internal static bool ReadTap(byte[] b, int count, byte[] key, out VoicePacket p)
        {
            p = null;
            if (count < 2 + 16 + VoiceHeader - 2 + 1 || b[0] != Version || b[1] != Tap)
                return false;
            int diff = 0;
            for (int i = 0; i < 16; i++)
                diff |= b[2 + i] ^ key[i];
            return diff == 0 && ReadVoice(b, 16, count - 16, out p);
        }

        private static uint FloatBits(float f) => BitConverter.ToUInt32(BitConverter.GetBytes(f), 0);

        internal sealed class RosterEntry
        {
            internal byte[] Token;
            internal uint Tag;
            internal int SenderId;
        }

        internal sealed class StatusEntry
        {
            internal int SenderId;
            internal byte Flags;
            internal Vector3 Anchor;
        }

        internal sealed class SessionInfo
        {
            internal int WireVersion;
            internal int RelayPort;
            internal byte[] Token;
            internal uint Tag;
            internal int SenderId;
        }

        internal static int ReadSubtype(Stream stream)
        {
            int subtype = 0;
            stream.SerializeIntegerBits(ref subtype, 8);
            return subtype;
        }

        internal static void WriteSubtype(Stream stream, int subtype) => stream.SerializeIntegerBits(ref subtype, 8);

        internal static void SerializeSession(Stream stream, SessionInfo s)
        {
            int version = s.WireVersion, port = s.RelayPort, tag = (int)s.Tag, sender = s.SenderId;
            stream.SerializeIntegerBits(ref version, 8);
            stream.SerializeIntegerBits(ref port, 16);
            if (stream.IsReading)
                s.Token = new byte[TokenLength];
            stream.AlignAndSerializeBytes(s.Token, TokenLength);
            stream.SerializeIntegerBits(ref tag, 32);
            stream.SerializeIntegerBits(ref sender, 32);
            s.WireVersion = version;
            s.RelayPort = port;
            s.Tag = (uint)tag;
            s.SenderId = sender;
        }

        internal static void SerializeState(Stream stream, ref byte flags)
        {
            int value = flags;
            stream.SerializeIntegerBits(ref value, 8);
            flags = (byte)(value & (FlagMuted | FlagNoMic));
        }

        internal static void SerializeStatus(Stream stream, List<StatusEntry> entries)
        {
            int count = entries.Count;
            stream.SerializeIntegerBits(ref count, 16);
            if (count > MaximumStatusEntries)
                throw new InvalidOperationException("status list too long");
            for (int i = 0; i < count; i++)
            {
                StatusEntry e = stream.IsReading ? new StatusEntry() : entries[i];
                int sender = e.SenderId, flags = e.Flags;
                stream.SerializeIntegerBits(ref sender, 32);
                stream.SerializeIntegerBits(ref flags, 8);
                e.SenderId = sender;
                e.Flags = (byte)flags;
                if ((e.Flags & FlagAnchored) != 0)
                {
                    float x = e.Anchor.x, y = e.Anchor.y, z = e.Anchor.z;
                    stream.SerializeFloat(ref x);
                    stream.SerializeFloat(ref y);
                    stream.SerializeFloat(ref z);
                    e.Anchor = new Vector3(x, y, z);
                }
                if (stream.IsReading)
                    entries.Add(e);
            }
        }

        internal static void SerializeMode(Stream stream, ref bool fallback)
        {
            int value = fallback ? 1 : 0;
            stream.SerializeIntegerBits(ref value, 8);
            fallback = value != 0;
        }
    }
}
