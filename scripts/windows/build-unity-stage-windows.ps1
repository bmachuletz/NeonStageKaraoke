$ErrorActionPreference = "Stop"
$repoRoot = (Resolve-Path "$PSScriptRoot\..\..").Path
$unity = $env:UNITY_EDITOR
if (-not $unity) {
  $unity = Get-ChildItem "$env:ProgramFiles\Unity\Hub\Editor\*\Editor\Unity.exe" `
    -File -ErrorAction SilentlyContinue |
    Sort-Object FullName -Descending |
    Select-Object -First 1 -ExpandProperty FullName
}
if (-not $unity -or -not (Test-Path $unity -PathType Leaf)) {
  throw "Unity.exe wurde nicht gefunden. Installiere Unity 6 mit Windows Build Support oder setze UNITY_EDITOR."
}
New-Item -ItemType Directory -Force -Path "$repoRoot\Builds" | Out-Null
& $unity -batchmode -quit -projectPath "$repoRoot\src\Karaoke.Stage.Unity" `
  -executeMethod NeonStage.Stage.Editor.NeonStageAndroidBuild.BuildWindows `
  -logFile "$repoRoot\Builds\windows-unity.log"
if ($LASTEXITCODE -ne 0) { throw "Unity-Build fehlgeschlagen ($LASTEXITCODE)." }
