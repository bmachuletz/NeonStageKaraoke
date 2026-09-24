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

$prepareArguments = @{}
if ($SkipUnity) { $prepareArguments['SkipUnity'] = $true }
if ($FfmpegExe) { $prepareArguments['FfmpegExe'] = $FfmpegExe }
& "$repoRoot\scripts\windows\prepare-build.ps1" @prepareArguments
# PowerShell script failures propagate through ErrorActionPreference=Stop.
# LASTEXITCODE is reserved for native executables and can be undefined here.
if (-not $FfmpegExe -and $env:FFMPEG_EXE) { $FfmpegExe = $env:FFMPEG_EXE }

function Invoke-Checked {
    param([string]$Command, [string[]]$Arguments)
    $global:LASTEXITCODE = 0
    & $Command @Arguments
    $exitCode = [int]$global:LASTEXITCODE
    if ($exitCode -ne 0) {
        throw "Command failed ($exitCode): $Command $($Arguments -join ' ')"
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

function New-PortableExecutable {
    param(
        [string]$PayloadArchive,
        [string]$Profile,
        [string]$EntryPoint,
        [string]$ArtifactName,
        [ValidateSet('Exe', 'WinExe')][string]$LauncherOutputType = 'WinExe'
    )
    $launcherName = [IO.Path]::GetFileNameWithoutExtension($ArtifactName)
    $launcherOutput = Join-Path $workRoot ("launcher-" + $Profile)
    Invoke-Checked 'dotnet' @(
        'publish', "$repoRoot\src\NeonStage.PortableLauncher\NeonStage.PortableLauncher.csproj",
        '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true',
        '-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true',
        '-p:EnableCompressionInSingleFile=true', '-p:DebugType=None', '-p:DebugSymbols=false',
        "-p:Version=$Version", "-p:InformationalVersion=$Version+build.$BuildNumber",
        "-p:AssemblyName=$launcherName", "-p:OutputType=$LauncherOutputType",
        "-p:LauncherProfile=$Profile", "-p:LauncherEntryPoint=$EntryPoint",
        "-p:PayloadArchive=$PayloadArchive", '-o', $launcherOutput
    )
    $launcher = Join-Path $launcherOutput ($launcherName + '.exe')
    if (-not (Test-Path $launcher -PathType Leaf)) {
        throw "Portable launcher was not generated: $launcher"
    }
    Copy-Item $launcher -Destination (Join-Path $outputRoot $ArtifactName)
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
        '-p:PublishSingleFile=true',
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
    $vlcAudioPlugin = Get-ChildItem $editorPublish -Recurse -File -Filter 'libmmdevice_plugin.dll' |
        Select-Object -First 1
    if (-not $libVlc -or -not $vlcPlugins -or -not $vlcAudioPlugin) {
        throw 'The Windows editor package does not contain the expected LibVLC runtime and plugins.'
    }

    $serverArchive = Join-Path $outputRoot "NeonStage-Server-$suffix-windows-x64.zip"
    $editorArchive = Join-Path $outputRoot "NeonStage-LyricsEditor-$suffix-windows-x64.zip"
    Compress-Package $serverPublish $serverArchive
    Compress-Package $editorPublish $editorArchive
    New-PortableExecutable -PayloadArchive $serverArchive -Profile 'server' `
        -EntryPoint 'Karaoke.Server.exe' -ArtifactName "NeonStage-Server-$suffix-windows-x64.exe" `
        -LauncherOutputType 'Exe'
    New-PortableExecutable -PayloadArchive $editorArchive -Profile 'editor' `
        -EntryPoint 'Karaoke.App.Desktop.exe' `
        -ArtifactName "NeonStage-LyricsEditor-$suffix-windows-x64.exe"

    if (-not $SkipUnity) {
        $env:NEONSTAGE_VERSION = $Version
        $env:NEONSTAGE_BUILD_NUMBER = $BuildNumber.ToString()
        $env:NEONSTAGE_RELEASE_BUILD = '1'
        try {
            & "$repoRoot\scripts\windows\build-unity-stage-windows.ps1" -SkipPrepare
            # Das Unter-Skript prüft Unitys nativen Exitcode und die erzeugte
            # NeonStage.exe selbst. $? ist hier unzuverlässig, weil ein intern
            # aufgerufener nativer Prozess den Wert trotz erfolgreichem Skript
            # auf false belassen kann.
        }
        catch {
            $unityLog = Join-Path $repoRoot 'Builds\windows-unity.log'
            if (Test-Path $unityLog -PathType Leaf) {
                Write-Host 'Letzte Meldungen aus dem Unity-Buildlog:' -ForegroundColor Yellow
                Get-Content $unityLog -Tail 40 | Out-Host
            }
            throw "Windows-Stage-Build fehlgeschlagen: $($_.Exception.Message)"
        }

        $stageSource = "$repoRoot\src\Karaoke.Stage.Unity\Builds\Windows"
        if (-not (Test-Path "$stageSource\NeonStage.exe" -PathType Leaf)) {
            throw "Incomplete Unity Windows build: $stageSource"
        }
        $stagePackage = Join-Path $workRoot 'stage'
        New-Item -ItemType Directory -Force -Path $stagePackage | Out-Null
        Copy-Item "$stageSource\*" -Destination $stagePackage -Recurse
        Copy-ReleaseNotices $stagePackage
        Copy-Item "$repoRoot\packaging\windows\Start-NeonStage-Stage.cmd" -Destination $stagePackage
        $stageArchive = Join-Path $outputRoot "NeonStage-Stage-$suffix-windows-x64.zip"
        Compress-Package $stagePackage $stageArchive
        New-PortableExecutable -PayloadArchive $stageArchive -Profile 'stage' `
            -EntryPoint 'NeonStage.exe' -ArtifactName "NeonStage-Stage-$suffix-windows-x64.exe"
    }
}
finally {
    if (Test-Path $workRoot) { Remove-Item -Recurse -Force $workRoot }
}

Write-Host "Windows release packages created in: $outputRoot"
