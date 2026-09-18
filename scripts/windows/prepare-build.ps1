param(
    [switch]$UnityOnly,
    [switch]$SkipUnity,
    [string]$FfmpegExe = $env:FFMPEG_EXE
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repoRoot = (Resolve-Path "$PSScriptRoot\..\..").Path
$unityProject = Join-Path $repoRoot 'src\Karaoke.Stage.Unity'
$projectVersionFile = Join-Path $unityProject 'ProjectSettings\ProjectVersion.txt'

$isWindowsHost = [Runtime.InteropServices.RuntimeInformation]::IsOSPlatform(
    [Runtime.InteropServices.OSPlatform]::Windows)
if (-not $isWindowsHost) { throw 'Windows-Builds müssen unter Windows vorbereitet werden.' }
if (-not (Test-Path $projectVersionFile -PathType Leaf)) {
    throw "Unity-Projektversion fehlt: $projectVersionFile"
}
$versionLine = Get-Content $projectVersionFile | Where-Object { $_ -like 'm_EditorVersion: *' } |
    Select-Object -First 1
if (-not $versionLine) { throw 'Unity-Version konnte nicht gelesen werden.' }
$unityVersion = $versionLine.Substring('m_EditorVersion: '.Length).Trim()

function Resolve-UnityEditor {
    if ($env:UNITY_EDITOR) { return $env:UNITY_EDITOR }
    return Join-Path $env:ProgramFiles "Unity\Hub\Editor\$unityVersion\Editor\Unity.exe"
}

if (-not $SkipUnity) {
    $unity = Resolve-UnityEditor
    if (-not $unity -or -not (Test-Path $unity -PathType Leaf)) {
        throw "Unity $unityVersion wurde nicht gefunden. Installiere Unity 6 oder setze UNITY_EDITOR."
    }
    $windowsSupport = Join-Path (Split-Path $unity -Parent) 'Data\PlaybackEngines\WindowsStandaloneSupport'
    if (-not (Test-Path $windowsSupport -PathType Container)) {
        throw "Unity-Modul 'Windows Build Support (Mono)' fehlt: $windowsSupport"
    }
    New-Item -ItemType Directory -Force -Path "$repoRoot\Builds" | Out-Null
    Write-Host "Prepare Windows: Unity $unityVersion · $unity"
    & $unity -batchmode -nographics -quit -projectPath $unityProject `
        -logFile "$repoRoot\Builds\windows-prepare.log"
    if ($LASTEXITCODE -ne 0) { throw "Unity-Prepare fehlgeschlagen ($LASTEXITCODE)." }
}

if (-not $UnityOnly) {
    $dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
    if (-not $dotnet) { throw '.NET SDK 10 wurde nicht gefunden.' }
    $major = [int]((& dotnet --version).Split('.')[0])
    if ($major -lt 10) { throw ".NET SDK 10 oder neuer erforderlich; gefunden: $(& dotnet --version)" }

    if (-not $FfmpegExe) {
        $ffmpegCommand = Get-Command ffmpeg.exe -ErrorAction SilentlyContinue
        if ($ffmpegCommand) { $FfmpegExe = $ffmpegCommand.Source }
    }
    if (-not $FfmpegExe -or -not (Test-Path $FfmpegExe -PathType Leaf)) {
        throw 'ffmpeg.exe wurde nicht gefunden. Setze FFMPEG_EXE oder ergänze PATH.'
    }

    & dotnet restore "$repoRoot\src\Karaoke.Server\Karaoke.Server.csproj"
    if ($LASTEXITCODE -ne 0) { throw 'Restore von Karaoke.Server ist fehlgeschlagen.' }
    & dotnet restore "$repoRoot\src\Karaoke.App.Desktop\Karaoke.App.Desktop.csproj"
    if ($LASTEXITCODE -ne 0) { throw 'Restore von Karaoke.App.Desktop ist fehlgeschlagen.' }
}

Write-Host 'Windows-Prepare erfolgreich.'
