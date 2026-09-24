param(
    [switch]$UnityOnly,
    [switch]$SkipUnity,
    [switch]$NoAutoInstall,
    [string]$FfmpegExe = $env:FFMPEG_EXE
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repoRoot = (Resolve-Path "$PSScriptRoot\..\..").Path
$unityProject = Join-Path $repoRoot 'src\Karaoke.Stage.Unity'
$projectVersionFile = Join-Path $unityProject 'ProjectSettings\ProjectVersion.txt'
$windowsTools = Join-Path $repoRoot '.tools\windows'

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

function Refresh-ProcessPath {
    $machinePath = [Environment]::GetEnvironmentVariable('Path', 'Machine')
    $userPath = [Environment]::GetEnvironmentVariable('Path', 'User')
    $env:Path = (@($machinePath, $userPath, $env:Path) |
        Where-Object { $_ } | Select-Object -Unique) -join ';'
}

function Resolve-DotNet {
    $command = Get-Command dotnet.exe -ErrorAction SilentlyContinue
    if ($command) { return $command.Source }
    foreach ($candidate in @(
        (Join-Path $env:ProgramFiles 'dotnet\dotnet.exe'),
        (Join-Path $windowsTools 'dotnet\dotnet.exe')
    )) {
        if ($candidate -and (Test-Path $candidate -PathType Leaf)) { return $candidate }
    }
    return $null
}

function Test-DotNet10Sdk {
    param([string]$DotNetExe)
    if (-not $DotNetExe) { return $false }
    try {
        $sdks = Invoke-NativeChecked -FilePath $DotNetExe -Description '.NET-SDK-Prüfung' `
            -ArgumentList @('--list-sdks')
        return [bool]($sdks | Where-Object { $_ -match '^10\.' } | Select-Object -First 1)
    }
    catch { return $false }
}

function Install-WinGetPackage {
    param([string]$PackageId, [string]$Description)
    $winget = Get-Command winget.exe -ErrorAction SilentlyContinue
    if (-not $winget) { return $false }
    Write-Host "Installiere $Description über WinGet ($PackageId) ..."
    try {
        Invoke-NativeChecked -FilePath $winget.Source -Description "Installation von $Description" `
            -ArgumentList @('install', '--id', $PackageId, '--exact', '--source', 'winget', '--silent',
                '--accept-package-agreements', '--accept-source-agreements', '--disable-interactivity') | Out-Host
        Refresh-ProcessPath
        return $true
    }
    catch {
        Write-Warning "WinGet konnte $Description nicht installieren: $($_.Exception.Message)"
        return $false
    }
}

function Install-DotNetLocally {
    $installRoot = Join-Path $windowsTools 'dotnet'
    $installScript = Join-Path $windowsTools 'dotnet-install.ps1'
    New-Item -ItemType Directory -Force -Path $windowsTools | Out-Null
    Write-Host "Installiere .NET SDK 10 lokal nach $installRoot ..."
    Invoke-WebRequest 'https://dot.net/v1/dotnet-install.ps1' -OutFile $installScript -UseBasicParsing
    & $installScript -Channel '10.0' -InstallDir $installRoot -NoPath | Out-Host
    $dotnetExe = Join-Path $installRoot 'dotnet.exe'
    if (-not (Test-Path $dotnetExe -PathType Leaf)) {
        throw "Lokale .NET-Installation wurde nicht vollständig angelegt: $dotnetExe"
    }
    $env:DOTNET_ROOT = $installRoot
    $env:Path = "$installRoot;$env:Path"
    return $dotnetExe
}

function Resolve-Ffmpeg {
    param([string]$ExplicitPath)
    if ($ExplicitPath -and (Test-Path $ExplicitPath -PathType Leaf)) {
        return (Resolve-Path $ExplicitPath).Path
    }
    $command = Get-Command ffmpeg.exe -ErrorAction SilentlyContinue
    if ($command) { return $command.Source }
    $wingetLink = Join-Path $env:LOCALAPPDATA 'Microsoft\WinGet\Links\ffmpeg.exe'
    if (Test-Path $wingetLink -PathType Leaf) { return $wingetLink }
    $local = Get-ChildItem (Join-Path $windowsTools 'ffmpeg') -Recurse -File -Filter 'ffmpeg.exe' `
        -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($local) { return $local.FullName }
    $wingetPackageRoot = Join-Path $env:LOCALAPPDATA 'Microsoft\WinGet\Packages'
    $installed = Get-ChildItem $wingetPackageRoot -Recurse -File -Filter 'ffmpeg.exe' `
        -ErrorAction SilentlyContinue | Where-Object { $_.FullName -like '*Gyan.FFmpeg*' } |
        Select-Object -First 1
    if ($installed) { return $installed.FullName }
    return $null
}

function Install-FfmpegLocally {
    $ffmpegRoot = Join-Path $windowsTools 'ffmpeg'
    $archive = Join-Path $windowsTools 'ffmpeg-release-essentials.zip'
    New-Item -ItemType Directory -Force -Path $windowsTools | Out-Null
    if (Test-Path $ffmpegRoot) { Remove-Item $ffmpegRoot -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $ffmpegRoot | Out-Null
    Write-Host "Installiere FFmpeg lokal nach $ffmpegRoot ..."
    Invoke-WebRequest 'https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip' `
        -OutFile $archive -UseBasicParsing
    Expand-Archive -Path $archive -DestinationPath $ffmpegRoot -Force
    Remove-Item $archive -Force
    $ffmpeg = Resolve-Ffmpeg $null
    if (-not $ffmpeg) { throw 'Die lokale FFmpeg-Installation enthält keine ffmpeg.exe.' }
    return $ffmpeg
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
    if ($env:UNITY_EDITOR -and (Test-Path $env:UNITY_EDITOR -PathType Leaf)) {
        return $env:UNITY_EDITOR
    }

    $hubRoot = Join-Path $env:ProgramFiles 'Unity\Hub\Editor'
    $exact = Join-Path $hubRoot "$unityVersion\Editor\Unity.exe"
    if (Test-Path $exact -PathType Leaf) { return $exact }
    if (-not (Test-Path $hubRoot -PathType Container)) { return $null }

    # Das Projekt benötigt Unity 6, aber nicht exakt dieselbe Patchversion.
    # Bevorzugt wird die neueste installierte 6000.x-Version.
    $installations = Get-ChildItem $hubRoot -Directory -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -match '^6000\.(\d+)\.(\d+)' } |
        Sort-Object {
            if ($_.Name -match '^6000\.(\d+)\.(\d+)') {
                [Version]::new(6000, [int]$Matches[1], [int]$Matches[2])
            } else { [Version]::new(0, 0, 0) }
        } -Descending
    foreach ($installation in $installations) {
        $candidate = Join-Path $installation.FullName 'Editor\Unity.exe'
        if (Test-Path $candidate -PathType Leaf) { return $candidate }
    }
    return $null
}

if (-not $SkipUnity) {
    $unity = Resolve-UnityEditor
    if (-not $unity -or -not (Test-Path $unity -PathType Leaf)) {
        throw "Keine installierte Unity-6000.x-Version wurde gefunden. Installiere Unity 6 oder setze UNITY_EDITOR."
    }
    $env:UNITY_EDITOR = $unity
    $windowsSupport = Join-Path (Split-Path $unity -Parent) 'Data\PlaybackEngines\WindowsStandaloneSupport'
    if (-not (Test-Path $windowsSupport -PathType Container)) {
        throw "Unity-Modul 'Windows Build Support (Mono)' fehlt: $windowsSupport"
    }
    New-Item -ItemType Directory -Force -Path "$repoRoot\Builds" | Out-Null
    Write-Host "Prepare Windows: Projekt $unityVersion · verwendet $unity"
    Invoke-NativeChecked -FilePath $unity -Description 'Unity-Prepare' -ArgumentList @(
        '-batchmode', '-nographics', '-quit', '-projectPath', $unityProject,
        '-logFile', "$repoRoot\Builds\windows-prepare.log"
    )
}

if (-not $UnityOnly) {
    Refresh-ProcessPath
    $dotnetExe = Resolve-DotNet
    if (-not (Test-DotNet10Sdk $dotnetExe)) {
        if ($NoAutoInstall) { throw '.NET SDK 10 wurde nicht gefunden.' }
        $installedWithWinget = Install-WinGetPackage 'Microsoft.DotNet.SDK.10' '.NET SDK 10'
        $dotnetExe = Resolve-DotNet
        if (-not $installedWithWinget -or -not (Test-DotNet10Sdk $dotnetExe)) {
            $dotnetExe = Install-DotNetLocally
        }
    }
    if (-not (Test-DotNet10Sdk $dotnetExe)) {
        throw '.NET SDK 10 ist auch nach der automatischen Installation nicht verfügbar.'
    }

    $FfmpegExe = Resolve-Ffmpeg $FfmpegExe
    if (-not $FfmpegExe) {
        if ($NoAutoInstall) { throw 'ffmpeg.exe wurde nicht gefunden.' }
        $installedWithWinget = Install-WinGetPackage 'Gyan.FFmpeg' 'FFmpeg'
        $FfmpegExe = Resolve-Ffmpeg $null
        if (-not $installedWithWinget -or -not $FfmpegExe) {
            $FfmpegExe = Install-FfmpegLocally
        }
    }
    if (-not $FfmpegExe -or -not (Test-Path $FfmpegExe -PathType Leaf)) {
        throw 'ffmpeg.exe ist auch nach der automatischen Installation nicht verfügbar.'
    }
    $env:FFMPEG_EXE = (Resolve-Path $FfmpegExe).Path

    Invoke-NativeChecked -FilePath $dotnetExe -Description 'Restore von Karaoke.Server' `
        -ArgumentList @('restore', "$repoRoot\src\Karaoke.Server\Karaoke.Server.csproj")
    Invoke-NativeChecked -FilePath $dotnetExe -Description 'Restore von Karaoke.App.Desktop' `
        -ArgumentList @('restore', "$repoRoot\src\Karaoke.App.Desktop\Karaoke.App.Desktop.csproj")
    Invoke-NativeChecked -FilePath $dotnetExe -Description 'Restore des portablen Windows-Launchers' `
        -ArgumentList @('restore', "$repoRoot\src\NeonStage.PortableLauncher\NeonStage.PortableLauncher.csproj")
    Invoke-NativeChecked -FilePath $dotnetExe -Description 'Restore der Editor-Core-Tests' `
        -ArgumentList @('restore', "$repoRoot\tests\Karaoke.Editor.Core.Tests\Karaoke.Editor.Core.Tests.csproj")
    Invoke-NativeChecked -FilePath $dotnetExe -Description 'Restore der Server-Integrationstests' `
        -ArgumentList @('restore', "$repoRoot\tests\Karaoke.Server.PlaybackTests\Karaoke.Server.PlaybackTests.csproj")
    Write-Host ".NET SDK 10: $dotnetExe"
    Write-Host "FFmpeg: $env:FFMPEG_EXE"
}

Write-Host 'Windows-Prepare erfolgreich.'
