using System;
using UnityEngine;
using UnityEngine.InputSystem;

namespace TavernVoice
{
    internal sealed class VoiceSettingsGui
    {
        private const int WindowId = 0x564f4950;
        private bool visible;
        private Rect window = new Rect(20f, 40f, 620f, 460f);
        private string[] microphoneDevices = new string[0];
        private string status = "Waiting for status...";
        private string remoteStatus = "(none)";
        private double nextStatusRefresh;
        private bool microphoneDropdown;
        private Vector2 microphoneScroll;

        internal VoiceSettingsGui() => visible = false;

        internal void Update()
        {
            if (Keyboard.current != null && Keyboard.current.f5Key.wasPressedThisFrame)
            {
                visible = !visible;
                if (visible)
                {
                    microphoneDevices = VoiceCapture.DeviceNames();
                    RefreshStatus();
                }
            }
            if (visible && Clock.Now >= nextStatusRefresh)
                RefreshStatus();
        }

        internal void OnGui()
        {
            if (visible && !ApplicationManager.IsHeadless)
                window = GUI.Window(WindowId, window, Draw, "TavernVoice Settings (F5)");
        }

        private void Draw(int id)
        {
            GUI.Label(new Rect(15f, 25f, 590f, 85f), status);

            float y = 115f;
            GUI.Label(new Rect(15f, y, 590f, 22f), "Microphone input");
            y += 22f;
            string selected = VoiceRuntime.GetMicrophone();
            y += 2f;
            string selectedLabel = string.IsNullOrEmpty(selected) ? "Windows default" : selected;
            if (GUI.Button(new Rect(15f, y, 300f, 28f), $"{selectedLabel}  ▼")) microphoneDropdown = !microphoneDropdown;
            if (GUI.Button(new Rect(325f, y, 110f, 28f), "Save device")) VoiceRuntime.SaveMicrophone();
            if (GUI.Button(new Rect(445f, y, 160f, 28f), "Clear saved override")) VoiceRuntime.UseWindowsDefaultMicrophone();

            GUI.enabled = !microphoneDropdown;
            y += 38f;
            GUI.Label(new Rect(15f, y, 590f, 22f), $"Receive volume: {VoiceRuntime.OutputGain:0.00}×");
            y += 22f;
            float gain = GUI.HorizontalSlider(new Rect(15f, y, 470f, 22f), VoiceRuntime.OutputGain, 0f, 4f);
            if (GUI.enabled)
                VoiceRuntime.SetOutputGain(gain);
            if (GUI.Button(new Rect(495f, y - 5f, 110f, 26f), "Save volume")) VoiceRuntime.SaveOutputGain();

            y += 35f;
            GUI.Label(new Rect(15f, y, 250f, 28f), $"Audio bank: {(VoiceRuntime.IsBankLoaded ? "loaded" : "loading...")}");
            if (GUI.Button(new Rect(275f, y, 150f, 28f), "Reset counters")) VoiceDiagnostics.Reset();
            GUI.enabled = true;

            y += 38f;
            GUI.Label(new Rect(15f, y, 590f, 22f), "Remote speakers");
            y += 22f;
            GUI.Label(new Rect(15f, y, 590f, 110f), remoteStatus);
            if (microphoneDropdown)
                DrawMicrophoneDropdown(15f, 167f, 300f);
            GUI.DragWindow(new Rect(0f, 0f, 620f, 22f));
        }

        private void DrawMicrophoneDropdown(float x, float y, float width)
        {
            const float RowHeight = 26f;
            const float MaxViewHeight = RowHeight * 6f;
            int count = microphoneDevices.Length + 1;
            float contentHeight = count * RowHeight;
            float viewHeight = Mathf.Min(contentHeight, MaxViewHeight);
            float rowWidth = contentHeight > viewHeight ? width - 24f : width - 4f;
            GUI.Box(new Rect(x, y, width, viewHeight + 4f), GUIContent.none);
            microphoneScroll = GUI.BeginScrollView(new Rect(x + 2f, y + 2f, width - 4f, viewHeight), microphoneScroll, new Rect(0f, 0f, rowWidth, contentHeight));
            if (GUI.Button(new Rect(0f, 0f, rowWidth, 24f), "Windows default"))
            {
                VoiceRuntime.SetMicrophone("");
                microphoneDropdown = false;
            }
            for (int i = 0; i < microphoneDevices.Length; i++)
            {
                string device = microphoneDevices[i];
                if (!GUI.Button(new Rect(0f, RowHeight + i * RowHeight, rowWidth, 24f), device))
                    continue;
                VoiceRuntime.SetMicrophone(device);
                microphoneDropdown = false;
            }
            GUI.EndScrollView();
        }

        private void RefreshStatus()
        {
            nextStatusRefresh = Clock.Now + 0.25;
            string micState = VoiceRuntime.IsCapturing ? "ON" : VoiceRuntime.CaptureFailure != null ? $"FAILED ({VoiceRuntime.CaptureFailure})" : "OFF";
            status =
                $"{(VoiceRuntime.IsConnected ? "CONNECTED" : "DISCONNECTED")} | {VoiceRuntime.PathDescription} | Mic {micState} | Level {VoiceRuntime.LocalEnergy:0.0000} | Muted {VoiceRuntime.IsMuted}\n" +
                $"Up {Kbps(VoiceDiagnostics.SendBytesPerSecond):0.0} kbps | Down {Kbps(VoiceDiagnostics.ReceiveBytesPerSecond):0.0} kbps | Streams {VoiceRuntime.RemoteCount}\n" +
                $"Quality: PLC {VoiceDiagnostics.PlcFrames:N0} | FEC {VoiceDiagnostics.FecFrames:N0} | underruns {VoiceDiagnostics.AudioUnderruns:N0} | queue drops {VoiceDiagnostics.QueueDropped:N0}";
            string[] remotes = VoiceRuntime.GetRemoteDebugLines();
            remoteStatus = remotes.Length == 0 ? "(none)" : string.Join("\n", remotes);
        }

        private static float Kbps(float bytesPerSecond) => bytesPerSecond * 8f / 1000f;
    }
}
