using System;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Profile;
using UnityEditor.Build.Reporting;
using UnityEngine;

public static class BuildMetaScan
{
    public static void BuildQuest()
    {
        const string profilePath = "Assets/Settings/Build Profiles/Android.asset";
        const string outputPath = "Builds/MetaScan-Quest3S-6000.6.7f1-roi-fix.apk";
        BuildProfile profile = AssetDatabase.LoadAssetAtPath<BuildProfile>(profilePath);
        if (profile == null)
            throw new BuildFailedException("Android build profile is missing: " + profilePath);

        PlayerSettings.SetScriptingBackend(NamedBuildTarget.Android, ScriptingImplementation.IL2CPP);
        PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64;
        EditorUserBuildSettings.buildAppBundle = false;

        var options = new BuildPlayerWithProfileOptions
        {
            buildProfile = profile,
            locationPathName = outputPath,
            options = BuildOptions.StrictMode,
        };
        BuildReport report = BuildPipeline.BuildPlayer(options);
        if (report.summary.result != BuildResult.Succeeded)
            throw new BuildFailedException("Quest APK build failed: " + report.summary.result);
        Debug.Log("MetaScan Quest APK built: " + outputPath + " (" + report.summary.totalSize + " bytes)");
    }
}
