param([switch]$SkipPrepare)

$ErrorActionPreference = "Stop"
$repoRoot = (Resolve-Path "$PSScriptRoot\..\..").Path
if (-not $SkipPrepare) {
  & "$repoRoot\scripts\windows\prepare-build.ps1" -UnityOnly
}
$unity = $env:UNITY_EDITOR
if (-not $unity -or -not (Test-Path $unity -PathType Leaf)) {
  $versionFile = "$repoRoot\src\Karaoke.Stage.Unity\ProjectSettings\ProjectVersion.txt"
  $versionLine = Get-Content $versionFile | Where-Object { $_ -like 'm_EditorVersion: *' } |
    Select-Object -First 1
  if (-not $versionLine) { throw "Unity-Projektversion konnte nicht gelesen werden: $versionFile" }
  $unityVersion = $versionLine.Substring('m_EditorVersion: '.Length).Trim()
  $hubRoot = Join-Path $env:ProgramFiles 'Unity\Hub\Editor'
  $exact = Join-Path $hubRoot "$unityVersion\Editor\Unity.exe"
  if (Test-Path $exact -PathType Leaf) {
    $unity = $exact
  } elseif (Test-Path $hubRoot -PathType Container) {
    $unity = Get-ChildItem $hubRoot -Directory -ErrorAction SilentlyContinue |
      Where-Object { $_.Name -match '^6000\.(\d+)\.(\d+)' } |
      Sort-Object {
        if ($_.Name -match '^6000\.(\d+)\.(\d+)') {
          [Version]::new(6000, [int]$Matches[1], [int]$Matches[2])
        } else { [Version]::new(0, 0, 0) }
      } -Descending |
      ForEach-Object { Join-Path $_.FullName 'Editor\Unity.exe' } |
      Where-Object { Test-Path $_ -PathType Leaf } |
      Select-Object -First 1
  }
}
if (-not $unity -or -not (Test-Path $unity -PathType Leaf)) {
  throw "Keine Unity-6000.x-Version wurde gefunden. Installiere Unity 6 mit Windows Build Support oder setze UNITY_EDITOR."
}
New-Item -ItemType Directory -Force -Path "$repoRoot\Builds" | Out-Null
$global:LASTEXITCODE = 0
& $unity -batchmode -quit -projectPath "$repoRoot\src\Karaoke.Stage.Unity" `
  -executeMethod NeonStage.Stage.Editor.NeonStageAndroidBuild.BuildWindows `
  -logFile "$repoRoot\Builds\windows-unity.log"
$exitCode = [int]$global:LASTEXITCODE
if ($exitCode -ne 0) { throw "Unity-Build fehlgeschlagen ($exitCode)." }
if (-not (Test-Path "$repoRoot\src\Karaoke.Stage.Unity\Builds\Windows\NeonStage.exe" -PathType Leaf)) {
  throw "Unity meldete Erfolg, hat aber keine NeonStage.exe erzeugt. Siehe $repoRoot\Builds\windows-unity.log"
}
Write-Host "Build: $repoRoot\src\Karaoke.Stage.Unity\Builds\Windows\NeonStage.exe"
