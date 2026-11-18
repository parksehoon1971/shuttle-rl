using System;
using System.Reflection;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEditor.PackageManager.Requests;
using UnityEngine;
using UnityEngine.Rendering;

namespace ShuttleRL.Editor
{
    /// <summary>
    /// Switches the project to URP automatically the first time it is opened:
    ///  1. installs com.unity.render-pipelines.universal (the version matching this editor),
    ///  2. creates a URP asset + Universal Renderer in Assets/ShuttleRL/Settings,
    ///  3. assigns it in Graphics settings and re-imports the models so their materials become URP/Lit.
    /// URP types are reached by reflection so this compiles before the package is installed.
    /// Run again any time with menu ShuttleRL → Set Up URP.
    /// </summary>
    [InitializeOnLoad]
    public static class ShuttleRLUrpSetup
    {
        const string Package = "com.unity.render-pipelines.universal";
        const string SettingsDir = "Assets/ShuttleRL/Settings";
        const string AssetPath = SettingsDir + "/ShuttleRL_URP.asset";
        const string RendererPath = SettingsDir + "/ShuttleRL_URP_Renderer.asset";
        const string ModelsDir = "Assets/ShuttleRL/Models";
        const string Asm = ", Unity.RenderPipelines.Universal.Runtime";
        const string SessionKey = "ShuttleRL.UrpSetupTried";

        static AddRequest addRequest;

        static ShuttleRLUrpSetup()
        {
            if (Application.isBatchMode) return;
            EditorApplication.delayCall += () => Run(false);
        }

        [MenuItem("ShuttleRL/Set Up URP")]
        static void RunFromMenu() { Run(true); }

        static Type UrpAssetType => Type.GetType("UnityEngine.Rendering.Universal.UniversalRenderPipelineAsset" + Asm);

        static void Run(bool force)
        {
            if (!force && GraphicsSettings.defaultRenderPipeline != null) return; // already on a pipeline
            if (UrpAssetType != null) { CreateAndAssign(); return; }
            // install once per editor session; the domain reload after install calls Run again
            if (addRequest != null || (!force && SessionState.GetBool(SessionKey, false))) return;
            SessionState.SetBool(SessionKey, true);
            Debug.Log("ShuttleRL: installing URP package (" + Package + ")...");
            addRequest = Client.Add(Package);
            EditorApplication.update += WaitForInstall;
        }

        static void WaitForInstall()
        {
            if (addRequest == null || !addRequest.IsCompleted) return;
            EditorApplication.update -= WaitForInstall;
            if (addRequest.Status == StatusCode.Success)
            {
                Debug.Log("ShuttleRL: URP " + addRequest.Result.version + " installed.");
            }
            else Debug.LogError("ShuttleRL: URP install failed: " + addRequest.Error?.message);
            addRequest = null;
        }

        static void CreateAndAssign()
        {
            var assetType = UrpAssetType;
            var asset = AssetDatabase.LoadAssetAtPath(AssetPath, assetType) as RenderPipelineAsset;
            if (asset == null)
            {
                if (!AssetDatabase.IsValidFolder(SettingsDir)) AssetDatabase.CreateFolder("Assets/ShuttleRL", "Settings");
                var rendererType = Type.GetType("UnityEngine.Rendering.Universal.UniversalRendererData" + Asm);
                var baseRendererType = Type.GetType("UnityEngine.Rendering.Universal.ScriptableRendererData" + Asm);
                var rendererData = ScriptableObject.CreateInstance(rendererType);
                AssetDatabase.CreateAsset(rendererData, RendererPath);
                var create = assetType.GetMethod("Create", BindingFlags.Public | BindingFlags.Static, null, new[] { baseRendererType }, null);
                if (create == null) { Debug.LogError("ShuttleRL: UniversalRenderPipelineAsset.Create not found; create a URP asset by hand (Assets > Create > Rendering)."); return; }
                asset = (RenderPipelineAsset)create.Invoke(null, new object[] { rendererData });
                AssetDatabase.CreateAsset(asset, AssetPath);
                AssetDatabase.SaveAssets();
            }
            GraphicsSettings.defaultRenderPipeline = asset;
            int current = QualitySettings.GetQualityLevel();
            for (int i = 0; i < QualitySettings.names.Length; i++)
            {
                QualitySettings.SetQualityLevel(i, false);
                QualitySettings.renderPipeline = asset;
            }
            QualitySettings.SetQualityLevel(current, false);
            AssetDatabase.SaveAssets();
            // Re-import FBX models so their embedded materials are recreated with the URP Lit shader
            AssetDatabase.ImportAsset(ModelsDir, ImportAssetOptions.ImportRecursive | ImportAssetOptions.ForceUpdate);
            Debug.Log("ShuttleRL: project now uses URP (" + AssetPath + ").");
        }
    }
}
