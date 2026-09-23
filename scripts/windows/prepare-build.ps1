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

function Invoke-NativeChecked {
    param(
        [Parameter(Mandatory = $true)][string]$FilePath,
        [string[]]$ArgumentList = @(),
        [Parameter(Mandatory = $true)][string]$Description
    )
    # LASTEXITCODE is created only by native processes. Initializing it makes
    # this reliable under StrictMode in both Windows PowerShell 5.1 and pwsh.
    $global:LASTEXITCODE = 0
    & $FilePath @ArgumentList
    $exitCode = [int]$global:LASTEXITCODE
    if ($exitCode -ne 0) { throw "$Description fehlgeschlagen ($exitCode)." }
}

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
    Invoke-NativeChecked -FilePath $unity -Description 'Unity-Prepare' -ArgumentList @(
        '-batchmode', '-nographics', '-quit', '-projectPath', $unityProject,
        '-logFile', "$repoRoot\Builds\windows-prepare.log"
    )
}

if (-not $UnityOnly) {
    $dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
    if (-not $dotnet) { throw '.NET SDK 10 wurde nicht gefunden.' }
    $dotnetVersion = (Invoke-NativeChecked -FilePath $dotnet.Source -Description '.NET-Versionstest' `
        -ArgumentList @('--version') | Select-Object -First 1).Trim()
    $major = [int]($dotnetVersion.Split('.')[0])
    if ($major -lt 10) { throw ".NET SDK 10 oder neuer erforderlich; gefunden: $dotnetVersion" }

    if (-not $FfmpegExe) {
        $ffmpegCommand = Get-Command ffmpeg.exe -ErrorAction SilentlyContinue
        if ($ffmpegCommand) { $FfmpegExe = $ffmpegCommand.Source }
    }
    if (-not $FfmpegExe -or -not (Test-Path $FfmpegExe -PathType Leaf)) {
        throw 'ffmpeg.exe wurde nicht gefunden. Setze FFMPEG_EXE oder ergänze PATH.'
    }

    Invoke-NativeChecked -FilePath $dotnet.Source -Description 'Restore von Karaoke.Server' `
        -ArgumentList @('restore', "$repoRoot\src\Karaoke.Server\Karaoke.Server.csproj")
    Invoke-NativeChecked -FilePath $dotnet.Source -Description 'Restore von Karaoke.App.Desktop' `
        -ArgumentList @('restore', "$repoRoot\src\Karaoke.App.Desktop\Karaoke.App.Desktop.csproj")
}

Write-Host 'Windows-Prepare erfolgreich.'
