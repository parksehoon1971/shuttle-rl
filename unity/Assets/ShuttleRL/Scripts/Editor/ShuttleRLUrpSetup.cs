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

        const int QualityVersion = 1;   // bump to re-apply ApplyQuality once on every machine
        static string QualityKey => "ShuttleRL.QualityVersion." + Application.dataPath;

        static ShuttleRLUrpSetup()
        {
            if (Application.isBatchMode) return;
            EditorApplication.delayCall += () =>
            {
                Run(false);
                if (GraphicsSettings.defaultRenderPipeline != null && EditorPrefs.GetInt(QualityKey, 0) < QualityVersion) ApplyQuality();
            };
        }

        /// <summary>
        /// High quality settings on the active URP asset and its renderer: 4 soft shadow cascades over 60 m at 4096,
        /// MSAA 4x, HDR, and Screen Space Ambient Occlusion. Fields are set through SerializedObject so this compiles
        /// without the URP package. Materials, lights and post-processing are set at runtime by RackLook.
        /// </summary>
        [MenuItem("ShuttleRL/Apply High Quality Look")]
        public static void ApplyQuality()
        {
            var asset = GraphicsSettings.defaultRenderPipeline;
            if (asset == null) { Debug.LogWarning("ShuttleRL: no render pipeline asset assigned yet (ShuttleRL > Set Up URP)."); return; }
            var so = new SerializedObject(asset);
            SetFloat(so, "m_ShadowDistance", 60f);
            SetInt(so, "m_ShadowCascadeCount", 4);
            SetBool(so, "m_SoftShadowsSupported", true);
            SetInt(so, "m_MainLightShadowmapResolution", 4096);
            SetBool(so, "m_MainLightShadowsSupported", true);
            SetBool(so, "m_AdditionalLightShadowsSupported", true);
            SetInt(so, "m_MSAA", 4);
            SetBool(so, "m_SupportsHDR", true);
            SetFloat(so, "m_RenderScale", 1f);
            SetBool(so, "m_RequireDepthTexture", true);
            so.ApplyModifiedProperties();
            EditorUtility.SetDirty(asset);

            // SSAO renderer feature on every renderer of the asset
            var list = so.FindProperty("m_RendererDataList");
            if (list != null && list.isArray)
                for (int i = 0; i < list.arraySize; i++)
                {
                    var data = list.GetArrayElementAtIndex(i).objectReferenceValue as ScriptableObject;
                    if (data != null) AddSsao(data);
                }
            QualitySettings.anisotropicFiltering = AnisotropicFiltering.ForceEnable;
            AssetDatabase.SaveAssets();
            EditorPrefs.SetInt(QualityKey, QualityVersion);
            Debug.Log("ShuttleRL: high quality look applied (soft shadows 4096 / 4 cascades / 60 m, MSAA 4x, HDR, SSAO).");
        }

        static void AddSsao(ScriptableObject data)
        {
            var ssaoType = Type.GetType("UnityEngine.Rendering.Universal.ScreenSpaceAmbientOcclusion" + Asm);
            if (ssaoType == null) return;
            var so = new SerializedObject(data);
            var features = so.FindProperty("m_RendererFeatures");
            var map = so.FindProperty("m_RendererFeatureMap");
            if (features == null || map == null) return;
            for (int i = 0; i < features.arraySize; i++)
            {
                var f = features.GetArrayElementAtIndex(i).objectReferenceValue;
                if (f != null && f.GetType() == ssaoType) return;   // already there
            }
            var feature = ScriptableObject.CreateInstance(ssaoType);
            feature.name = "ScreenSpaceAmbientOcclusion";
            AssetDatabase.AddObjectToAsset(feature, data);
            AssetDatabase.TryGetGUIDAndLocalFileIdentifier(feature, out string _, out long localId);
            features.arraySize++;
            features.GetArrayElementAtIndex(features.arraySize - 1).objectReferenceValue = feature;
            map.arraySize++;
            map.GetArrayElementAtIndex(map.arraySize - 1).longValue = localId;
            so.ApplyModifiedProperties();

            var fso = new SerializedObject(feature);   // softer, contact-shadow style occlusion
            SetFloat(fso, "m_Settings.Intensity", 1.2f);
            SetFloat(fso, "m_Settings.Radius", 0.3f);
            fso.ApplyModifiedProperties();
            EditorUtility.SetDirty(data);
        }

        static void SetFloat(SerializedObject so, string path, float v) { var p = so.FindProperty(path); if (p != null) p.floatValue = v; }
        static void SetInt(SerializedObject so, string path, int v) { var p = so.FindProperty(path); if (p != null) p.intValue = v; }
        static void SetBool(SerializedObject so, string path, bool v) { var p = so.FindProperty(path); if (p != null) p.boolValue = v; }

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
            ApplyQuality();
        }
    }
}
