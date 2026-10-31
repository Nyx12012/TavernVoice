using Alta.Serialization;

namespace TavernVoice
{
    internal sealed class VoicePacket
    {
        internal int SenderId;
        internal ushort Talkspurt;
        internal ushort Sequence;
        internal byte[] Payload;
    }

    internal static class VoiceProtocol
    {
        internal const int MaximumPayload = 512;

        internal static VoicePacket ReadClient(Stream stream)
        {
            var packet = new VoicePacket();
            SerializeBody(stream, packet);
            return packet;
        }

        internal static VoicePacket ReadServer(Stream stream)
        {
            var packet = new VoicePacket();
            stream.SerializeIntegerBits(ref packet.SenderId, 32);
            SerializeBody(stream, packet);
            return packet;
        }

        internal static void WriteClient(Stream stream, VoicePacket packet) => SerializeBody(stream, packet);

        internal static void WriteServer(Stream stream, VoicePacket packet)
        {
            stream.SerializeIntegerBits(ref packet.SenderId, 32);
            SerializeBody(stream, packet);
        }

        private static void SerializeBody(Stream stream, VoicePacket packet)
        {
            int talkspurt = packet.Talkspurt;
            int sequence = packet.Sequence;
            int length = stream.IsWriting ? packet.Payload.Length : 0;
            stream.SerializeIntegerBits(ref talkspurt, 16);
            stream.SerializeIntegerBits(ref sequence, 16);
            stream.SerializeInteger(ref length, 0, MaximumPayload);
            packet.Talkspurt = (ushort)talkspurt;
            packet.Sequence = (ushort)sequence;
            if (stream.IsReading)
                packet.Payload = new byte[length];
            stream.AlignAndSerializeBytes(packet.Payload, length);
        }
    }
}
