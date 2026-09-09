#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
public static class VibeNetQualificationBuild
{
    public static void Build()
    {
        PlayerSettings.SetApiCompatibilityLevel(BuildTargetGroup.Standalone, ApiCompatibilityLevel.NET_Standard);
        PlayerSettings.SetScriptingBackend(BuildTargetGroup.Standalone, ScriptingImplementation.Mono2x);
        PlayerSettings.companyName = "VibeNet";
        PlayerSettings.productName = "VibeNetQualification";
        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene);
        new GameObject("Qualification").AddComponent<VibeNetQualification>();
        EditorSceneManager.SaveScene(scene, "Assets/Qualification.unity");
        var result = BuildPipeline.BuildPlayer(new BuildPlayerOptions {
            scenes = new[] { "Assets/Qualification.unity" },
            locationPathName = "Build/VibeNetQualification.exe",
            target = BuildTarget.StandaloneWindows64,
            options = BuildOptions.None
        });
        if (result.summary.result != BuildResult.Succeeded) throw new System.Exception("Build failed: " + result.summary.result);
    }
}
#endif
