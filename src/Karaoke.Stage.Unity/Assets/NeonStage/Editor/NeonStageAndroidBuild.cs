#if UNITY_EDITOR
using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace NeonStage.Stage.Editor
{

public static class NeonStageAndroidBuild
{
    private const string ScenePath = "Assets/NeonStage/Scenes/Stage.unity";

    [MenuItem("Neon Stage/Build Shell S2 ARM32 APK")]
    public static void BuildShellS2()
    {
        EnsureBrandingAssets();
        EnsureScene();
        ConfigureCommonPlayerSettings();
        PlayerSettings.productName = "Neon Stage Karaoke";
        PlayerSettings.SetApplicationIdentifier(NamedBuildTarget.Android, "de.neonstage.stage");
        PlayerSettings.Android.minSdkVersion = AndroidSdkVersions.AndroidApiLevel26;
        PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARMv7;
        PlayerSettings.Android.forceInternetPermission = true;
        PlayerSettings.Android.forceSDCardPermission = false;
        PlayerSettings.Android.buildApkPerCpuArchitecture = false;
        ApplyAndroidIcon();
        EditorUserBuildSettings.buildAppBundle = false;

        Directory.CreateDirectory("Builds/Android");
        var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
        {
            scenes = new[] { ScenePath },
            locationPathName = "Builds/Android/NeonStage-ShellS2-arm32.apk",
            target = BuildTarget.Android,
            options = BuildOptions.Development
        });
        if (report.summary.result != BuildResult.Succeeded)
            throw new BuildFailedException($"Shell-S2-Build fehlgeschlagen: {report.summary.result}");
        Debug.Log($"NeonStage Shell S2 APK: {Path.GetFullPath("Builds/Android/NeonStage-ShellS2-arm32.apk")}");
    }

    [MenuItem("Neon Stage/Build Linux Stage")]
    public static void BuildLinux()
    {
        EnsureBrandingAssets();
        EnsureScene();
        ConfigureCommonPlayerSettings();
        PlayerSettings.productName = "Neon Stage Karaoke";
        PlayerSettings.SetApplicationIdentifier(NamedBuildTarget.Standalone, "de.neonstage.stage");
        Directory.CreateDirectory("Builds/Linux");
        var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
        {
            scenes = new[] { ScenePath },
            locationPathName = "Builds/Linux/NeonStage",
            target = BuildTarget.StandaloneLinux64,
            options = BuildOptions.None
        });
        if (report.summary.result != BuildResult.Succeeded)
            throw new BuildFailedException($"Linux-Build fehlgeschlagen: {report.summary.result}");
        Debug.Log($"NeonStage Linux Stage: {Path.GetFullPath("Builds/Linux/NeonStage")}");
    }

    private static void ConfigureCommonPlayerSettings()
    {
        // NeonStage wird im lokalen Party-Netz bewusst per HTTP betrieben.
        PlayerSettings.insecureHttpOption = InsecureHttpOption.AlwaysAllowed;
        PlayerSettings.runInBackground = true;
        PlayerSettings.SplashScreen.backgroundColor = new Color(.025f, .004f, .045f);
        PlayerSettings.SplashScreen.show = true;
    }

    private static void EnsureBrandingAssets()
    {
        const string destination = "Assets/Resources/NeonStageIcon.png";
        var source = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", "Karaoke.App", "Assets", "neon-stage-icon.png"));
        if (!File.Exists(source)) throw new BuildFailedException($"Neon-Stage-Icon fehlt: {source}");
        Directory.CreateDirectory("Assets/Resources");
        File.Copy(source, destination, true);
        AssetDatabase.ImportAsset(destination, ImportAssetOptions.ForceUpdate);
        if (AssetImporter.GetAtPath(destination) is TextureImporter importer)
        {
            importer.textureType = TextureImporterType.Sprite;
            importer.spriteImportMode = SpriteImportMode.Single;
            importer.alphaIsTransparency = true;
            importer.mipmapEnabled = false;
            importer.maxTextureSize = 1024;
            importer.SaveAndReimport();
        }
    }

    private static void ApplyAndroidIcon()
    {
        var icon = AssetDatabase.LoadAssetAtPath<Texture2D>("Assets/Resources/NeonStageIcon.png");
        if (icon == null) throw new BuildFailedException("Android-App-Icon konnte nicht importiert werden.");
        PlayerSettings.SetIcons(NamedBuildTarget.Android, new[] { icon }, IconKind.Application);
    }

    private static void EnsureScene()
    {
        Directory.CreateDirectory("Assets/NeonStage/Scenes");
        if (File.Exists(ScenePath)) return;
        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        EditorSceneManager.SaveScene(scene, ScenePath);
        AssetDatabase.Refresh();
    }
}
}
#endif
