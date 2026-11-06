using System;
using System.Globalization;
using Alta.Console;
using MelonLoader;

namespace TavernVoice
{
    [Module("tavernvoice", "TavernVoice proximity voice: status and settings")]
    internal static class ServerCommands
    {
        internal static void Register()
        {
            try
            {
                CommandService.CommandCollection.Collect(typeof(ServerCommands).Assembly);
            }
            catch (Exception exception)
            {
                MelonLogger.Warning($"The tavernvoice console commands could not be added: {exception.Message}");
            }
        }

        [Command(null, "Shows the relay, the settings and every player's voice")]
        [Alias("status")]
        private static string Status() => ServerVoice.StatusText();

        [Command("range", "Sets how far a voice carries, in meters (1 to 1000)")]
        private static string Range(float meters)
        {
            if (float.IsNaN(meters) || meters < 1f || meters > 1000f)
                return "Give a range from 1 to 1000 meters, for example: tavernvoice range 50";
            ServerVoice.SetRange(meters);
            return $"Voices now carry {meters.ToString("0.#", CultureInfo.InvariantCulture)} m (saved)";
        }

        [Command("relay", "Turns the voice relay on or off (off: all voice goes through the game connection)")]
        private static string Relay(string value)
        {
            if (!TryOnOff(value, out bool on))
                return "Use: tavernvoice relay on, or tavernvoice relay off";
            ServerVoice.SetRelay(on);
            return on ? "Relay on (saved). Players switch to it within a few seconds." : "Relay off (saved). Voice goes through the game connection.";
        }

        [Command("fallback", "Turns voice over the game connection on or off, for players who can't reach the relay")]
        private static string Fallback(string value)
        {
            if (!TryOnOff(value, out bool on))
                return "Use: tavernvoice fallback on, or tavernvoice fallback off";
            Settings.SetInGameFallback(on);
            return on ? "In-game fallback on (saved)" : "In-game fallback off (saved). Players who can't reach the relay have no voice.";
        }

        [Command("cvc", "Turns the bridge for players still on Circuit's Voice Chat on or off")]
        private static string Cvc(string value)
        {
            if (!TryOnOff(value, out bool on))
                return "Use: tavernvoice cvc on, or tavernvoice cvc off";
            ServerVoice.SetCvcBridge(on);
            return on ? "Circuit's Voice Chat bridge on (saved). Those players talk through the game connection." : "Circuit's Voice Chat bridge off (saved). Those players have no voice.";
        }

        [Command("rate", "Sets how many times a second player positions go to the relay (1 to 20)")]
        private static string Rate(int hz)
        {
            if (hz < 1 || hz > 20)
                return "Give a rate from 1 to 20, for example: tavernvoice rate 4";
            Settings.SetPositionRateHz(hz);
            return $"Positions go to the relay {hz} times a second (saved)";
        }

        private static bool TryOnOff(string value, out bool on)
        {
            switch ((value ?? "").Trim().ToLowerInvariant())
            {
                case "on":
                case "true":
                case "yes":
                case "1":
                    on = true;
                    return true;
                case "off":
                case "false":
                case "no":
                case "0":
                    on = false;
                    return true;
                default:
                    on = false;
                    return false;
            }
        }
    }
}
