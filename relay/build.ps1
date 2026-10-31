$ErrorActionPreference = 'Stop'
$go = (Get-Command go -ErrorAction SilentlyContinue).Source
if (-not $go) { $go = 'C:\Program Files\Go\bin\go.exe' }
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$exe = Join-Path $here 'build\TavernVoiceRelay.exe'

Push-Location $here
try {
    & $go vet .
    if ($LASTEXITCODE -ne 0) { throw "go vet failed" }
    & $go test -count=1 .
    if ($LASTEXITCODE -ne 0) { throw "go test failed" }
    $env:GOOS = 'windows'; $env:GOARCH = 'amd64'; $env:CGO_ENABLED = '0'
    & $go build -trimpath -ldflags '-s -w' -o $exe .
    if ($LASTEXITCODE -ne 0) { throw "go build failed" }
} finally {
    Remove-Item Env:GOOS, Env:GOARCH, Env:CGO_ENABLED -ErrorAction SilentlyContinue
    Pop-Location
}
$f = Get-Item $exe
'{0} {1:N0} bytes sha256 {2}' -f $f.Name, $f.Length, (Get-FileHash $exe -Algorithm SHA256).Hash.ToLower()
