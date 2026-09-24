param(
    [ValidatePattern('^[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z.-]+)?$')]
    [string]$Version,
    [switch]$ReuseBuild,
    [switch]$SkipUnity,
    [switch]$SkipTests,
    [switch]$AllowDirty,
    [switch]$Publish,
    [switch]$NoPublish,
    [string]$FfmpegExe = $env:FFMPEG_EXE
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repoRoot = (Resolve-Path "$PSScriptRoot\..\..").Path
$versionFile = Join-Path $repoRoot 'release-version.env'
$artifactRoot = Join-Path $repoRoot 'artifacts\releases'

function Invoke-Checked {
    param([string]$Command, [string[]]$Arguments)
    $global:LASTEXITCODE = 0
    & $Command @Arguments
    $exitCode = [int]$global:LASTEXITCODE
    if ($exitCode -ne 0) {
        throw "Command failed ($exitCode): $Command $($Arguments -join ' ')"
    }
}

function Read-ReleaseState {
    if (-not (Test-Path $versionFile -PathType Leaf)) {
        throw "Versionsdatei fehlt: $versionFile"
    }
    $values = @{}
    foreach ($line in Get-Content $versionFile) {
        if ($line -match '^([A-Z]+)=(.+)$') { $values[$Matches[1]] = $Matches[2].Trim() }
    }
    if (-not $values.ContainsKey('VERSION') -or
        $values['VERSION'] -notmatch '^[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z.-]+)?$' -or
        -not $values.ContainsKey('BUILD') -or $values['BUILD'] -notmatch '^[0-9]+$') {
        throw "Ungültige Versionsdatei: $versionFile"
    }
    return [pscustomobject]@{ Version = [string]$values['VERSION']; Build = [int]$values['BUILD'] }
}

function Write-ReleaseState {
    param([string]$ProductVersion, [int]$BuildNumber)
    $content = @(
        '# Product version follows Semantic Versioning. BUILD is increased once after',
        '# every successful release-build run and is shared by all platform artifacts.',
        "VERSION=$ProductVersion",
        "BUILD=$BuildNumber"
    ) -join "`n"
    $temporary = $versionFile + '.tmp'
    [IO.File]::WriteAllText($temporary, $content + "`n", [Text.UTF8Encoding]::new($false))
    Move-Item $temporary $versionFile -Force
}

function Add-ReleaseMetadata {
    param([string]$Directory, [string]$ProductVersion, [int]$BuildNumber, [string]$Commit)
    Copy-Item "$repoRoot\LICENSE", "$repoRoot\NOTICE", "$repoRoot\THIRD_PARTY_NOTICES.md", `
        "$repoRoot\ACKNOWLEDGEMENTS.md" -Destination $Directory
    $metadata = @(
        "NEONSTAGE_VERSION=$ProductVersion",
        "NEONSTAGE_BUILD=$BuildNumber",
        "GIT_COMMIT=$Commit",
        'PLATFORMS=windows',
        "CREATED_UTC=$([DateTime]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ssZ'))"
    ) -join "`n"
    [IO.File]::WriteAllText((Join-Path $Directory 'RELEASE-METADATA.txt'),
        $metadata + "`n", [Text.UTF8Encoding]::new($false))
    $checksums = Get-ChildItem $Directory -File | Where-Object Name -ne 'SHA256SUMS' |
        Sort-Object Name | ForEach-Object {
            $hash = (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
            "$hash  $($_.Name)"
        }
    [IO.File]::WriteAllLines((Join-Path $Directory 'SHA256SUMS'), $checksums,
        [Text.UTF8Encoding]::new($false))
}

if (-not [Runtime.InteropServices.RuntimeInformation]::IsOSPlatform(
        [Runtime.InteropServices.OSPlatform]::Windows)) {
    throw 'Dieses Release-Skript muss unter Windows ausgeführt werden.'
}
if ($Publish -and $NoPublish) { throw '-Publish und -NoPublish schließen sich gegenseitig aus.' }

$state = Read-ReleaseState
$productVersion = if ($Version) { $Version } else { $state.Version }
if ($ReuseBuild) {
    if ($productVersion -ne $state.Version -or $state.Build -lt 1) {
        throw '-ReuseBuild benötigt die gespeicherte Version mit BUILD > 0.'
    }
    $buildNumber = $state.Build
} elseif ($productVersion -eq $state.Version) {
    $buildNumber = $state.Build + 1
} else {
    $buildNumber = 1
}

if (-not $AllowDirty) {
    $status = & git -C $repoRoot status --porcelain
    if ($global:LASTEXITCODE -ne 0) { throw 'Git-Status konnte nicht gelesen werden.' }
    if ($status) { throw 'Der Git-Arbeitsbaum ist nicht sauber. Committe Änderungen oder verwende -AllowDirty.' }
}

$releaseName = "v$productVersion-build.$buildNumber"
$target = Join-Path $artifactRoot $releaseName
if (Test-Path $target) { throw "Release-Ziel existiert bereits: $target" }
New-Item -ItemType Directory -Force -Path $artifactRoot | Out-Null
$partial = Join-Path $artifactRoot ('.' + $releaseName + '.partial-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $partial | Out-Null

Write-Host "Neon Stage Windows Release $productVersion (Build $buildNumber)" -ForegroundColor Cyan
try {
    $prepareArguments = @{}
    if ($SkipUnity) { $prepareArguments['SkipUnity'] = $true }
    if ($FfmpegExe) { $prepareArguments['FfmpegExe'] = $FfmpegExe }
    & "$repoRoot\scripts\windows\prepare-build.ps1" @prepareArguments
    if (-not $FfmpegExe -and $env:FFMPEG_EXE) { $FfmpegExe = $env:FFMPEG_EXE }

    if (-not $SkipTests) {
        foreach ($project in @(
            'src\Karaoke.Server\Karaoke.Server.csproj',
            'src\Karaoke.App.Desktop\Karaoke.App.Desktop.csproj',
            'tests\Karaoke.Editor.Core.Tests\Karaoke.Editor.Core.Tests.csproj',
            'tests\Karaoke.Server.PlaybackTests\Karaoke.Server.PlaybackTests.csproj')) {
            Invoke-Checked 'dotnet' @('build', (Join-Path $repoRoot $project), '-c', 'Release', '--no-restore')
        }
        Invoke-Checked 'dotnet' @('run', '--project',
            "$repoRoot\tests\Karaoke.Editor.Core.Tests\Karaoke.Editor.Core.Tests.csproj",
            '-c', 'Release', '--no-build')
        Invoke-Checked 'dotnet' @('run', '--project',
            "$repoRoot\tests\Karaoke.Server.PlaybackTests\Karaoke.Server.PlaybackTests.csproj",
            '-c', 'Release', '--no-build')
    }

    $buildArguments = @{
        OutputDirectory = $partial
        Version = $productVersion
        BuildNumber = $buildNumber
        SkipPrepare = $true
    }
    if ($SkipUnity) { $buildArguments['SkipUnity'] = $true }
    if ($FfmpegExe) { $buildArguments['FfmpegExe'] = $FfmpegExe }
    & "$repoRoot\scripts\windows\build-release.ps1" @buildArguments

    $commit = (& git -C $repoRoot rev-parse HEAD).Trim()
    if ($global:LASTEXITCODE -ne 0) { throw 'Git-Commit konnte nicht ermittelt werden.' }
    Add-ReleaseMetadata $partial $productVersion $buildNumber $commit
    Move-Item $partial $target
    if (-not $ReuseBuild) { Write-ReleaseState $productVersion $buildNumber }
}
catch {
    if (Test-Path $partial) { Remove-Item $partial -Recurse -Force }
    throw
}

Write-Host "Release erfolgreich: $target" -ForegroundColor Green
$shouldPublish = $Publish
if (-not $Publish -and -not $NoPublish) {
    $answer = Read-Host "Release $releaseName jetzt zu GitHub hochladen? [j/N]"
    $shouldPublish = $answer -match '^(j|ja|y|yes)$'
}
if ($shouldPublish) {
    if (-not (Get-Command gh.exe -ErrorAction SilentlyContinue) -and
        -not (Get-Command gh -ErrorAction SilentlyContinue)) {
        throw "GitHub CLI 'gh' fehlt. Das lokale Release bleibt unter $target."
    }
    Invoke-Checked 'gh' @('auth', 'status')
    $assets = @(Get-ChildItem $target -File | ForEach-Object FullName)
    & gh release view $releaseName *> $null
    if ($global:LASTEXITCODE -eq 0) {
        Invoke-Checked 'gh' (@('release', 'upload', $releaseName) + $assets + @('--clobber'))
    } else {
        Invoke-Checked 'gh' (@('release', 'create', $releaseName) + $assets + @(
            '--target', $commit, '--title', "Neon Stage $productVersion (Build $buildNumber)",
            '--generate-notes', '--prerelease'))
    }
    Write-Host "GitHub Release veröffentlicht: $releaseName" -ForegroundColor Green
}
