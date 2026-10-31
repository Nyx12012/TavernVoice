using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using MelonLoader;

[assembly: MelonInfo(typeof(TavernVoice.Mod), "TavernVoice", TavernVoice.Mod.Version, "Nyx12012")]
[assembly: MelonGame("Alta", "A Township Tale")]

namespace TavernVoice
{
    public sealed class Mod : MelonMod
    {
        public const string Version = "2.0.1";
        private VoiceSettingsGui settingsGui;
        private bool off;
        private bool server;

        public override void OnInitializeMelon()
        {
            if (CircuitsVoiceChatLoaded())
            {
                off = true;
                LoggerInstance.Error("Circuit's Voice Chat is installed too. Delete Mods/CircuitsVoiceChat*.dll; TavernVoice stays off until then.");
                return;
            }
            string concentus = ConcentusVersion();
            if (concentus == null)
            {
                off = true;
                LoggerInstance.Error("Concentus.dll is missing from UserLibs; reinstall TavernVoice from the launcher.");
                return;
            }
            Settings.Load();
            server = ServerVoice.IsServerProcess;
            VivoxRemovalPatches.Apply(HarmonyInstance);
            KcpSocketPatch.Apply(HarmonyInstance);
            if (server)
                ServerVoice.Initialize(HarmonyInstance);
            else
            {
                VoiceRuntime.Initialize(HarmonyInstance);
                NameIcons.Apply(HarmonyInstance);
                settingsGui = new VoiceSettingsGui();
            }
            LoggerInstance.Msg($"TavernVoice {Version} as {(server ? "server" : "client")}, Concentus {concentus}{(TestMode.Active ? ", TEST MODE" : "")}");
        }

        private static bool CircuitsVoiceChatLoaded()
        {
            if (MelonBase.RegisteredMelons.Any(m => m.Info.Name == "Circuit's Voice Chat"))
                return true;
            return AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetName().Name == "CircuitsVoiceChat");
        }

        private static string ConcentusVersion()
        {
            try
            {
                return ReadConcentusVersion();
            }
            catch (Exception e) when (e is FileNotFoundException || e is FileLoadException || e is TypeLoadException)
            {
                return null;
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static string ReadConcentusVersion() => typeof(Concentus.Structs.OpusDecoder).Assembly.GetName().Version.ToString();

        public override void OnUpdate()
        {
            if (off)
                return;
            if (server)
                ServerVoice.Update();
            else
            {
                settingsGui.Update();
                VoiceRuntime.Update();
            }
        }

        public override void OnGUI() => settingsGui?.OnGui();

        public override void OnApplicationQuit()
        {
            if (off)
                return;
            if (server)
                RelayHost.Stop();
            else
                VoiceRuntime.Shutdown();
        }
    }
}
