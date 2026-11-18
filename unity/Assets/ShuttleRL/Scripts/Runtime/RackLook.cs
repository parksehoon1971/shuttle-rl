using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.Rendering;

namespace ShuttleRL
{
    /// <summary>
    /// Visual quality for the 3D demo (runtime, no package references: URP types are reached by reflection):
    ///   materials : physically based presets per Blender material (painted steel, galvanized rails, wood, cardboard ...)
    ///   lighting  : warm key light with soft high-resolution shadows, cool fill light, tri-light ambient, reflection probe
    ///   floor     : polished concrete slabs (procedural texture)
    ///   post      : ACES tonemapping, exposure / contrast, subtle bloom and vignette, SMAA on the camera
    /// The URP asset itself (shadow distance / cascades, MSAA, HDR, SSAO) is set up by the editor menu
    /// ShuttleRL > Apply High Quality Look, which also runs once automatically.
    /// </summary>
    public static class RackLook
    {
        public static bool Enabled = true;

        struct Preset
        {
            public Color color; public float metallic, smoothness; public float emission;
            public Preset(float r, float g, float b, float metallic, float smoothness, float emission = 0f)
            { color = new Color(r, g, b); this.metallic = metallic; this.smoothness = smoothness; this.emission = emission; }
        }

        // keyed by the Blender material name without the "M_" prefix (blender/build_assets.py COLORS)
        static readonly Dictionary<string, Preset> Presets = new Dictionary<string, Preset>
        {
            { "steel",        new Preset(0.62f, 0.64f, 0.67f, 0.85f, 0.55f) },  // galvanized steel
            { "rail",         new Preset(0.13f, 0.30f, 0.62f, 0.35f, 0.62f) },  // powder-coated blue rail
            { "upright",      new Preset(0.11f, 0.25f, 0.55f, 0.30f, 0.55f) },  // powder-coated blue upright
            { "beam",         new Preset(0.96f, 0.42f, 0.06f, 0.20f, 0.62f) },  // safety-orange beam
            { "shuttle",      new Preset(0.98f, 0.74f, 0.06f, 0.15f, 0.72f) },  // glossy yellow body
            { "shuttle_dark", new Preset(0.10f, 0.10f, 0.12f, 0.45f, 0.50f) },
            { "jack",         new Preset(0.42f, 0.43f, 0.46f, 0.90f, 0.62f) },
            { "wheel",        new Preset(0.04f, 0.04f, 0.045f, 0.0f, 0.35f) },  // polyurethane
            { "light",        new Preset(0.10f, 0.90f, 0.30f, 0.0f, 0.80f, 2.5f) }, // status LED
            { "wood",         new Preset(0.64f, 0.48f, 0.30f, 0.0f, 0.18f) },
            { "load",         new Preset(0.73f, 0.58f, 0.40f, 0.0f, 0.12f) },  // cardboard
            { "roller",       new Preset(0.76f, 0.78f, 0.81f, 0.92f, 0.72f) },
            { "lift",         new Preset(0.80f, 0.15f, 0.11f, 0.25f, 0.62f) },
            { "conveyor",     new Preset(0.22f, 0.22f, 0.24f, 0.50f, 0.45f) },
        };

        static readonly Dictionary<string, Material> cache = new Dictionary<string, Material>();
        static Shader lit;
        static bool sceneDone;

        static Shader Lit
        {
            get
            {
                if (lit == null) lit = Shader.Find("Universal Render Pipeline/Lit");
                if (lit == null) lit = Shader.Find("Standard");
                return lit;
            }
        }

        public static Material Make(Color color, float metallic, float smoothness, float emission = 0f, string name = "ShuttleRL_Mat")
        {
            var m = new Material(Lit) { name = name };
            m.SetColor("_BaseColor", color); m.SetColor("_Color", color);
            m.SetFloat("_Metallic", metallic);
            m.SetFloat("_Smoothness", smoothness); m.SetFloat("_Glossiness", smoothness);
            if (emission > 0f)
            {
                m.EnableKeyword("_EMISSION");
                m.SetColor("_EmissionColor", color * emission);
                m.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
            }
            m.enableInstancing = true;
            return m;
        }

        /// <summary>Replace the imported FBX materials under go with the physically based presets.</summary>
        public static void ApplyMaterials(GameObject go)
        {
            if (!Enabled || go == null || Lit == null) return;
            foreach (var r in go.GetComponentsInChildren<Renderer>(true))
            {
                var mats = r.sharedMaterials;
                bool changed = false;
                for (int i = 0; i < mats.Length; i++)
                {
                    if (mats[i] == null) continue;
                    string key = mats[i].name.Replace(" (Instance)", "");
                    if (key.StartsWith("ShuttleRL_")) continue;              // already ours (or the outline)
                    int dot = key.IndexOf('.');                              // Blender duplicates: M_steel.001
                    if (dot > 0) key = key.Substring(0, dot);
                    if (key.StartsWith("M_")) key = key.Substring(2);
                    if (!Presets.TryGetValue(key, out var p)) continue;
                    if (!cache.TryGetValue(key, out var m) || m == null)
                        cache[key] = m = Make(p.color, p.metallic, p.smoothness, p.emission, "ShuttleRL_" + key);
                    mats[i] = m; changed = true;
                }
                if (changed) r.sharedMaterials = mats;
            }
        }

        /// <summary>Floor, lights, reflections and post-processing. Runs once per scene.</summary>
        public static void SetupScene(Transform rackRoot, Bounds rackBounds)
        {
            if (!Enabled || sceneDone) return;
            sceneDone = true;
            try { Floor(rackRoot, rackBounds); } catch (Exception e) { Debug.LogWarning("ShuttleRL look (floor): " + e.Message); }
            try { Lights(); } catch (Exception e) { Debug.LogWarning("ShuttleRL look (lights): " + e.Message); }
            var world = rackBounds; world.center = rackRoot.TransformPoint(rackBounds.center);
            try { Reflections(world); } catch (Exception e) { Debug.LogWarning("ShuttleRL look (reflections): " + e.Message); }
            try { PostProcessing(); } catch (Exception e) { Debug.LogWarning("ShuttleRL look (post-processing): " + e.Message); }
            try { CameraSettings(Camera.main); } catch (Exception e) { Debug.LogWarning("ShuttleRL look (camera): " + e.Message); }
            QualitySettings.anisotropicFiltering = AnisotropicFiltering.ForceEnable;
            QualitySettings.shadows = ShadowQuality.All;
            QualitySettings.shadowResolution = ShadowResolution.VeryHigh;
        }

        /// <summary>Static state must not survive into the next Play session (domain reload may be disabled).</summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics() { sceneDone = false; floor = null; cache.Clear(); }

        static GameObject floor;

        /// <summary>Resize the floor to a new rack layout.</summary>
        public static void FitFloor(Transform rackRoot, Bounds b)
        {
            if (floor == null) return;
            var c = rackRoot.TransformPoint(new Vector3(b.center.x, 0f, b.center.z));
            floor.transform.position = new Vector3(c.x, rackRoot.position.y - 0.005f, c.z);
            float size = Mathf.Max(b.size.x, b.size.z) + 30f;
            floor.transform.localScale = new Vector3(size / 10f, 1f, size / 10f); // Unity plane is 10 x 10 m
            var m = floor.GetComponent<Renderer>().sharedMaterial;
            if (m != null)
            {
                var tiles = new Vector2(size / 4f, size / 4f);                    // 4 m concrete slabs
                if (m.HasProperty("_BaseMap")) m.SetTextureScale("_BaseMap", tiles);
                if (m.HasProperty("_MainTex")) m.SetTextureScale("_MainTex", tiles);
            }
        }

        static void Floor(Transform rackRoot, Bounds b)
        {
            floor = GameObject.CreatePrimitive(PrimitiveType.Plane);
            floor.name = "ShuttleRL_Floor";
            var col = floor.GetComponent<Collider>(); if (col != null) UnityEngine.Object.Destroy(col); // clicks on the floor = empty space
            var r = floor.GetComponent<Renderer>();
            var m = Make(new Color(0.50f, 0.50f, 0.49f), 0f, 0.42f, 0f, "ShuttleRL_Floor");
            var tex = ConcreteTexture(256);
            m.SetTexture("_BaseMap", tex); m.SetTexture("_MainTex", tex);
            r.sharedMaterial = m;
            r.shadowCastingMode = ShadowCastingMode.Off;
            FitFloor(rackRoot, b);
        }

        /// <summary>Procedural polished-concrete texture: soft mottling plus faint slab joints.</summary>
        static Texture2D ConcreteTexture(int n)
        {
            var t = new Texture2D(n, n, TextureFormat.RGBA32, true) { wrapMode = TextureWrapMode.Repeat, filterMode = FilterMode.Trilinear, anisoLevel = 8, name = "ShuttleRL_Concrete" };
            var rng = new System.Random(7);
            float ox = (float)rng.NextDouble() * 100f, oy = (float)rng.NextDouble() * 100f;
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    float u = (float)x / n, v = (float)y / n;
                    // tileable noise: sample a torus-wrapped Perlin field at three scales
                    float big = TileNoise(u, v, 3, ox, oy), mid = TileNoise(u, v, 9, oy, ox), fine = TileNoise(u, v, 40, ox + 13, oy + 7);
                    float g = 0.86f + 0.10f * big + 0.05f * mid + 0.03f * fine + 0.02f * ((float)rng.NextDouble() - 0.5f);
                    bool joint = x < 1 || y < 1;   // slab joint on the tile border (4 m slabs)
                    if (joint) g *= 0.78f;
                    t.SetPixel(x, y, new Color(g, g, g * 0.98f, 1f));
                }
            t.Apply(true);
            return t;
        }

        static float TileNoise(float u, float v, int period, float ox, float oy)
        {
            // blend four shifted samples so the pattern repeats at the tile edges
            float a = Mathf.PerlinNoise(ox + u * period, oy + v * period);
            float b = Mathf.PerlinNoise(ox + (u - 1f) * period, oy + v * period);
            float c = Mathf.PerlinNoise(ox + u * period, oy + (v - 1f) * period);
            float d = Mathf.PerlinNoise(ox + (u - 1f) * period, oy + (v - 1f) * period);
            float ab = Mathf.Lerp(a, b, u), cd = Mathf.Lerp(c, d, u);
            return Mathf.Lerp(ab, cd, v) * 2f - 1f;
        }

        static void Lights()
        {
            Light sun = RenderSettings.sun;
            if (sun == null)
                foreach (var l in UnityEngine.Object.FindObjectsByType<Light>(FindObjectsSortMode.None))
                    if (l.type == LightType.Directional) { sun = l; break; }
            if (sun == null) { sun = new GameObject("Sun").AddComponent<Light>(); sun.type = LightType.Directional; }
            sun.transform.rotation = Quaternion.Euler(48f, -35f, 0f);
            sun.color = new Color(1f, 0.95f, 0.88f);
            sun.intensity = 1.5f;
            sun.shadows = LightShadows.Soft;
            sun.shadowStrength = 0.82f;
            sun.shadowBias = 0.03f; sun.shadowNormalBias = 0.3f;
            RenderSettings.sun = sun;

            var fill = new GameObject("ShuttleRL_FillLight").AddComponent<Light>();
            fill.type = LightType.Directional;
            fill.transform.rotation = Quaternion.Euler(30f, 150f, 0f);
            fill.color = new Color(0.72f, 0.82f, 1f);
            fill.intensity = 0.35f;
            fill.shadows = LightShadows.None;

            RenderSettings.ambientMode = AmbientMode.Trilight;
            RenderSettings.ambientSkyColor = new Color(0.58f, 0.64f, 0.74f);
            RenderSettings.ambientEquatorColor = new Color(0.42f, 0.43f, 0.45f);
            RenderSettings.ambientGroundColor = new Color(0.22f, 0.21f, 0.20f);
            RenderSettings.ambientIntensity = 1f;
            RenderSettings.reflectionIntensity = 0.8f;
        }

        static void Reflections(Bounds b)
        {
            var go = new GameObject("ShuttleRL_ReflectionProbe");
            go.transform.position = b.center + Vector3.up * 0.5f;
            var p = go.AddComponent<ReflectionProbe>();
            p.mode = ReflectionProbeMode.Realtime;
            p.refreshMode = ReflectionProbeRefreshMode.ViaScripting;
            p.timeSlicingMode = ReflectionProbeTimeSlicingMode.AllFacesAtOnce;
            p.size = b.size + new Vector3(20f, 10f, 20f);
            p.resolution = 256;
            p.hdr = true;
            p.intensity = 0.9f;
            p.boxProjection = false;
            p.RenderProbe();
        }

        // ------------------------------------------------------------------ URP by reflection
        const string Core = ", Unity.RenderPipelines.Core.Runtime";
        const string Urp = ", Unity.RenderPipelines.Universal.Runtime";

        static void PostProcessing()
        {
            var volumeType = Type.GetType("UnityEngine.Rendering.Volume" + Core);
            var profileType = Type.GetType("UnityEngine.Rendering.VolumeProfile" + Core);
            if (volumeType == null || profileType == null) return;   // no SRP core package: skip quietly
            var go = new GameObject("ShuttleRL_PostProcessing");
            var vol = go.AddComponent(volumeType);
            SetMember(vol, "isGlobal", true);
            SetMember(vol, "priority", 10f);
            var profile = ScriptableObject.CreateInstance(profileType);
            profile.name = "ShuttleRL_PostProfile";
            SetMember(vol, "sharedProfile", profile);
            var add = profileType.GetMethod("Add", new[] { typeof(Type), typeof(bool) });
            if (add == null) return;

            var tonemap = Add(add, profile, "UnityEngine.Rendering.Universal.Tonemapping");
            SetParam(tonemap, "mode", 2);                // ACES
            var color = Add(add, profile, "UnityEngine.Rendering.Universal.ColorAdjustments");
            SetParam(color, "postExposure", 0.25f);
            SetParam(color, "contrast", 10f);
            SetParam(color, "saturation", 8f);
            var bloom = Add(add, profile, "UnityEngine.Rendering.Universal.Bloom");
            SetParam(bloom, "threshold", 1.0f);
            SetParam(bloom, "intensity", 0.35f);
            SetParam(bloom, "scatter", 0.65f);
            var vignette = Add(add, profile, "UnityEngine.Rendering.Universal.Vignette");
            SetParam(vignette, "intensity", 0.18f);
            SetParam(vignette, "smoothness", 0.45f);
            var wb = Add(add, profile, "UnityEngine.Rendering.Universal.WhiteBalance");
            SetParam(wb, "temperature", 4f);
        }

        static object Add(MethodInfo add, object profile, string typeName)
        {
            var t = Type.GetType(typeName + Urp);
            return t == null ? null : add.Invoke(profile, new object[] { t, true });
        }

        /// <summary>Set VolumeParameter field 'name' of a volume component (value + override flag).</summary>
        static void SetParam(object component, string name, object value)
        {
            if (component == null) return;
            var f = component.GetType().GetField(name, BindingFlags.Public | BindingFlags.Instance);
            var param = f?.GetValue(component);
            if (param == null) return;
            var vp = param.GetType().GetProperty("value");
            if (vp == null) return;
            var t = vp.PropertyType;
            object v = t.IsEnum ? Enum.ToObject(t, value) : Convert.ChangeType(value, t);
            vp.SetValue(param, v);
            SetMember(param, "overrideState", true);
        }

        public static void CameraSettings(Camera cam)
        {
            if (cam == null) return;
            cam.allowHDR = true;
            cam.allowMSAA = true;
            cam.backgroundColor = new Color(0.16f, 0.17f, 0.19f);
            var dataType = Type.GetType("UnityEngine.Rendering.Universal.UniversalAdditionalCameraData" + Urp);
            if (dataType == null) return;
            var data = cam.GetComponent(dataType);
            if (data == null) data = cam.gameObject.AddComponent(dataType);
            SetMember(data, "renderPostProcessing", true);
            SetMember(data, "antialiasing", 2);           // SMAA
            SetMember(data, "antialiasingQuality", 2);    // high
            SetMember(data, "renderShadows", true);
        }

        /// <summary>Set a public property or field by name; enums accept their integer value.</summary>
        static void SetMember(object target, string name, object value)
        {
            if (target == null) return;
            var type = target.GetType();
            var p = type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            if (p != null && p.CanWrite) { p.SetValue(target, Coerce(value, p.PropertyType)); return; }
            var f = type.GetField(name, BindingFlags.Public | BindingFlags.Instance);
            if (f != null) f.SetValue(target, Coerce(value, f.FieldType));
        }

        static object Coerce(object value, Type t)
        {
            if (value == null || t.IsInstanceOfType(value)) return value;
            if (t.IsEnum) return Enum.ToObject(t, value);
            return Convert.ChangeType(value, t);
        }
    }
}
