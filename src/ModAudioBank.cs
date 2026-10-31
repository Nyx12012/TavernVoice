using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using MelonLoader;

namespace TavernVoice
{

    internal static class ModAudioBank
    {
        internal const string VoiceEvent = "Play_ModVoice";
        internal const string StreamEvent = "Play_ModStream";
        private const string ResourceName = "TavernVoice.ModAudio.bnk";
        private const double RetrySeconds = 5.0;
        private static uint bankId;
        private static double nextAttempt;
        private static bool warned;

        internal static bool Loaded { get; private set; }

        internal static void EnsureLoaded()
        {
            if (Loaded || Clock.Now < nextAttempt || !AkSoundEngine.IsInitialized())
                return;
            nextAttempt = Clock.Now + RetrySeconds;
            byte[] bytes = ReadEmbedded();
            if (bytes == null)
                return;
            GCHandle handle = GCHandle.Alloc(bytes, GCHandleType.Pinned);
            try
            {
                AKRESULT result = AkSoundEngine.LoadBankMemoryCopy(handle.AddrOfPinnedObject(), (uint)bytes.Length, out bankId);
                if (result == AKRESULT.AK_Success)
                {
                    Loaded = true;
                    MelonLogger.Msg($"Loaded ModAudio soundbank (id {bankId})");
                }
                else if (!warned)
                {
                    warned = true;
                    MelonLogger.Warning($"ModAudio bank load failed: {result}, retrying every {RetrySeconds:0} s");
                }
            }
            finally
            {
                handle.Free();
            }
        }

        private static byte[] ReadEmbedded()
        {
            using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName))
            {
                if (stream == null)
                {
                    if (!warned) { warned = true; MelonLogger.Error($"Embedded bank '{ResourceName}' missing"); }
                    return null;
                }
                byte[] bytes = new byte[stream.Length];
                int read = 0;
                while (read < bytes.Length)
                    read += stream.Read(bytes, read, bytes.Length - read);
                return bytes;
            }
        }
    }
}
