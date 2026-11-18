using System;
using System.Collections.Generic;
using ShuttleRL.Core;
using Unity.MLAgents;
using Unity.MLAgents.Policies;
using UnityEngine;
using UnityEngine.Rendering;

namespace ShuttleRL
{
    /// <summary>
    /// Heads-up display (IMGUI, no Canvas needed), all boxes rounded:
    ///   top    : title, simulated time, play controls (pause / step / restart / speed), camera controls, panel toggles
    ///   left   : simulation settings (rack size, demand, seed; applied as a new episode) and training settings
    ///            (behavior type, model, trainer connection, reward weights)
    ///   right  : two tabs, "대시보드" (system state) and "속성" (clicked shuttle / pallet)
    ///   bottom : event list, folds open / closed
    /// Left-click a shuttle or a pallet in the Game view to select it; it gets a highlighted outline (Esc clears).
    /// RackEnvironment adds this automatically in visual mode.
    /// </summary>
    public class RackHud : MonoBehaviour
    {
        public RackEnvironment env;
        public bool showHud = true;
        public bool showLeft = true, showRight = true, showEvents = true, eventsOpen = true;
        public float leftWidth = 300f, rightWidth = 340f, eventsHeight = 220f;
        public Color outlineColor = new Color(1f, 0.72f, 0.1f, 1f);

        enum Sel { None, Shuttle, Pallet }
        Sel sel; int selId = -1;
        int rightTab;              // 0 dashboard, 1 properties
        bool onlySelected;
        Vector2 leftScroll, dashScroll, propScroll, eventScroll;
        RackConfig draft;          // settings being edited in the left panel (applied with the button)
        bool dragFromHud;
        readonly SelectionOutline outline = new SelectionOutline();

        Texture2D roundTex, roundHoverTex, whiteTex;
        GUIStyle boxStyle, titleStyle, headStyle, labelStyle, valueStyle, smallStyle, eventStyle, timeStyle, btnStyle, bigStyle, toggleStyle;

        const float Pad = 10f, BarH = 50f, FoldH = 40f;
        static readonly Color PanelBg = new Color(0.08f, 0.1f, 0.14f, 0.84f);
        static readonly Color BtnBg = new Color(0.25f, 0.31f, 0.42f, 1f);
        static readonly Color BtnOn = new Color(0.95f, 0.62f, 0.12f, 1f);
        static readonly Color Accent = new Color(1f, 0.82f, 0.35f);

        // ------------------------------------------------------------------ layout
        float Top0 => Pad * 2 + BarH;
        Rect BarRect => new Rect(Pad, Pad, Screen.width - 2 * Pad, BarH);
        Rect LeftRect => new Rect(Pad, Top0, leftWidth, Screen.height - Top0 - Pad);
        Rect RightRect => new Rect(Screen.width - rightWidth - Pad, Top0, rightWidth, Screen.height - Top0 - Pad);
        Rect EventsRect
        {
            get
            {
                float x0 = showLeft ? Pad * 2 + leftWidth : Pad;
                float x1 = showRight ? Screen.width - Pad * 2 - rightWidth : Screen.width - Pad;
                float h = eventsOpen ? eventsHeight : FoldH;
                return new Rect(x0, Screen.height - Pad - h, Mathf.Max(200f, x1 - x0), h);
            }
        }
        Rect HelpRect
        {
            get
            {
                float x1 = showRight ? Screen.width - Pad * 2 - rightWidth : Screen.width - Pad;
                return new Rect(x1 - 400f, Top0, 400f, 168f);
            }
        }

        RackCameraController Cam
        {
            get { var c = Camera.main; return c != null ? c.GetComponent<RackCameraController>() : null; }
        }

        // ------------------------------------------------------------------ input, selection, outline
        void Update()
        {
            if (env == null || env.World == null || env.Visuals == null) return;
            var cam = Cam;
            if (cam != null) cam.drawHelp = !showHud;
            bool over = false;
#if ENABLE_LEGACY_INPUT_MANAGER
            over = OverHud(Input.mousePosition);
            if (Input.GetKeyDown(KeyCode.Escape)) ClearSelection();
            if (Input.GetKeyDown(KeyCode.Tab)) showHud = !showHud;
            bool anyDown = Input.GetMouseButtonDown(0) || Input.GetMouseButtonDown(1) || Input.GetMouseButtonDown(2);
            if (anyDown) dragFromHud = over;
            if (!Input.GetMouseButton(0) && !Input.GetMouseButton(1) && !Input.GetMouseButton(2)) dragFromHud = false;
            bool alt = Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt);
            if (Input.GetMouseButtonDown(0) && !alt && !over) Pick(Input.mousePosition);
#endif
            if (cam != null)
            {
                cam.blockWheel = over;
                cam.blockDrag = dragFromHud;
                cam.blockKeys = GUIUtility.keyboardControl != 0;
            }
            outline.color = outlineColor;
            outline.SetTarget(SelectedTransform());
        }

        void OnDisable() { outline.Clear(); }
        void OnDestroy() { outline.Clear(); }

        Transform SelectedTransform()
        {
            if (sel == Sel.Shuttle) return env.Visuals.ShuttleTransform(selId);
            if (sel == Sel.Pallet) return env.Visuals.PalletTransform(selId);
            return null;
        }

        bool OverHud(Vector3 mouse)
        {
            if (!showHud) return false;
            var p = new Vector2(mouse.x, Screen.height - mouse.y); // IMGUI y is top-down
            if (BarRect.Contains(p)) return true;
            if (showLeft && LeftRect.Contains(p)) return true;
            if (showRight && RightRect.Contains(p)) return true;
            if (showEvents && EventsRect.Contains(p)) return true;
            var cam = Cam;
            return cam != null && cam.showHelp && HelpRect.Contains(p);
        }

        void Pick(Vector3 mouse)
        {
            var cam = Camera.main; if (cam == null) return;
            if (!Physics.Raycast(cam.ScreenPointToRay(mouse), out var hit, 1000f)) return;
            var tag = hit.collider.GetComponentInParent<RackSelectable>();
            if (tag == null) return;
            if (tag.isShuttle) Select(Sel.Shuttle, tag.index);
            else
            {
                int pid = env.Visuals.SlotPallet(tag.index);
                if (pid >= 0) Select(Sel.Pallet, pid);
            }
        }

        void Select(Sel kind, int id)
        {
            sel = kind; selId = id; rightTab = 1; showRight = true;
            var cam = Cam;
            if (cam != null && cam.follow != null) cam.Follow(SelectedTransform(), cam.distance); // keep following the new pick
        }

        void ClearSelection()
        {
            sel = Sel.None; selId = -1;
            var cam = Cam; if (cam != null) cam.follow = null;
        }

        // ------------------------------------------------------------------ styles and drawing helpers
        void EnsureStyles()
        {
            if (roundTex != null && boxStyle != null) return;
            roundTex = MakeRoundTexture(32, 10, 0.82f);
            roundHoverTex = MakeRoundTexture(32, 10, 1f);
            whiteTex = MakeRoundTexture(8, 3, 1f);
            boxStyle = new GUIStyle { normal = { background = roundTex }, border = new RectOffset(12, 12, 12, 12), padding = new RectOffset(14, 14, 10, 10) };
            titleStyle = new GUIStyle(GUI.skin.label) { fontSize = 17, fontStyle = FontStyle.Bold, normal = { textColor = Accent } };
            headStyle = new GUIStyle(GUI.skin.label) { fontSize = 14, fontStyle = FontStyle.Bold, normal = { textColor = Accent } };
            labelStyle = new GUIStyle(GUI.skin.label) { fontSize = 13, normal = { textColor = new Color(0.75f, 0.8f, 0.88f) } };
            valueStyle = new GUIStyle(GUI.skin.label) { fontSize = 13, fontStyle = FontStyle.Bold, normal = { textColor = Color.white } };
            bigStyle = new GUIStyle(GUI.skin.label) { fontSize = 22, fontStyle = FontStyle.Bold, normal = { textColor = Color.white } };
            smallStyle = new GUIStyle(GUI.skin.label) { fontSize = 11, wordWrap = true, normal = { textColor = new Color(0.6f, 0.66f, 0.75f) } };
            eventStyle = new GUIStyle(GUI.skin.label) { fontSize = 12, wordWrap = true, normal = { textColor = new Color(0.9f, 0.92f, 0.95f) }, hover = { textColor = Accent } };
            timeStyle = new GUIStyle(GUI.skin.label) { fontSize = 12, normal = { textColor = new Color(0.55f, 0.8f, 1f) } };
            toggleStyle = new GUIStyle(GUI.skin.toggle) { fontSize = 13, wordWrap = true };
            toggleStyle.normal.textColor = toggleStyle.hover.textColor = new Color(0.75f, 0.8f, 0.88f);
            toggleStyle.onNormal.textColor = toggleStyle.onHover.textColor = Color.white;
            btnStyle = new GUIStyle
            {
                fontSize = 12, alignment = TextAnchor.MiddleCenter,
                normal = { background = roundTex, textColor = Color.white },
                hover = { background = roundHoverTex, textColor = Color.white },
                active = { background = roundHoverTex, textColor = Accent },
                border = new RectOffset(10, 10, 10, 10), padding = new RectOffset(8, 8, 4, 4), margin = new RectOffset(2, 2, 2, 2),
            };
        }

        static Texture2D MakeRoundTexture(int size, int radius, float shade)
        {
            var t = new Texture2D(size, size, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    float cx = Mathf.Clamp(x + 0.5f, radius, size - radius), cy = Mathf.Clamp(y + 0.5f, radius, size - radius);
                    float d = Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), new Vector2(cx, cy));
                    float a = Mathf.Clamp01(radius - d + 0.5f);
                    t.SetPixel(x, y, new Color(shade, shade, shade, a));
                }
            t.Apply();
            return t;
        }

        void RoundBox(Rect r)
        {
            var c = GUI.backgroundColor;
            GUI.backgroundColor = PanelBg;
            GUI.Box(r, GUIContent.none, boxStyle);
            GUI.backgroundColor = c;
        }

        bool Btn(string text, bool on = false, float width = 0f, bool enabled = true)
        {
            var c = GUI.backgroundColor; bool e = GUI.enabled;
            GUI.backgroundColor = on ? BtnOn : BtnBg;
            GUI.enabled = e && enabled;
            bool r = width > 0 ? GUILayout.Button(text, btnStyle, GUILayout.Width(width), GUILayout.Height(26))
                               : GUILayout.Button(text, btnStyle, GUILayout.Height(26));
            GUI.backgroundColor = c; GUI.enabled = e;
            return r;
        }

        void Bar(float fraction, Color color, float height = 8f)
        {
            var r = GUILayoutUtility.GetRect(10f, height, GUILayout.ExpandWidth(true));
            var c = GUI.color;
            GUI.color = new Color(1f, 1f, 1f, 0.12f);
            GUI.DrawTexture(r, whiteTex);
            GUI.color = color;
            GUI.DrawTexture(new Rect(r.x, r.y, r.width * Mathf.Clamp01(fraction), r.height), whiteTex);
            GUI.color = c;
        }

        void Row(string k, string v, float keyWidth = 104f)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(k, labelStyle, GUILayout.Width(keyWidth));
            GUILayout.Label(v, valueStyle);
            GUILayout.EndHorizontal();
        }

        void Divider()
        {
            GUILayout.Space(6);
            var r = GUILayoutUtility.GetRect(10f, 1f, GUILayout.ExpandWidth(true));
            var c = GUI.color; GUI.color = new Color(1f, 1f, 1f, 0.12f); GUI.DrawTexture(r, whiteTex); GUI.color = c;
            GUILayout.Space(6);
        }

        int StepInt(string label, int v, int step, int min, int max, string suffix = "")
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, labelStyle, GUILayout.Width(128));
            GUILayout.FlexibleSpace();
            if (Btn("−", false, 26, v > min)) v = Math.Max(min, v - step);
            GUILayout.Label(v + suffix, valueStyle, GUILayout.Width(62));
            if (Btn("+", false, 26, v < max)) v = Math.Min(max, v + step);
            GUILayout.EndHorizontal();
            return v;
        }

        float StepFloat(string label, float v, float step, float min, float max, string format, float display = 1f, string suffix = "")
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, labelStyle, GUILayout.Width(128));
            GUILayout.FlexibleSpace();
            if (Btn("−", false, 26, v > min + 1e-6f)) v = Mathf.Max(min, (float)Math.Round((v - step) / step) * step);
            GUILayout.Label((v * display).ToString(format) + suffix, valueStyle, GUILayout.Width(62));
            if (Btn("+", false, 26, v < max - 1e-6f)) v = Mathf.Min(max, (float)Math.Round((v + step) / step) * step);
            GUILayout.EndHorizontal();
            return v;
        }

        static string Clock(double t)
        {
            var ts = TimeSpan.FromSeconds(Math.Max(0, t));
            return $"{(int)ts.TotalHours:00}:{ts.Minutes:00}:{ts.Seconds:00}";
        }

        // ------------------------------------------------------------------ OnGUI
        void OnGUI()
        {
            if (!showHud || env == null || env.World == null) return;
            EnsureStyles();
            var w = env.World;
            DrawTopBar(w);
            if (showLeft) DrawLeft(w);
            if (showRight) DrawRight(w);
            if (showEvents) DrawEvents(w);
            var cam = Cam;
            if (cam != null && cam.showHelp) DrawHelp(cam);
        }

        // ---- top bar: title | time | play | camera | panels
        void DrawTopBar(SimWorld w)
        {
            var r = BarRect; RoundBox(r);
            var cam = Cam;
            GUILayout.BeginArea(new Rect(r.x + 16, r.y + 11, r.width - 32, r.height - 16));
            GUILayout.BeginHorizontal();

            bool wide = Screen.width >= 1600;
            GUILayout.Label(wide ? "ShuttleRL  셔틀 랙 시뮬레이터" : "ShuttleRL", titleStyle);
            GUILayout.Space(22);

            GUILayout.Label("시간", labelStyle);
            GUILayout.Label(Clock(w.Time), valueStyle, GUILayout.Width(66));
            GUILayout.Label($"/ {Clock(w.Cfg.episodeSeconds)}   에피소드 {env.Episode + 1}", labelStyle);
            GUILayout.Space(22);

            bool playing = !env.paused;
            if (Btn(playing ? "일시정지" : "재생", !playing, 70)) env.paused = !env.paused;
            if (Btn("+1초", false, 46, env.paused)) env.StepSeconds(1.0);
            if (Btn("+10초", false, 52, env.paused)) env.StepSeconds(10.0);
            if (Btn("다시 시작", false, 74)) RestartWith(null);
            GUILayout.Space(6);
            GUILayout.Label("배속", labelStyle);
            if (Btn("−", false, 26)) env.visualTimeScale = Mathf.Max(0.25f, env.visualTimeScale / 2f);
            GUILayout.Label($"x{env.visualTimeScale:0.##}", valueStyle, GUILayout.Width(44));
            if (Btn("+", false, 26)) env.visualTimeScale = Mathf.Min(256f, env.visualTimeScale * 2f);

            GUILayout.FlexibleSpace();

            if (cam != null)
            {
                GUILayout.Label("카메라", labelStyle);
                if (Btn("전체", false, 44)) cam.FrameAll();
                if (Btn("처음", false, 44)) cam.ResetView();
                if (Btn("위", false, 32)) cam.TopView();
                if (Btn("옆", false, 32)) cam.SideView();
                if (wide && Btn("확대", false, 44)) cam.Zoom(0.8f);
                if (wide && Btn("축소", false, 44)) cam.Zoom(1.25f);
                var target = SelectedTransform();
                bool following = cam.follow != null;
                if (Btn("따라가기", following, 70, target != null || following))
                    cam.Follow(following ? null : target, 8f);
                if (Btn("도움말", cam.showHelp, 58)) cam.showHelp = !cam.showHelp;
                GUILayout.Space(14);
            }
            if (Btn("설정", showLeft, 44)) showLeft = !showLeft;
            if (Btn("정보", showRight, 44)) showRight = !showRight;
            if (Btn("이벤트", showEvents, 54)) showEvents = !showEvents;

            GUILayout.EndHorizontal();
            GUILayout.EndArea();
        }

        void DrawHelp(RackCameraController cam)
        {
            var r = HelpRect; RoundBox(r);
            GUILayout.BeginArea(new Rect(r.x + 14, r.y + 10, r.width - 28, r.height - 20));
            GUILayout.BeginHorizontal();
            GUILayout.Label("조작 방법", headStyle);
            GUILayout.FlexibleSpace();
            if (Btn("닫기", false, 44)) cam.showHelp = false;
            GUILayout.EndHorizontal();
            GUILayout.Label(RackCameraController.HelpText.Substring(RackCameraController.HelpText.IndexOf('\n') + 1), eventStyle);
            GUILayout.EndArea();
        }

        // ---- left panel: simulation + training settings
        void RestartWith(RackConfig cfg)
        {
            if (cfg != null)
            {
                var c = cfg.Clone(); c.Validate();
                env.config = c;
                draft = c.Clone();
            }
            env.RestartEpisode();
            if (sel == Sel.Shuttle && selId >= env.World.Shuttles.Count) ClearSelection();
        }

        void DrawLeft(SimWorld w)
        {
            var r = LeftRect; RoundBox(r);
            if (draft == null) draft = env.config.Clone();
            GUILayout.BeginArea(new Rect(r.x + 14, r.y + 10, r.width - 28, r.height - 20));
            leftScroll = GUILayout.BeginScrollView(leftScroll);

            // ---------------- simulation
            GUILayout.Label("시뮬레이션 설정", headStyle);
            GUILayout.Label("적용을 누르면 새 에피소드로 다시 시작합니다.", smallStyle);
            GUILayout.Space(4);
            var d = draft;
            d.levels = StepInt("층 수", d.levels, 1, 1, 12);
            d.aisles = StepInt("메인 통로 수", d.aisles, 1, 1, 4);
            d.lanesPerAisle = StepInt("통로당 레인 열", d.lanesPerAisle, 1, 1, 20);
            d.cellsPerLane = StepInt("레인당 셀", d.cellsPerLane, 1, 1, 10);
            d.shuttlesPerLevel = StepInt("층당 셔틀", d.shuttlesPerLevel, 1, d.aisles, 12, "대");
            d.inboundPerHour = StepFloat("입고량", d.inboundPerHour, 10f, 0f, 1000f, "0", 1f, "/h");
            d.outboundPerHour = StepFloat("출고량", d.outboundPerHour, 10f, 0f, 1000f, "0", 1f, "/h");
            d.initialFill = StepFloat("초기 적재율", d.initialFill, 0.05f, 0f, 0.95f, "0", 100f, "%");
            d.episodeSeconds = StepFloat("에피소드 길이", d.episodeSeconds, 600f, 600f, 6f * 3600f, "0", 1f / 60f, "분");
            int autoLifts = AutoLiftCount(d);
            GUILayout.BeginHorizontal();
            GUILayout.Label("리프트 수", labelStyle, GUILayout.Width(128));
            GUILayout.FlexibleSpace();
            if (Btn("−", false, 26, d.liftCountOverride > 0)) d.liftCountOverride = d.liftCountOverride - 1;
            GUILayout.Label(d.liftCountOverride > 0 ? $"{Math.Min(d.liftCountOverride, 2 * d.aisles)}대" : $"자동 {autoLifts}", valueStyle, GUILayout.Width(62));
            if (Btn("+", false, 26, d.liftCountOverride < 2 * d.aisles)) d.liftCountOverride = d.liftCountOverride == 0 ? Math.Min(2 * d.aisles, autoLifts + 1) : d.liftCountOverride + 1;
            GUILayout.EndHorizontal();
            d.deadlockSeconds = StepFloat("교착 판정", d.deadlockSeconds, 30f, 60f, 1800f, "0", 1f, "초");
            env.baseSeed = StepInt("시드", env.baseSeed, 1, 0, 9999);
            d.maskCollisions = GUILayout.Toggle(d.maskCollisions, " 충돌 행동 마스크 (점유 셀로 이동 금지)", toggleStyle);

            bool dirty = !SameConfig(d, env.config);
            GUILayout.Space(6);
            GUILayout.BeginHorizontal();
            if (Btn(dirty ? "적용 (새 에피소드) *" : "적용 (새 에피소드)", dirty)) RestartWith(d);
            if (Btn("되돌리기", false, 76, dirty)) draft = env.config.Clone();
            GUILayout.EndHorizontal();
            bool trainer = Academy.IsInitialized && Academy.Instance.IsCommunicatorOn;
            env.useEnvironmentParameters = GUILayout.Toggle(env.useEnvironmentParameters, " 학습 커리큘럼 값 사용 (환경 파라미터)", toggleStyle);
            if (trainer && env.useEnvironmentParameters)
                GUILayout.Label("학습 연결 중에는 커리큘럼(yaml) 값이 위 설정보다 우선합니다.", smallStyle);

            Divider();
            // ---------------- training
            GUILayout.Label("학습 설정", headStyle);
            Row("트레이너", trainer ? "연결됨 (mlagents-learn)" : "연결 안 됨");
            GUILayout.Label("셔틀 제어 방식", labelStyle);
            var bt = env.Behavior;
            GUILayout.BeginHorizontal();
            if (Btn("규칙 기반", bt == BehaviorType.HeuristicOnly)) env.Behavior = BehaviorType.HeuristicOnly;
            if (Btn("학습 모델", bt == BehaviorType.InferenceOnly, 0f, env.ModelName != null)) env.Behavior = BehaviorType.InferenceOnly;
            if (Btn("학습", bt == BehaviorType.Default)) env.Behavior = BehaviorType.Default;
            GUILayout.EndHorizontal();
            GUILayout.Label(bt == BehaviorType.HeuristicOnly ? "규칙 기반 기준 셔틀 (최단 경로, 레인 잠금, 양보)."
                          : bt == BehaviorType.InferenceOnly ? "학습된 .onnx 모델로 움직입니다."
                          : "mlagents-learn에 연결되면 학습, 아니면 모델 또는 규칙 기반으로 움직입니다.", smallStyle);
            Row("모델", env.ModelName ?? "없음 (Behavior Parameters › Model)");
            if (Academy.IsInitialized)
            {
                Row("Academy 스텝", Academy.Instance.TotalStepCount.ToString("N0"));
                Row("완료 에피소드", Academy.Instance.EpisodeCount.ToString("N0"));
            }
            bool fast = GUILayout.Toggle(env.trainingMode, " 고속 모드 (다음 이벤트로 바로 점프)", toggleStyle);
            if (fast != env.trainingMode) env.trainingMode = fast;

            GUILayout.Space(6);
            GUILayout.Label("보상 가중치 (즉시 적용)", labelStyle);
            w.rPick = StepFloat("  들기", w.rPick, 0.05f, 0f, 5f, "0.00");
            w.rDrop = StepFloat("  내리기 (작업 완료)", w.rDrop, 0.05f, 0f, 5f, "0.00");
            w.rGroupTask = StepFloat("  그룹 작업 완료", w.rGroupTask, 0.05f, 0f, 5f, "0.00");
            w.rShaping = StepFloat("  목표 접근", w.rShaping, 0.005f, 0f, 0.2f, "0.000");
            w.rInvalid = StepFloat("  잘못된 행동", w.rInvalid, 0.01f, -1f, 0f, "0.00");
            w.rTimePerSec = StepFloat("  시간 페널티 /초", w.rTimePerSec, 0.0001f, -0.01f, 0f, "0.0000");
            w.rDeadlock = StepFloat("  교착", w.rDeadlock, 0.1f, -10f, 0f, "0.0");

            GUILayout.EndScrollView();
            GUILayout.EndArea();
        }

        static int AutoLiftCount(RackConfig d)
        {
            var c = d.Clone(); c.liftCountOverride = 0; c.Validate();
            return c.ComputeLiftCount();
        }

        static bool SameConfig(RackConfig a, RackConfig b)
        {
            return a.levels == b.levels && a.aisles == b.aisles && a.lanesPerAisle == b.lanesPerAisle && a.cellsPerLane == b.cellsPerLane
                && a.shuttlesPerLevel == b.shuttlesPerLevel && a.inboundPerHour == b.inboundPerHour && a.outboundPerHour == b.outboundPerHour
                && a.initialFill == b.initialFill && a.episodeSeconds == b.episodeSeconds && a.liftCountOverride == b.liftCountOverride
                && a.deadlockSeconds == b.deadlockSeconds && a.maskCollisions == b.maskCollisions;
        }

        // ---- right panel: dashboard / properties tabs
        void DrawRight(SimWorld w)
        {
            var r = RightRect; RoundBox(r);
            GUILayout.BeginArea(new Rect(r.x + 14, r.y + 10, r.width - 28, r.height - 20));
            GUILayout.BeginHorizontal();
            if (Btn("대시보드", rightTab == 0)) rightTab = 0;
            if (Btn("속성", rightTab == 1)) rightTab = 1;
            GUILayout.EndHorizontal();
            GUILayout.Space(8);
            if (rightTab == 0) DrawDashboard(w); else DrawProperties(w);
            GUILayout.EndArea();
        }

        void DrawDashboard(SimWorld w)
        {
            dashScroll = GUILayout.BeginScrollView(dashScroll);
            var st = w.Stats;
            double hours = Math.Max(1e-6, w.Time / 3600.0);

            GUILayout.Label("에피소드 진행", labelStyle);
            Bar((float)(w.Time / Math.Max(1.0, w.Cfg.episodeSeconds)), new Color(0.45f, 0.75f, 1f));
            GUILayout.Space(6);
            GUILayout.BeginHorizontal();
            GUILayout.BeginVertical();
            GUILayout.Label("처리량", labelStyle);
            GUILayout.Label($"{(st.inboundStored + st.outboundShipped) / hours:F0}/h", bigStyle);
            GUILayout.EndVertical();
            GUILayout.FlexibleSpace();
            GUILayout.BeginVertical();
            GUILayout.Label("셔틀 가동률", labelStyle);
            GUILayout.Label($"{w.Utilization():P0}", bigStyle);
            GUILayout.EndVertical();
            GUILayout.FlexibleSpace();
            GUILayout.BeginVertical();
            GUILayout.Label("교착", labelStyle);
            GUILayout.Label(st.deadlocks.ToString(), bigStyle);
            GUILayout.EndVertical();
            GUILayout.EndHorizontal();
            Divider();

            Row("입고 (보관/도착)", $"{st.inboundStored} / {st.inboundArrived}", 130);
            Row("출고 (출하/지시)", $"{st.outboundShipped} / {st.outboundOrdered}", 130);
            Row("평균 입고 시간", st.inboundStored > 0 ? $"{st.inboundCycleSum / st.inboundStored:F0} s" : "-", 130);
            Row("평균 출고 시간", st.outboundShipped > 0 ? $"{st.outboundCycleSum / st.outboundShipped:F0} s" : "-", 130);
            Row("입고 대기 (최대)", $"{w.InboundQueue.Count} ({st.maxInboundQueue})", 130);
            Row("진행 중 작업", w.Tasks.Count.ToString(), 130);
            Row("잘못된 행동", st.invalidActions.ToString(), 130);
            Row("에피소드 보상", env.EpisodeReward.ToString("F2"), 130);
            var last = env.LastEpisode;
            if (last.valid)
                Row("지난 에피소드", $"#{last.episode} {last.throughput:F0}/h, 보상 {last.reward:F1}{(last.deadlock ? ", 교착" : "")}", 130);
            Divider();

            GUILayout.Label("층별 적재율", headStyle);
            var L = w.Layout;
            int storage = 0;
            for (int c = 0; c < L.CellCount; c++) if (L.Type[c] == CellType.Lane) storage++;
            for (int lv = w.Cfg.levels - 1; lv >= 0; lv--)
            {
                int used = 0;
                for (int c = 0; c < L.CellCount; c++) if (L.Type[c] == CellType.Lane && w.PalletAt[lv][c] >= 0) used++;
                float f = storage > 0 ? (float)used / storage : 0f;
                GUILayout.BeginHorizontal();
                GUILayout.Label($"{lv + 1}층", labelStyle, GUILayout.Width(36));
                GUILayout.BeginVertical(); GUILayout.Space(7); Bar(f, new Color(0.4f, 0.85f, 0.55f)); GUILayout.EndVertical();
                GUILayout.Label($"{used}/{storage}", valueStyle, GUILayout.Width(56));
                GUILayout.EndHorizontal();
            }
            Divider();

            GUILayout.Label("셔틀 (누르면 선택)", headStyle);
            foreach (var s in w.Shuttles)
            {
                GUILayout.BeginHorizontal();
                bool isSel = sel == Sel.Shuttle && selId == s.id;
                if (Btn($"S{s.id}", isSel, 40)) Select(Sel.Shuttle, s.id);
                GUILayout.Label($"{s.level + 1}층 {ActName(s.act)}{(s.loaded ? " · 적재" : "")}", eventStyle, GUILayout.Width(118));
                GUILayout.BeginVertical(); GUILayout.Space(9);
                Bar(w.Time > 0 ? (float)(s.busyTime / w.Time) : 0f, new Color(1f, 0.7f, 0.25f));
                GUILayout.EndVertical();
                GUILayout.EndHorizontal();
            }
            Divider();

            GUILayout.Label("리프트", headStyle);
            for (int i = 0; i < w.Lifts.Count; i++)
            {
                var l = w.Lifts[i];
                GUILayout.BeginHorizontal();
                GUILayout.Label($"리프트{i + 1}", labelStyle, GUILayout.Width(60));
                GUILayout.Label($"{w.LiftLevel(l, w.Time) + 1f:0.0}층", valueStyle, GUILayout.Width(48));
                GUILayout.Label(LiftState(l), eventStyle);
                GUILayout.EndHorizontal();
            }
            GUILayout.EndScrollView();
        }

        static string LiftState(Lift l)
        {
            if (l.phase == 0) return "대기";
            int target = l.targetLevel + 1;
            if (l.outboundJob)
                switch (l.phase)
                {
                    case 1: return $"출고: {target}층으로 이동";
                    case 2: return $"출고: {target}층 P&D에서 싣는 중";
                    case 3: return "출고: 1층으로 내려가는 중";
                    default: return "출고: 컨베이어로 내보내는 중";
                }
            switch (l.phase)
            {
                case 1: return "입고: 1층으로 이동";
                case 2: return "입고: 1층에서 싣는 중";
                case 3: return $"입고: {target}층으로 올라가는 중";
                default: return $"입고: {target}층 P&D에 내리는 중";
            }
        }

        void DrawProperties(SimWorld w)
        {
            propScroll = GUILayout.BeginScrollView(propScroll);
            if (sel == Sel.Shuttle && selId < w.Shuttles.Count) DrawShuttle(w, w.Shuttles[selId]);
            else if (sel == Sel.Pallet) DrawPallet(w, selId);
            else GUILayout.Label("화면에서 셔틀이나 화물을 왼쪽 클릭하면 속성이 여기에 보입니다.\n선택한 대상은 윤곽선으로 표시됩니다. (Esc 선택 해제)", eventStyle);
            if (sel != Sel.None)
            {
                GUILayout.Space(10);
                GUILayout.BeginHorizontal();
                var cam = Cam; var target = SelectedTransform();
                bool following = cam != null && cam.follow != null;
                if (cam != null && Btn(following ? "따라가기 끄기" : "카메라 따라가기", following, 0f, target != null || following))
                    cam.Follow(following ? null : target, 8f);
                if (Btn("선택 해제", false, 80)) ClearSelection();
                GUILayout.EndHorizontal();
            }
            GUILayout.EndScrollView();
        }

        static string ActName(ShuttleAct a)
        {
            switch (a)
            {
                case ShuttleAct.Move: return "주행";
                case ShuttleAct.Brake: return "감속";
                case ShuttleAct.JackUp: return "들기";
                case ShuttleAct.JackDown: return "내리기";
                case ShuttleAct.Wait: return "대기";
                default: return "정지";
            }
        }

        void DrawShuttle(SimWorld w, Shuttle s)
        {
            GUILayout.Label($"셔틀 S{s.id}", headStyle);
            Row("층", $"{s.level + 1}층");
            Row("위치", w.CellName(s.cell));
            Row("상태", ActName(s.act));
            Row("속도", $"{w.CurrentSpeed(s, w.Time):F2} m/s");
            Row("바퀴", s.axis == 0 ? "통로 방향 (X)" : "레인 방향 (Y)");
            Row("적재", s.loaded ? $"P{s.carried}" : "없음");
            if (s.task != null)
            {
                Row("작업", $"{(s.task.kind == TaskKind.Inbound ? "입고" : "출고")} P{s.task.pallet}");
                Row("단계", s.task.phase == TaskPhase.ToPick ? $"들러 가는 중 → {w.CellName(s.task.pickCell)}" : $"내리러 가는 중 → {w.CellName(s.task.dropCell)}");
                Row("경과", $"{w.Time - s.task.created:F0} s");
            }
            else Row("작업", "없음");
            Row("가동률", w.Time > 0 ? $"{s.busyTime / w.Time:P0}" : "-");
            Row("제어", BehaviorName(env.Behavior));
        }

        static string BehaviorName(BehaviorType b)
        {
            return b == BehaviorType.HeuristicOnly ? "규칙 기반" : b == BehaviorType.InferenceOnly ? "학습 모델" : "학습 (기본)";
        }

        void DrawPallet(SimWorld w, int pallet)
        {
            GUILayout.Label($"파렛트 P{pallet}", headStyle);
            if (!w.FindPallet(pallet, out var where, out int level, out var task))
            {
                Row("위치", "출고됨 (시스템 밖)");
                return;
            }
            if (level >= 0) Row("층", $"{level + 1}층");
            Row("위치", where);
            if (task != null)
            {
                Row("작업", task.kind == TaskKind.Inbound ? "입고" : "출고");
                string phase = task.phase == TaskPhase.Waiting ? "대기" : task.phase == TaskPhase.Released ? "셔틀 배정 대기"
                    : task.phase == TaskPhase.ToPick ? "셔틀이 가지러 가는 중" : task.phase == TaskPhase.ToDrop ? "운반 중" : "완료";
                Row("단계", phase);
                if (task.shuttle >= 0) Row("셔틀", $"S{task.shuttle}");
                if (task.dropCell >= 0) Row("목적지", w.CellName(task.dropCell));
                Row("경과", $"{w.Time - task.created:F0} s");
            }
            else Row("작업", "보관 중");
        }

        // ---- bottom: event list (fold / unfold)
        void DrawEvents(SimWorld w)
        {
            var r = EventsRect; RoundBox(r);
            GUILayout.BeginArea(new Rect(r.x + 14, r.y + 7, r.width - 28, r.height - 12));
            GUILayout.BeginHorizontal();
            if (GUILayout.Button($"이벤트  ({w.Events.Count})", headStyle)) eventsOpen = !eventsOpen;
            if (!eventsOpen && w.Events.Count > 0)
            {
                var e = w.Events[w.Events.Count - 1];
                GUILayout.Label(Clock(e.time), timeStyle, GUILayout.Width(62));
                GUILayout.Label(e.text, eventStyle);
            }
            GUILayout.FlexibleSpace();
            if (eventsOpen)
            {
                bool can = sel != Sel.None;
                onlySelected = GUILayout.Toggle(onlySelected && can, can ? $" 선택 항목만 ({SelName()})" : " 선택 항목만", toggleStyle);
                GUILayout.Space(8);
            }
            if (Btn(eventsOpen ? "접기 ▼" : "펼치기 ▲", false, 72)) eventsOpen = !eventsOpen;
            GUILayout.EndHorizontal();

            if (eventsOpen)
            {
                eventScroll = GUILayout.BeginScrollView(eventScroll);
                int shown = 0;
                for (int i = w.Events.Count - 1; i >= 0; i--)
                {
                    var e = w.Events[i];
                    if (onlySelected && sel != Sel.None && !(sel == Sel.Shuttle ? e.shuttle == selId : e.pallet == selId)) continue;
                    GUILayout.BeginHorizontal();
                    GUILayout.Label(Clock(e.time), timeStyle, GUILayout.Width(62));
                    if (GUILayout.Button(e.text, eventStyle)) // click an event to select what it is about
                    {
                        if (e.shuttle >= 0) Select(Sel.Shuttle, e.shuttle);
                        else if (e.pallet >= 0) Select(Sel.Pallet, e.pallet);
                    }
                    GUILayout.EndHorizontal();
                    shown++;
                }
                if (shown == 0) GUILayout.Label(w.LogEvents ? "아직 이벤트가 없습니다." : "이벤트 기록이 꺼져 있습니다.", smallStyle);
                GUILayout.EndScrollView();
            }
            GUILayout.EndArea();
        }

        string SelName() { return sel == Sel.Shuttle ? $"S{selId}" : sel == Sel.Pallet ? $"P{selId}" : ""; }
    }

    /// <summary>
    /// Outline around the selected object: for every mesh under the target, two overlay copies are added as
    /// children, a stencil mask and an outward-grown hull drawn only outside that mask (shader Hidden/ShuttleRL/Outline).
    /// Overlay copies have no colliders, so picking is unaffected; they move with the target.
    /// </summary>
    public class SelectionOutline
    {
        public Color color = new Color(1f, 0.72f, 0.1f, 1f);
        public float width = 0.0035f; // fraction of camera distance (about 3 px at 1080p)

        Transform target;
        readonly List<GameObject> parts = new List<GameObject>();
        Material maskMat, lineMat;
        bool shaderMissing;
        MaterialPropertyBlock mpb;

        public void SetTarget(Transform t)
        {
            if (t == target && (t == null || parts.Count == 0 || parts[0] != null)) { UpdateColor(); return; }
            Clear();
            target = t;
            if (t == null || !EnsureMaterials()) return;
            if (mpb == null) mpb = new MaterialPropertyBlock();
            foreach (var mf in t.GetComponentsInChildren<MeshFilter>(true))
            {
                if (mf.gameObject.name.StartsWith("Outline")) continue; // our own overlay copies
                var mesh = mf.sharedMesh;
                if (mesh == null || mf.GetComponent<MeshRenderer>() == null) continue;
                var b = mesh.bounds;
                AddPart(mf.transform, mesh, maskMat, b, "OutlineMask");
                AddPart(mf.transform, mesh, lineMat, b, "Outline");
            }
            UpdateColor();
        }

        void AddPart(Transform parent, Mesh mesh, Material mat, Bounds b, string name)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var r = go.AddComponent<MeshRenderer>();
            var mats = new Material[Math.Max(1, mesh.subMeshCount)];
            for (int i = 0; i < mats.Length; i++) mats[i] = mat;
            r.sharedMaterials = mats;
            r.shadowCastingMode = ShadowCastingMode.Off;
            r.receiveShadows = false;
            mpb.Clear();
            mpb.SetVector("_Center", b.center);
            mpb.SetVector("_Extents", b.extents);
            r.SetPropertyBlock(mpb);
            parts.Add(go);
        }

        void UpdateColor()
        {
            if (lineMat == null) return;
            lineMat.SetColor("_Color", color);
            lineMat.SetFloat("_Width", width);
        }

        bool EnsureMaterials()
        {
            if (maskMat != null && lineMat != null) return true;
            if (shaderMissing) return false;
            var sh = Resources.Load<Shader>("ShuttleRLOutline");
            if (sh == null) sh = Shader.Find("Hidden/ShuttleRL/Outline");
            if (sh == null) { shaderMissing = true; Debug.LogWarning("ShuttleRL: outline shader not found (Assets/ShuttleRL/Resources/ShuttleRLOutline.shader)."); return false; }
            maskMat = new Material(sh) { name = "ShuttleRL_OutlineMask", renderQueue = 3020 };
            maskMat.SetFloat("_Width", 0f);
            maskMat.SetFloat("_ColorMask", 0f);                                  // no color, stencil only
            maskMat.SetFloat("_StencilComp", (float)CompareFunction.Always);
            maskMat.SetFloat("_StencilPass", (float)StencilOp.Replace);
            lineMat = new Material(sh) { name = "ShuttleRL_Outline", renderQueue = 3021 };
            lineMat.SetFloat("_ColorMask", 15f);
            lineMat.SetFloat("_StencilComp", (float)CompareFunction.NotEqual);
            lineMat.SetFloat("_StencilPass", (float)StencilOp.Keep);
            return true;
        }

        public void Clear()
        {
            foreach (var p in parts)
                if (p != null) { p.transform.SetParent(null, false); UnityEngine.Object.Destroy(p); } // detach: Destroy is deferred
            parts.Clear();
            target = null;
        }
    }
}
