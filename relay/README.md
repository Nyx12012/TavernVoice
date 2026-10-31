# TavernVoiceRelay

The small program the TavernVoice server mod starts beside the game server. It
passes voice between players who are in range of each other, outside the game
process, so a server hitch doesn't stall anyone's voice.

## Build

Go 1.22 or newer. On Windows:

    .\build.ps1

Anywhere else:

    GOOS=windows GOARCH=amd64 CGO_ENABLED=0 go build -trimpath -ldflags "-s -w" -o TavernVoiceRelay.exe

Tests: `go test ./...`

## Run

The mod starts it. By hand:

    TavernVoiceRelay.exe --key <32 hex digits> [--port 1770] [--parent <pid>] [--range 50]

It exits with:

- 0: the server process is gone
- 1: bad arguments
- 2: something else holds the port
- 3: a newer relay took the port over
