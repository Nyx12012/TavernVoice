param([string]$Game = 'C:\Games\Alta\A Township Tale', [switch]$Zip)
$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path

& (Join-Path $here 'relay\build.ps1')
if ($LASTEXITCODE -ne 0) { throw 'relay build failed' }

dotnet build (Join-Path $here 'tests\StyleCheck\StyleCheck.csproj') -c Release -nologo -v q | Out-Null
& (Join-Path $here 'tests\StyleCheck\bin\Release\net9.0\StyleCheck.exe') $here
if ($LASTEXITCODE -ne 0) { throw 'style check failed' }

dotnet run --project (Join-Path $here 'tests\RulesTest\RulesTest.csproj') -c Release
if ($LASTEXITCODE -ne 0) { throw 'rules test failed' }

dotnet build (Join-Path $here 'TavernVoice.csproj') -c Release -nologo -v q "-p:GamePath=$Game"
if ($LASTEXITCODE -ne 0) { throw 'mod build failed' }
$dll = Join-Path $here 'bin\Release\TavernVoice.dll'
'{0} {1:N0} bytes sha256 {2}' -f 'TavernVoice.dll', (Get-Item $dll).Length, (Get-FileHash $dll -Algorithm SHA256).Hash.ToLower()

if ($Zip) {
    $version = (Select-String -Path (Join-Path $here 'src\Mod.cs') -Pattern 'Version = "([^"]+)"').Matches[0].Groups[1].Value
    $stage = Join-Path $here 'bin\zip'
    Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Force (Join-Path $stage 'Mods'), (Join-Path $stage 'UserLibs') | Out-Null
    Copy-Item $dll (Join-Path $stage 'Mods')
    Copy-Item (Join-Path $Game 'UserLibs\Concentus.dll') (Join-Path $stage 'UserLibs')
    Copy-Item (Join-Path $here 'LICENSE'), (Join-Path $here 'THIRD-PARTY-LICENSES.txt'), (Join-Path $here 'README.md') $stage
    $zipPath = Join-Path $here "bin\TavernVoice-v$version.zip"
    Remove-Item $zipPath -ErrorAction SilentlyContinue
    Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
    $archive = [System.IO.Compression.ZipFile]::Open($zipPath, 'Create')
    try {
        Get-ChildItem $stage -Recurse -File | ForEach-Object {
            $name = $_.FullName.Substring($stage.Length + 1).Replace([string][char]92, '/')
            [void][System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $_.FullName, $name)
        }
    } finally { $archive.Dispose() }
    '{0} {1:N0} bytes sha256 {2}' -f (Split-Path $zipPath -Leaf), (Get-Item $zipPath).Length, (Get-FileHash $zipPath -Algorithm SHA256).Hash.ToLower()
}
