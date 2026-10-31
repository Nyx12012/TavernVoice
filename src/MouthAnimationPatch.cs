using Alta.Networking.Voice;
using HarmonyLib;
using UnityEngine;

namespace TavernVoice
{
    internal static class MouthAnimationPatch
    {
        private static readonly int MouthShapeId = Animator.StringToHash("Mouth Shape");
        private static readonly AccessTools.FieldRef<VoiceChatMouthController, Player> PlayerField =
            AccessTools.FieldRefAccess<VoiceChatMouthController, Player>("player");
        private static readonly AccessTools.FieldRef<VoiceChatMouthController, Animator> AnimatorField =
            AccessTools.FieldRefAccess<VoiceChatMouthController, Animator>("animator");
        private static readonly AccessTools.FieldRef<VoiceChatMouthController, AnimationCurve> CurveField =
            AccessTools.FieldRefAccess<VoiceChatMouthController, AnimationCurve>("voiceModulator");
        private static readonly AccessTools.FieldRef<VoiceChatMouthController, int> LayerField =
            AccessTools.FieldRefAccess<VoiceChatMouthController, int>("speakingLayer");
        private static readonly AccessTools.FieldRef<VoiceChatMouthController, float> ChanceField =
            AccessTools.FieldRefAccess<VoiceChatMouthController, float>("mouthChangeChance");
        private static readonly AccessTools.FieldRef<VoiceChatMouthController, float> ShiftField =
            AccessTools.FieldRefAccess<VoiceChatMouthController, float>("normalizedVolumeShiftChange");
        private static readonly AccessTools.FieldRef<VoiceChatMouthController, int> ShapesField =
            AccessTools.FieldRefAccess<VoiceChatMouthController, int>("numberOfMouthShapes");
        private static readonly AccessTools.FieldRef<VoiceChatMouthController, float> LastField =
            AccessTools.FieldRefAccess<VoiceChatMouthController, float>("lastChangeVolume");

        internal static void Apply(HarmonyLib.Harmony harmony)
        {
            harmony.Patch(AccessTools.Method(typeof(VoiceChatMouthController), "LateUpdate"),
                prefix: new HarmonyMethod(typeof(MouthAnimationPatch), nameof(UpdateMouth)));
        }

        private static bool UpdateMouth(VoiceChatMouthController __instance)
        {
            Player player = PlayerField(__instance);
            Animator animator = AnimatorField(__instance);
            if (player == null || animator == null)
                return false;
            float amount = CurveField(__instance).Evaluate(VoiceRuntime.GetAudioEnergy(player.UserInfo.Identifier));
            animator.SetLayerWeight(LayerField(__instance), amount);
            float last = LastField(__instance);
            if (Random.value < ChanceField(__instance) + ShiftField(__instance) * Mathf.Abs(last - amount))
            {
                LastField(__instance) = amount;
                animator.SetInteger(MouthShapeId, Random.Range(0, ShapesField(__instance)));
            }
            return false;
        }
    }
}
