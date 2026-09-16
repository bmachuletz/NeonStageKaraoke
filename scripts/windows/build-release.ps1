param(
    [Parameter(Mandatory = $true)]
    [string]$OutputDirectory,
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z.-]+)?$')]
    [string]$Version,
    [Parameter(Mandatory = $true)]
    [ValidateRange(1, 2147483647)]
    [int]$BuildNumber,
    [switch]$SkipUnity,
    [string]$FfmpegExe = $env:FFMPEG_EXE
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repoRoot = (Resolve-Path "$PSScriptRoot\..\..").Path
$outputRoot = [IO.Path]::GetFullPath($OutputDirectory)
$suffix = "v$Version-build.$BuildNumber"

function Invoke-Checked {
    param([string]$Command, [string[]]$Arguments)
    & $Command @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "Command failed ($LASTEXITCODE): $Command $($Arguments -join ' ')"
    }
}

function Copy-ReleaseNotices {
    param([string]$Destination)
    Copy-Item "$repoRoot\LICENSE", "$repoRoot\NOTICE", `
        "$repoRoot\THIRD_PARTY_NOTICES.md", "$repoRoot\ACKNOWLEDGEMENTS.md" `
        -Destination $Destination
    Copy-Item "$repoRoot\packaging\windows\README-Windows.txt" -Destination $Destination
}

function Compress-Package {
    param([string]$SourceDirectory, [string]$DestinationArchive)
    if (Test-Path $DestinationArchive) { Remove-Item -Force $DestinationArchive }
    Compress-Archive -Path (Join-Path $SourceDirectory '*') `
        -DestinationPath $DestinationArchive -CompressionLevel Optimal
}

$isWindowsHost = [Runtime.InteropServices.RuntimeInformation]::IsOSPlatform(
    [Runtime.InteropServices.OSPlatform]::Windows)
if (-not $isWindowsHost) {
    throw 'Windows release packages must be produced on Windows.'
}
if (-not $FfmpegExe) {
    $ffmpegCommand = Get-Command ffmpeg.exe -ErrorAction SilentlyContinue
    if ($ffmpegCommand) { $FfmpegExe = $ffmpegCommand.Source }
}
if (-not $FfmpegExe -or -not (Test-Path $FfmpegExe -PathType Leaf)) {
    throw 'ffmpeg.exe was not found. Set FFMPEG_EXE or add FFmpeg to PATH.'
}
$FfmpegExe = (Resolve-Path $FfmpegExe).Path

New-Item -ItemType Directory -Force -Path $outputRoot | Out-Null
$workRoot = Join-Path ([IO.Path]::GetTempPath()) ("neonstage-release-" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $workRoot | Out-Null
try {
    $serverPublish = Join-Path $workRoot 'server'
    $editorPublish = Join-Path $workRoot 'editor'
    $commonPublishArguments = @(
        '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true',
        '-p:DebugType=None', '-p:DebugSymbols=false',
        "-p:Version=$Version", "-p:InformationalVersion=$Version+build.$BuildNumber"
    )

    Invoke-Checked 'dotnet' (@('publish', "$repoRoot\src\Karaoke.Server\Karaoke.Server.csproj") +
        $commonPublishArguments + @('-o', $serverPublish))
    Invoke-Checked 'dotnet' (@('publish', "$repoRoot\src\Karaoke.App.Desktop\Karaoke.App.Desktop.csproj") +
        $commonPublishArguments + @('-o', $editorPublish))

    Copy-Item $FfmpegExe -Destination (Join-Path $serverPublish 'ffmpeg.exe')
    Copy-Item $FfmpegExe -Destination (Join-Path $editorPublish 'ffmpeg.exe')
    & $FfmpegExe -version | Set-Content -Encoding utf8 (Join-Path $serverPublish 'FFmpeg-build.txt')
    Copy-Item (Join-Path $serverPublish 'FFmpeg-build.txt') -Destination $editorPublish
    Copy-ReleaseNotices $serverPublish
    Copy-ReleaseNotices $editorPublish
    Copy-Item "$repoRoot\packaging\windows\Start-NeonStage-Server.cmd" -Destination $serverPublish
    Copy-Item "$repoRoot\packaging\windows\Start-NeonStage-Editor.cmd" -Destination $editorPublish

    $libVlc = Get-ChildItem $editorPublish -Recurse -File -Filter 'libvlc.dll' | Select-Object -First 1
    $vlcPlugins = Get-ChildItem $editorPublish -Recurse -Directory -Filter 'plugins' |
        Where-Object { Test-Path (Join-Path $_.FullName 'access') } | Select-Object -First 1
    if (-not $libVlc -or -not $vlcPlugins) {
        throw 'The Windows editor package does not contain the expected LibVLC runtime and plugins.'
    }

    Compress-Package $serverPublish (Join-Path $outputRoot "NeonStage-Server-$suffix-windows-x64.zip")
    Compress-Package $editorPublish (Join-Path $outputRoot "NeonStage-LyricsEditor-$suffix-windows-x64.zip")

    if (-not $SkipUnity) {
        $env:NEONSTAGE_VERSION = $Version
        $env:NEONSTAGE_BUILD_NUMBER = $BuildNumber.ToString()
        $env:NEONSTAGE_RELEASE_BUILD = '1'
        & "$repoRoot\scripts\windows\build-unity-stage-windows.ps1"
        if ($LASTEXITCODE -ne 0) { throw "Unity build failed ($LASTEXITCODE)." }

        $stageSource = "$repoRoot\src\Karaoke.Stage.Unity\Builds\Windows"
        if (-not (Test-Path "$stageSource\NeonStage.exe" -PathType Leaf)) {
            throw "Incomplete Unity Windows build: $stageSource"
        }
        $stagePackage = Join-Path $workRoot 'stage'
        New-Item -ItemType Directory -Force -Path $stagePackage | Out-Null
        Copy-Item "$stageSource\*" -Destination $stagePackage -Recurse
        Copy-ReleaseNotices $stagePackage
        Compress-Package $stagePackage (Join-Path $outputRoot "NeonStage-Stage-$suffix-windows-x64.zip")
    }
}
finally {
    if (Test-Path $workRoot) { Remove-Item -Recurse -Force $workRoot }
}

Write-Host "Windows release packages created in: $outputRoot"
