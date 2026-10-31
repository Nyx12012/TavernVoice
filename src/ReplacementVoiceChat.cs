using System;
using System.Collections.Generic;
using Alta.Voice;
using UnityEngine;

namespace TavernVoice
{
    [Serializable]
    internal sealed class MutedList
    {
        public int[] ids;
    }

    internal sealed class ReplacementVoiceChat : IVoiceChat
    {
        private const string MutedPlayersKey = "MutedPlayersKey";
        private readonly HashSet<int> muted = new HashSet<int>();

        internal ReplacementVoiceChat()
        {
            string json = PlayerPrefs.GetString(MutedPlayersKey, "");
            try
            {
                MutedList saved = string.IsNullOrEmpty(json) ? null : JsonUtility.FromJson<MutedList>(json);
                if (saved?.ids != null)
                    foreach (int id in saved.ids) muted.Add(id);
            }
            catch (Exception exception)
            {
                MelonLoader.MelonLogger.Error(exception.ToString());
            }
        }

        public VoiceChatScope AudibleScope { get; private set; } = VoiceChatScope.Everyone;

        public void CycleAudibleScope()
        {
            VoiceChatScope next = AudibleScope - 1;
            AudibleScope = next < VoiceChatScope.Noone ? VoiceChatScope.Everyone : next;
            VoiceRuntime.ClearMutedAudio();
        }

        public void SetPlayerMuted(int player, bool isMuted)
        {
            if (isMuted) muted.Add(player); else muted.Remove(player);
            PlayerPrefs.SetString(MutedPlayersKey, JsonUtility.ToJson(new MutedList { ids = new List<int>(muted).ToArray() }));
            VoiceRuntime.ClearMutedAudio();
        }

        public bool GetPlayerMuted(int player) => muted.Contains(player);

        internal bool IsAudible(int userId)
        {
            if (AudibleScope == VoiceChatScope.Noone || muted.Contains(userId))
                return false;
            if (AudibleScope == VoiceChatScope.Friends)
                return Player.Current != null && Player.Current.FriendshipManager.IsFriendsWith(userId);
            return true;
        }
    }
}
