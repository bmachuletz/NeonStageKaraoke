$ErrorActionPreference = "Stop"
$repoRoot = (Resolve-Path "$PSScriptRoot\..\..").Path
$unity = $env:UNITY_EDITOR
if (-not $unity) { throw "UNITY_EDITOR muss auf Unity.exe zeigen." }
& $unity -batchmode -quit -projectPath "$repoRoot\src\Karaoke.Stage.Unity" `
  -executeMethod NeonStage.Stage.Editor.NeonStageAndroidBuild.BuildWindows `
  -logFile "$repoRoot\Builds\windows-unity.log"
if ($LASTEXITCODE -ne 0) { throw "Unity-Build fehlgeschlagen ($LASTEXITCODE)." }
