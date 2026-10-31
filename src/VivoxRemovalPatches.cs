using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading.Tasks;
using Alta.Console.Commands;
using Alta.QuickAccessActions;
using Alta.Utilities;
using Alta.Voice;
using HarmonyLib;
using UnityEngine;

namespace TavernVoice
{
    internal static class VivoxRemovalPatches
    {
        internal static void Apply(HarmonyLib.Harmony harmony)
        {
            harmony.Patch(AccessTools.Method(typeof(VivoxManager), "Awake"),
                prefix: new HarmonyMethod(typeof(VivoxRemovalPatches), nameof(DisableVivox)));
            harmony.Patch(AccessTools.PropertyGetter(typeof(MicMuteQuickAccessAction), "IsActive"),
                prefix: new HarmonyMethod(typeof(VivoxRemovalPatches), nameof(GetMicMuted)));
            harmony.Patch(AccessTools.Method(typeof(MicMuteQuickAccessAction), "Run"),
                prefix: new HarmonyMethod(typeof(VivoxRemovalPatches), nameof(ToggleQuickMic)));

            Type microphoneCommands = typeof(AudioCommandModule).GetNestedType("Microphone", BindingFlags.Public | BindingFlags.NonPublic);
            harmony.Patch(AccessTools.Method(microphoneCommands, "Mute"),
                prefix: new HarmonyMethod(typeof(VivoxRemovalPatches), nameof(MuteMic)));
            harmony.Patch(AccessTools.Method(microphoneCommands, "Unmute"),
                prefix: new HarmonyMethod(typeof(VivoxRemovalPatches), nameof(UnmuteMic)));

            harmony.Patch(AccessTools.Method(typeof(FlyCamHands), "MuteOwnMic"),
                prefix: new HarmonyMethod(typeof(VivoxRemovalPatches), nameof(ToggleMic)));
            harmony.Patch(AccessTools.PropertyGetter(typeof(VivoxConnectionQuickAccessAction), "IsValid"),
                prefix: new HarmonyMethod(typeof(VivoxRemovalPatches), nameof(VivoxActionInvalid)));

            harmony.Patch(AccessTools.Method(typeof(MicrophoneEnumSettingAccess), "GetPossibleValue"),
                prefix: new HarmonyMethod(typeof(VivoxRemovalPatches), nameof(GetMicrophones)));
            harmony.Patch(AccessTools.Method(typeof(MicrophoneEnumSettingAccess), "SetValue"),
                prefix: new HarmonyMethod(typeof(VivoxRemovalPatches), nameof(SetMicrophone)));
            harmony.Patch(AccessTools.Method(typeof(MicrophoneEnumSettingAccess), "GetValue"),
                prefix: new HarmonyMethod(typeof(VivoxRemovalPatches), nameof(GetMicrophone)));

            var transpiler = new HarmonyMethod(typeof(VivoxRemovalPatches), nameof(ReplaceModeratorMute));
            harmony.Patch(AccessTools.Method(typeof(ModsMicMuteManager), "UpdateMute"), transpiler: transpiler);
            harmony.Patch(AccessTools.Method(typeof(ModsMicMuteManager), "SyncRequest"), transpiler: transpiler);
        }

        private static bool DisableVivox(VivoxManager __instance)
        {
            __instance.enabled = false;
            return false;
        }

        private static bool GetMicMuted(ref bool __result) { __result = VoiceRuntime.IsMuted; return false; }
        private static bool ToggleMic() { VoiceRuntime.ToggleMuted(); return false; }
        private static bool ToggleQuickMic(MicMuteQuickAccessAction __instance)
        {
            VoiceRuntime.ToggleMuted();
            AccessTools.Method(typeof(QuickAccessMenuAction), "UpdateActive").Invoke(__instance, null);
            return false;
        }
        private static bool MuteMic() { VoiceRuntime.SetMuted(true); return false; }
        private static bool UnmuteMic() { VoiceRuntime.SetMuted(false); return false; }
        private static bool VivoxActionInvalid(ref bool __result) { __result = false; return false; }

        private static bool GetMicrophones(ref Task<string[]> __result)
        {
            __result = Task.FromResult(VoiceCapture.DeviceNames());
            return false;
        }

        private static bool SetMicrophone(string value) { VoiceRuntime.SetMicrophone(value); return false; }
        private static bool GetMicrophone(ref string __result) { __result = VoiceRuntime.GetMicrophone(); return false; }

        private static IEnumerable<CodeInstruction> ReplaceModeratorMute(IEnumerable<CodeInstruction> source)
        {
            List<CodeInstruction> code = source.ToList();
            MethodInfo setModsMute = AccessTools.Method(typeof(VivoxClient), "SetModsMute");
            MethodInfo getClient = AccessTools.PropertyGetter(typeof(VivoxManager), "Client");
            MethodInfo getInstance = AccessTools.PropertyGetter(typeof(SingletonBehaviour<VivoxManager>), "Instance");
            MethodInfo replacement = AccessTools.Method(typeof(VoiceRuntime), nameof(VoiceRuntime.SetModeratorMuted));
            for (int i = 0; i < code.Count; i++)
            {
                if (!code[i].Calls(setModsMute))
                    continue;
                int clientIndex = PreviousCall(code, i, getClient);
                int instanceIndex = PreviousCall(code, clientIndex, getInstance);
                code[instanceIndex].opcode = OpCodes.Nop;
                code[instanceIndex].operand = null;
                code[clientIndex].opcode = OpCodes.Nop;
                code[clientIndex].operand = null;
                code[i].opcode = OpCodes.Call;
                code[i].operand = replacement;
            }
            return code;
        }

        private static int PreviousCall(List<CodeInstruction> code, int before, MethodInfo method)
        {
            for (int i = before - 1; i >= 0; i--)
                if (code[i].Calls(method)) return i;
            throw new InvalidOperationException($"Expected call to {method.Name}");
        }
    }
}
