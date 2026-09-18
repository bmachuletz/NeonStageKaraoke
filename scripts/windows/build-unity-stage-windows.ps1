param([switch]$SkipPrepare)

$ErrorActionPreference = "Stop"
$repoRoot = (Resolve-Path "$PSScriptRoot\..\..").Path
if (-not $SkipPrepare) {
  & "$repoRoot\scripts\windows\prepare-build.ps1" -UnityOnly
  if ($LASTEXITCODE -ne 0) { throw "Windows-Prepare fehlgeschlagen ($LASTEXITCODE)." }
}
$unity = $env:UNITY_EDITOR
if (-not $unity) {
  $versionFile = "$repoRoot\src\Karaoke.Stage.Unity\ProjectSettings\ProjectVersion.txt"
  $versionLine = Get-Content $versionFile | Where-Object { $_ -like 'm_EditorVersion: *' } |
    Select-Object -First 1
  if (-not $versionLine) { throw "Unity-Projektversion konnte nicht gelesen werden: $versionFile" }
  $unityVersion = $versionLine.Substring('m_EditorVersion: '.Length).Trim()
  $unity = Join-Path $env:ProgramFiles "Unity\Hub\Editor\$unityVersion\Editor\Unity.exe"
}
if (-not $unity -or -not (Test-Path $unity -PathType Leaf)) {
  throw "Passende Unity.exe wurde nicht gefunden. Installiere die Projektversion mit Windows Build Support oder setze UNITY_EDITOR."
}
New-Item -ItemType Directory -Force -Path "$repoRoot\Builds" | Out-Null
& $unity -batchmode -quit -projectPath "$repoRoot\src\Karaoke.Stage.Unity" `
  -executeMethod NeonStage.Stage.Editor.NeonStageAndroidBuild.BuildWindows `
  -logFile "$repoRoot\Builds\windows-unity.log"
if ($LASTEXITCODE -ne 0) { throw "Unity-Build fehlgeschlagen ($LASTEXITCODE)." }
Write-Host "Build: $repoRoot\src\Karaoke.Stage.Unity\Builds\Windows\NeonStage.exe"
