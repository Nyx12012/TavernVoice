using System;
using Alta.QuickAccessActions;
using HarmonyLib;
using MelonLoader;
using UnityEngine;

namespace TavernVoice
{
    internal static class NameIcons
    {
        private const float IconHeight = 0.2f;
        private static readonly AccessTools.FieldRef<NameBoard, TextRenderer> NameText = AccessTools.FieldRefAccess<NameBoard, TextRenderer>("userNameRenderer");
        private static readonly AccessTools.FieldRef<QuickAccessMenuAction, GameObject> ActionPrefab = AccessTools.FieldRefAccess<QuickAccessMenuAction, GameObject>("prefab");
        private static bool warned;

        internal static Mesh Body { get; private set; }
        internal static Mesh Slash { get; private set; }
        internal static float Scale { get; private set; }
        internal static Quaternion Facing { get; private set; } = Quaternion.identity;
        internal static Vector3 Center { get; private set; }
        internal static Material Speaking { get; private set; }
        internal static Material Light { get; private set; }
        internal static Material Dim { get; private set; }
        internal static Material Red { get; private set; }
        internal static Material Orange { get; private set; }

        internal static void Apply(HarmonyLib.Harmony harmony)
        {
            harmony.Patch(AccessTools.Method(typeof(NameBoard), nameof(NameBoard.Setup)),
                postfix: new HarmonyMethod(typeof(NameIcons), nameof(Setup)));
        }

        private static void Setup(NameBoard __instance, string userName)
        {
            try
            {
                if (!Load())
                    return;
                VoiceIcon icon = __instance.GetComponent<VoiceIcon>();
                if (icon == null)
                    icon = __instance.gameObject.AddComponent<VoiceIcon>();
                icon.Bind(UserIdOf(userName), NameText(__instance));
            }
            catch (Exception exception)
            {
                if (warned)
                    return;
                warned = true;
                MelonLogger.Warning($"Voice icons on name boards failed: {exception.Message}");
            }
        }

        private static int UserIdOf(string userName)
        {
            foreach (Alta.Networking.Scripts.Player.IPlayer player in Player.AllPlayers)
                if (player.UserInfo != null && player.UserInfo.Username == userName)
                    return player.UserInfo.Identifier;
            return TestMode.Active && userName == TestMode.PeerName ? TestMode.PeerSender : 0;
        }

        private static bool Load()
        {
            if (Body != null)
                return true;
            foreach (MicMuteQuickAccessAction action in Resources.FindObjectsOfTypeAll<MicMuteQuickAccessAction>())
            {
                GameObject prefab = ActionPrefab(action);
                if (prefab == null)
                    continue;
                Material baseMaterial = null;
                foreach (MeshFilter filter in prefab.GetComponentsInChildren<MeshFilter>(true))
                {
                    if (filter.name == "microphoneToggle_emblem_body")
                    {
                        Body = filter.sharedMesh;
                        MeshRenderer renderer = filter.GetComponent<MeshRenderer>();
                        baseMaterial = renderer != null ? renderer.sharedMaterial : null;
                    }
                    else if (filter.name == "microphoneToggle_emblem_slash")
                        Slash = filter.sharedMesh;
                }
                if (Body == null || Slash == null || baseMaterial == null)
                {
                    Body = null;
                    continue;
                }
                Bounds bounds = Body.bounds;
                Vector3 size = bounds.size;
                if (size.y <= size.x && size.y <= size.z)
                {
                    Facing = Quaternion.Euler(90f, 0f, 0f);
                    size = new Vector3(size.x, size.z, size.y);
                }
                else if (size.x <= size.z)
                {
                    Facing = Quaternion.Euler(0f, 90f, 0f);
                    size = new Vector3(size.z, size.y, size.x);
                }
                Scale = IconHeight / Mathf.Max(0.0001f, size.y);
                Center = Facing * bounds.center * Scale;
                Speaking = Tinted(baseMaterial, new Color(0.35f, 0.95f, 0.45f));
                Light = Tinted(baseMaterial, new Color(0.9f, 0.9f, 0.9f));
                Dim = Tinted(baseMaterial, new Color(0.4f, 0.4f, 0.4f));
                Red = Tinted(baseMaterial, new Color(0.95f, 0.2f, 0.2f));
                Orange = Tinted(baseMaterial, new Color(1f, 0.6f, 0.15f));
                if (TestMode.Active)
                    MelonLogger.Msg($"TVTEST mic icon mesh bounds {Body.bounds.size} center {Body.bounds.center}, slash {Slash.bounds.size}, facing {Facing.eulerAngles}, scale {Scale:0.000}, shader {baseMaterial.shader.name}");
                return true;
            }
            if (!warned)
            {
                warned = true;
                MelonLogger.Warning("The game's microphone icon was not found: no voice icons on name boards");
            }
            return false;
        }

        private static Material Tinted(Material source, Color color)
        {
            Shader flat = Shader.Find("Unlit/Color");
            var material = flat != null ? new Material(flat) : new Material(source);
            material.name = "TavernVoice Icon";
            material.color = color;
            return material;
        }
    }

    internal sealed class VoiceIcon : MonoBehaviour
    {
        private const float Gap = 0.12f;
        private int userId;
        private TextRenderer nameText;
        private Transform root;
        private Transform body;
        private MeshRenderer bodyRenderer;
        private MeshRenderer slashRenderer;
        private StatusIcon shown;
        private bool built;

        internal int UserId => userId;
        internal StatusIcon Shown => shown;

        internal void Bind(int id, TextRenderer text)
        {
            userId = id;
            nameText = text;
            if (!built)
                Build();
            shown = StatusIcon.None;
            root.gameObject.SetActive(false);
            Refresh();
        }

        private void Build()
        {
            built = true;
            int layer = nameText != null ? nameText.gameObject.layer : gameObject.layer;
            root = new GameObject("TavernVoice Status").transform;
            root.gameObject.layer = layer;
            root.SetParent(transform, false);
            Transform icon = new GameObject("Icon").transform;
            icon.gameObject.layer = layer;
            icon.SetParent(root, false);
            icon.localRotation = NameIcons.Facing;
            icon.localPosition = -NameIcons.Center;
            icon.localScale = Vector3.one * NameIcons.Scale;
            body = Part(icon, "Body", NameIcons.Body, 1f, layer, out bodyRenderer);
            Part(icon, "Slash", NameIcons.Slash, 1.5f, layer, out slashRenderer);
        }

        private static Transform Part(Transform parent, string name, Mesh mesh, float scale, int layer, out MeshRenderer renderer)
        {
            var part = new GameObject(name);
            part.layer = layer;
            part.transform.SetParent(parent, false);
            part.transform.localScale = Vector3.one * scale;
            part.AddComponent<MeshFilter>().sharedMesh = mesh;
            renderer = part.AddComponent<MeshRenderer>();
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            return part.transform;
        }

        private void LateUpdate() => Refresh();

        private void Refresh()
        {
            if (!built)
                return;
            StatusIcon icon = userId == 0 ? StatusIcon.None : PlayerStatus.IconOf(userId);
            if (icon != shown)
            {
                shown = icon;
                root.gameObject.SetActive(icon != StatusIcon.None);
                bodyRenderer.sharedMaterial = icon == StatusIcon.Speaking ? NameIcons.Speaking : icon == StatusIcon.NoMic ? NameIcons.Dim : NameIcons.Light;
                slashRenderer.enabled = icon == StatusIcon.Muted || icon == StatusIcon.NoMic;
                slashRenderer.sharedMaterial = icon == StatusIcon.NoMic ? NameIcons.Orange : NameIcons.Red;
            }
            if (icon == StatusIcon.None)
                return;
            float half = nameText != null ? nameText.CurrentLineWidth * 0.5f : 0.5f;
            Vector3 at = nameText != null ? nameText.transform.localPosition : Vector3.zero;
            root.localPosition = new Vector3(-(half + Gap), at.y + 0.1f, at.z);
            float pulse = icon == StatusIcon.Speaking ? 1f + 0.25f * Mathf.Clamp01(VoiceRuntime.GetAudioEnergy(userId) * 4f) : 1f;
            body.localScale = Vector3.one * pulse;
        }
    }
}
