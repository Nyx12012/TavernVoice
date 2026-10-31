# TavernVoice

Proximity voice chat for A Township Tale servers. It replaces Circuit's Voice Chat and is built from
it (MIT, CircuitLord).

Voice goes through a small relay that the server starts next to itself, so a server hitch doesn't
stall anyone's voice. Players who can't reach the relay still get voice through the game
connection.

## Install

Two files, on the server and on every player's PC:

- `Mods/TavernVoice.dll`
- `UserLibs/Concentus.dll` (the same file Circuit's Voice Chat uses)

Remove `Mods/CircuitsVoiceChat*.dll` first. TavernVoice stays off while it is there.

## Hosting

Open UDP port 1770 on the server (a router forward at home, a firewall rule on a VPS). On Windows the
firewall asks once about `TavernVoiceRelay.exe`. Until the port is open, voice still works through
the game connection, just not lag proof.

Settings are in `UserData/MelonPreferences.cfg` under `[TavernVoice]`: `RelayPort`, `RelayEnabled`,
`RangeMeters`, `PositionRateHz`, `InGameFallback`.

The server console changes them too, and saves them:

| command | what it does |
|---|---|
| `tavernvoice` | the relay, the settings and every player's voice |
| `tavernvoice range 50` | how far a voice carries, in meters |
| `tavernvoice relay on` or `off` | off sends all voice through the game connection |
| `tavernvoice fallback on` or `off` | voice over the game connection for players who can't reach the relay |
| `tavernvoice rate 4` | how many times a second positions go to the relay |

`RelayPort` is only read at start.

## Playing

F5 opens the settings window: microphone, volume, and what the voice is doing right now.

With player names shown, a microphone next to a name tells you about that player's voice: green
while they talk, a red slash when they muted themselves, grey with an orange slash when the game
found no microphone on their PC.

A player who goes back to customization through the hut teleporter keeps talking and listening from
the teleporter until they come back. A new player in their first customization has no voice yet.

## Build

Needs the .NET SDK, Go 1.22 or newer and the game installed at `C:\Games\Alta\A Township Tale`
(or pass `-Game`).

    .\build.ps1

`/tavernvoice_test` on the game's command line turns on the test hooks the build is tested with (a
test tone instead of the microphone, a made up second player). Never use it on a real server.

## License

MIT, see `LICENSE`. Concentus and Opus: see `THIRD-PARTY-LICENSES.txt`.
