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
    ///   top    : title, simulated time, play controls (pause / step / restart / speed), camera controls, panel toggles, dark / light theme
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

        Texture2D panelTex, ctrlTex, pillTex, circleTex, barTex;
        GUIStyle panelShape, ctrlShape, titleStyle, headStyle, labelStyle, valueStyle, valueRight, smallStyle, eventStyle, timeStyle, bigStyle,
                 btnText, btnTextOn, btnTextOff;
        GUISkin skin;

        const float Pad = 10f, BarH = 52f, FoldH = 40f;

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
                return new Rect(x1 - 400f, Top0, 400f, 190f);
            }
        }

        RackCameraController Cam
        {
            get { var c = Camera.main; return c != null ? c.GetComponent<RackCameraController>() : null; }
        }

        // ------------------------------------------------------------------ input, selection, outline
        // Clicks that change what is drawn (panels, tabs, selection, restart) are applied before the next frame,
        // so the IMGUI layout pass and the event pass of one frame always see the same controls.
        readonly List<Action> later = new List<Action>();
        void Later(Action a) { later.Add(a); }

        void Update()
        {
            if (later.Count > 0) { var run = later.ToArray(); later.Clear(); foreach (var a in run) a(); }
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

        // ------------------------------------------------------------------ theme
        class Theme
        {
            public Color panel, border, surface, surfaceHover, text, sub, faint, accent, onAccent, divider, track, thumb, time;
        }

        static readonly Theme DarkTheme = new Theme
        {
            panel = new Color(0.09f, 0.10f, 0.12f, 0.93f), border = new Color(1f, 1f, 1f, 0.07f),
            surface = new Color(1f, 1f, 1f, 0.06f), surfaceHover = new Color(1f, 1f, 1f, 0.12f),
            text = new Color(0.94f, 0.95f, 0.97f), sub = new Color(0.63f, 0.67f, 0.74f), faint = new Color(0.45f, 0.49f, 0.56f),
            accent = new Color(0.33f, 0.56f, 1f), onAccent = Color.white,
            divider = new Color(1f, 1f, 1f, 0.07f), track = new Color(1f, 1f, 1f, 0.16f), thumb = new Color(1f, 1f, 1f, 0.22f),
            time = new Color(0.52f, 0.7f, 1f),
        };

        static readonly Theme LightTheme = new Theme
        {
            panel = new Color(0.985f, 0.987f, 0.99f, 0.95f), border = new Color(0f, 0f, 0f, 0.08f),
            surface = new Color(0f, 0f, 0f, 0.045f), surfaceHover = new Color(0f, 0f, 0f, 0.09f),
            text = new Color(0.09f, 0.11f, 0.15f), sub = new Color(0.36f, 0.40f, 0.47f), faint = new Color(0.55f, 0.58f, 0.63f),
            accent = new Color(0.15f, 0.42f, 0.95f), onAccent = Color.white,
            divider = new Color(0f, 0f, 0f, 0.08f), track = new Color(0f, 0f, 0f, 0.14f), thumb = new Color(0f, 0f, 0f, 0.22f),
            time = new Color(0.15f, 0.42f, 0.95f),
        };

        const string ThemePref = "ShuttleRL.HudTheme";
        int themeIndex = -1;    // 0 dark, 1 light (remembered in PlayerPrefs)
        int stylesFor = -2;
        Theme T => themeIndex == 1 ? LightTheme : DarkTheme;

        void SetTheme(int i)
        {
            if (i == themeIndex) return;
            themeIndex = i;
            PlayerPrefs.SetInt(ThemePref, i);
        }

        // ------------------------------------------------------------------ styles
        void EnsureStyles()
        {
            if (themeIndex < 0) themeIndex = PlayerPrefs.GetInt(ThemePref, 0) == 1 ? 1 : 0;
            if (stylesFor == themeIndex && skin != null) return;
            stylesFor = themeIndex;
            var t = T;
            if (panelTex == null)
            {
                panelTex = MakeRound(48, 48, 14);
                ctrlTex = MakeRound(32, 32, 8);
                pillTex = MakeRound(68, 36, 18);
                circleTex = MakeRound(32, 32, 16);
                barTex = MakeRound(12, 12, 6);
            }
            panelShape = new GUIStyle { normal = { background = panelTex }, border = new RectOffset(16, 16, 16, 16) };
            ctrlShape = new GUIStyle { normal = { background = ctrlTex }, border = new RectOffset(10, 10, 10, 10) };

            titleStyle = Text(16, t.text, FontStyle.Bold);
            headStyle = Text(11, t.faint, FontStyle.Bold);
            labelStyle = Text(13, t.sub);
            valueStyle = Text(13, t.text, FontStyle.Bold);
            valueRight = Text(13, t.text, FontStyle.Bold); valueRight.alignment = TextAnchor.MiddleRight;
            bigStyle = Text(22, t.text, FontStyle.Bold);
            smallStyle = Text(11, t.faint); smallStyle.wordWrap = true;
            eventStyle = Text(12, t.text); eventStyle.wordWrap = true;
            timeStyle = Text(12, t.time, FontStyle.Bold);
            btnText = Text(12, t.text); btnText.alignment = TextAnchor.MiddleCenter; btnText.padding = new RectOffset(10, 10, 0, 0);
            btnTextOn = new GUIStyle(btnText) { normal = { textColor = t.onAccent } };
            btnTextOff = new GUIStyle(btnText) { normal = { textColor = t.faint } };
            foreach (var st in new[] { titleStyle, labelStyle, valueStyle, valueRight, bigStyle, timeStyle, headStyle })
                if (st.alignment != TextAnchor.MiddleRight) st.alignment = TextAnchor.MiddleLeft;

            // thin, modern scrollbars; no horizontal bar
            if (skin != null) Destroy(skin);
            skin = ScriptableObject.Instantiate(GUI.skin);
            var thumbTex = MakeRound(8, 16, 3, t.thumb);
            skin.verticalScrollbar.normal.background = null;
            skin.verticalScrollbar.fixedWidth = 6f;
            skin.verticalScrollbar.border = new RectOffset(0, 0, 0, 0);
            skin.verticalScrollbar.margin = new RectOffset(6, 0, 2, 2);
            skin.verticalScrollbarThumb.normal.background = thumbTex;
            skin.verticalScrollbarThumb.fixedWidth = 6f;
            skin.verticalScrollbarThumb.border = new RectOffset(3, 3, 3, 3);
        }

        static GUIStyle Text(int size, Color color, FontStyle style = FontStyle.Normal)
        {
            var s = new GUIStyle { fontSize = size, fontStyle = style, clipping = TextClipping.Clip, padding = new RectOffset(0, 0, 2, 2) };
            s.normal.textColor = color;
            return s;
        }

        /// <summary>White rounded rectangle (tinted when drawn).</summary>
        static Texture2D MakeRound(int w, int h, int radius) { return MakeRound(w, h, radius, Color.white); }

        static Texture2D MakeRound(int w, int h, int radius, Color color)
        {
            var t = new Texture2D(w, h, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    float cx = Mathf.Clamp(x + 0.5f, radius, w - radius), cy = Mathf.Clamp(y + 0.5f, radius, h - radius);
                    float d = Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), new Vector2(cx, cy));
                    float a = Mathf.Clamp01(radius - d + 0.5f);
                    t.SetPixel(x, y, new Color(color.r, color.g, color.b, color.a * a));
                }
            t.Apply();
            return t;
        }

        // ------------------------------------------------------------------ drawing primitives
        static bool Repaint => Event.current.type == EventType.Repaint;

        void Fill(Rect r, Color c, GUIStyle shape)
        {
            if (!Repaint || c.a <= 0f) return;
            var old = GUI.backgroundColor;
            GUI.backgroundColor = c;
            GUI.Box(r, GUIContent.none, shape);
            GUI.backgroundColor = old;
        }

        void Tex(Rect r, Texture2D tex, Color c)
        {
            if (!Repaint) return;
            var old = GUI.color; GUI.color = c; GUI.DrawTexture(r, tex); GUI.color = old;
        }

        /// <summary>Panel: rounded fill with a hairline border.</summary>
        void Panel(Rect r)
        {
            Fill(r, T.border, panelShape);
            Fill(new Rect(r.x + 1, r.y + 1, r.width - 2, r.height - 2), T.panel, panelShape);
        }

        static bool Hover(Rect r) { return r.Contains(Event.current.mousePosition); }

        enum Kind { Ghost, Soft, Primary }

        /// <summary>Flat button: ghost (no fill until hover), soft (subtle fill) or primary (accent). 'on' shows it selected.</summary>
        bool Btn(string text, Kind kind = Kind.Ghost, bool on = false, float width = 0f, bool enabled = true, bool expand = false)
        {
            var content = new GUIContent(text);
            Rect r = width > 0 ? GUILayoutUtility.GetRect(width, 28f, GUILayout.Width(width), GUILayout.Height(28f))
                   : expand ? GUILayoutUtility.GetRect(content, btnText, GUILayout.Height(28f), GUILayout.ExpandWidth(true))
                   : GUILayoutUtility.GetRect(content, btnText, GUILayout.Height(28f));
            bool hover = enabled && Hover(r);
            bool filled = on || kind == Kind.Primary;
            Color bg = filled ? (hover ? Color.Lerp(T.accent, Color.white, 0.12f) : T.accent)
                     : kind == Kind.Soft ? (hover ? T.surfaceHover : T.surface)
                     : (hover ? T.surface : Color.clear);
            if (!enabled && filled) bg.a *= 0.4f;
            Fill(r, bg, ctrlShape);
            GUI.Label(r, content, !enabled ? btnTextOff : filled ? btnTextOn : btnText);
            return enabled && GUI.Button(r, GUIContent.none, GUIStyle.none);
        }

        /// <summary>Segmented control (tabs, modes). Returns the selected index.</summary>
        int Segmented(int value, string[] items, float width = 0f, bool[] enabled = null)
        {
            Rect r = width > 0 ? GUILayoutUtility.GetRect(width, 30f, GUILayout.Width(width), GUILayout.Height(30f))
                               : GUILayoutUtility.GetRect(10f, 30f, GUILayout.ExpandWidth(true), GUILayout.Height(30f));
            Fill(r, T.surface, ctrlShape);
            float w = (r.width - 6f) / items.Length;
            for (int i = 0; i < items.Length; i++)
            {
                var seg = new Rect(r.x + 3f + i * w, r.y + 3f, w, r.height - 6f);
                bool en = enabled == null || enabled[i];
                bool on = i == value;
                if (on) Fill(seg, T.accent, ctrlShape);
                else if (en && Hover(seg)) Fill(seg, T.surface, ctrlShape);
                GUI.Label(seg, items[i], on ? btnTextOn : en ? btnText : btnTextOff);
                if (en && GUI.Button(seg, GUIContent.none, GUIStyle.none)) value = i;
            }
            return value;
        }

        /// <summary>Label on the left, iOS-style switch on the right; the whole row is clickable.</summary>
        bool Switch(string label, bool value, bool enabled = true)
        {
            Rect r = GUILayoutUtility.GetRect(10f, 28f, GUILayout.ExpandWidth(true), GUILayout.Height(28f));
            GUI.Label(new Rect(r.x, r.y, r.width - 46f, r.height), label, enabled ? labelStyle : btnTextOff);
            var track = new Rect(r.xMax - 36f, r.y + 5f, 34f, 18f);
            float a = enabled ? 1f : 0.4f;
            var tc = value ? T.accent : T.track; tc.a *= a;
            Tex(track, pillTex, tc);
            var knob = new Rect(value ? track.xMax - 16f : track.x + 2f, track.y + 2f, 14f, 14f);
            Tex(knob, circleTex, new Color(1f, 1f, 1f, a));
            if (enabled && GUI.Button(r, GUIContent.none, GUIStyle.none)) value = !value;
            return value;
        }

        /// <summary>Compact "− value +" control. Returns -1, 0 or +1.</summary>
        int StepperControl(Rect c, string text, bool canDec, bool canInc)
        {
            Fill(c, T.surface, ctrlShape);
            var minus = new Rect(c.x, c.y, 28f, c.height);
            var plus = new Rect(c.xMax - 28f, c.y, 28f, c.height);
            if (canDec && Hover(minus)) Fill(minus, T.surface, ctrlShape);
            if (canInc && Hover(plus)) Fill(plus, T.surface, ctrlShape);
            GUI.Label(minus, "−", canDec ? btnText : btnTextOff);
            GUI.Label(plus, "+", canInc ? btnText : btnTextOff);
            var mid = new GUIStyle(valueStyle) { alignment = TextAnchor.MiddleCenter };
            GUI.Label(new Rect(c.x + 28f, c.y, c.width - 56f, c.height), text, mid);
            if (canDec && GUI.Button(minus, GUIContent.none, GUIStyle.none)) return -1;
            if (canInc && GUI.Button(plus, GUIContent.none, GUIStyle.none)) return 1;
            return 0;
        }

        int StepperRow(string label, string text, bool canDec, bool canInc)
        {
            Rect r = GUILayoutUtility.GetRect(10f, 30f, GUILayout.ExpandWidth(true), GUILayout.Height(30f));
            GUI.Label(new Rect(r.x, r.y, r.width - 120f, r.height), label, labelStyle);
            return StepperControl(new Rect(r.xMax - 116f, r.y + 2f, 116f, 26f), text, canDec, canInc);
        }

        int StepInt(string label, int v, int step, int min, int max, string suffix = "")
        {
            int d = StepperRow(label, v + suffix, v > min, v < max);
            return d < 0 ? Math.Max(min, v - step) : d > 0 ? Math.Min(max, v + step) : v;
        }

        float StepFloat(string label, float v, float step, float min, float max, string format, float display = 1f, string suffix = "")
        {
            int d = StepperRow(label, (v * display).ToString(format) + suffix, v > min + 1e-6f, v < max - 1e-6f);
            if (d < 0) v = Mathf.Max(min, (float)Math.Round((v - step) / step) * step);
            if (d > 0) v = Mathf.Min(max, (float)Math.Round((v + step) / step) * step);
            return v;
        }

        void Bar(float fraction, Color color, float height = 6f)
        {
            var r = GUILayoutUtility.GetRect(10f, height, GUILayout.ExpandWidth(true), GUILayout.Height(height));
            BarAt(r, fraction, color);
        }

        void BarAt(Rect r, float fraction, Color color)
        {
            Tex(r, barTex, T.track);
            float f = Mathf.Clamp01(fraction);
            if (f > 0f) Tex(new Rect(r.x, r.y, Mathf.Max(r.height, r.width * f), r.height), barTex, color);
        }

        void Row(string k, string v)
        {
            Rect r = GUILayoutUtility.GetRect(10f, 22f, GUILayout.ExpandWidth(true), GUILayout.Height(22f));
            GUI.Label(r, k, labelStyle);
            GUI.Label(r, v, valueRight);
        }

        void Section(string title)
        {
            GUILayout.Space(10);
            GUILayout.Label(title, headStyle);
            GUILayout.Space(2);
        }

        void Divider()
        {
            GUILayout.Space(8);
            var r = GUILayoutUtility.GetRect(10f, 1f, GUILayout.ExpandWidth(true), GUILayout.Height(1f));
            Tex(r, barTex, T.divider);
            GUILayout.Space(8);
        }

        void VSep()
        {
            GUILayout.Space(10);
            var r = GUILayoutUtility.GetRect(1f, 28f, GUILayout.Width(1f), GUILayout.Height(28f));
            Tex(new Rect(r.x, r.y + 4f, 1f, 20f), barTex, T.divider);
            GUILayout.Space(10);
        }

        Vector2 Scroll(Vector2 pos)
        {
            return GUILayout.BeginScrollView(pos, false, false, GUIStyle.none, skin.verticalScrollbar, GUIStyle.none);
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
            var oldSkin = GUI.skin;
            GUI.skin = skin;
            var w = env.World;
            DrawTopBar(w);
            if (showLeft) DrawLeft(w);
            if (showRight) DrawRight(w);
            if (showEvents) DrawEvents(w);
            var cam = Cam;
            if (cam != null && cam.showHelp) DrawHelp(cam);
            GUI.skin = oldSkin;
        }

        // ---- top bar: title | time | play | camera | panels | theme
        void DrawTopBar(SimWorld w)
        {
            var r = BarRect; Panel(r);
            var cam = Cam;
            bool wide = Screen.width >= 1600;
            GUILayout.BeginArea(new Rect(r.x + 16, r.y + (r.height - 30f) / 2f, r.width - 32, 30f));
            GUILayout.BeginHorizontal();

            var accentTitle = new GUIStyle(titleStyle) { normal = { textColor = T.accent } };
            GUILayout.Label("ShuttleRL", accentTitle, GUILayout.Height(28));
            if (wide) GUILayout.Label("셔틀 랙 시뮬레이터", labelStyle, GUILayout.Height(28));
            VSep();

            GUILayout.Label(Clock(w.Time), new GUIStyle(titleStyle) { fontSize = 15 }, GUILayout.Height(28));
            GUILayout.Label($"/ {Clock(w.Cfg.episodeSeconds)}  ·  에피소드 {env.Episode + 1}", smallStyle, GUILayout.Height(28));
            VSep();

            if (Btn(env.paused ? "재생" : "일시정지", Kind.Primary, false, 76)) env.paused = !env.paused;
            if (Btn("+1초", Kind.Ghost, false, 0, env.paused)) env.StepSeconds(1.0);
            if (Btn("+10초", Kind.Ghost, false, 0, env.paused)) env.StepSeconds(10.0);
            if (Btn("다시 시작")) Later(() => RestartWith(null));
            GUILayout.Space(6);
            GUILayout.Label("배속", labelStyle, GUILayout.Height(28));
            var sr = GUILayoutUtility.GetRect(104f, 28f, GUILayout.Width(104f), GUILayout.Height(28f));
            int ds = StepperControl(new Rect(sr.x, sr.y + 1f, sr.width, 26f), $"x{env.visualTimeScale:0.##}", env.visualTimeScale > 0.25f, env.visualTimeScale < 256f);
            if (ds < 0) env.visualTimeScale = Mathf.Max(0.25f, env.visualTimeScale / 2f);
            if (ds > 0) env.visualTimeScale = Mathf.Min(256f, env.visualTimeScale * 2f);

            GUILayout.FlexibleSpace();

            if (cam != null)
            {
                if (Btn("전체")) cam.FrameAll();
                if (Btn("처음")) cam.ResetView();
                if (Btn("위")) cam.TopView();
                if (Btn("옆")) cam.SideView();
                if (wide && Btn("확대")) cam.Zoom(0.8f);
                if (wide && Btn("축소")) cam.Zoom(1.25f);
                var target = SelectedTransform();
                bool following = cam.follow != null;
                if (Btn("따라가기", Kind.Ghost, following, 0, target != null || following)) cam.Follow(following ? null : target, 8f);
                if (Btn("도움말", Kind.Ghost, cam.showHelp)) Later(() => cam.showHelp = !cam.showHelp);
                VSep();
            }
            if (Btn("설정", Kind.Ghost, showLeft)) Later(() => showLeft = !showLeft);
            if (Btn("정보", Kind.Ghost, showRight)) Later(() => showRight = !showRight);
            if (Btn("이벤트", Kind.Ghost, showEvents)) Later(() => showEvents = !showEvents);
            VSep();
            SetTheme(Segmented(themeIndex, new[] { "다크", "라이트" }, 112f));

            GUILayout.EndHorizontal();
            GUILayout.EndArea();
        }

        void DrawHelp(RackCameraController cam)
        {
            var r = HelpRect; Panel(r);
            GUILayout.BeginArea(new Rect(r.x + 16, r.y + 12, r.width - 32, r.height - 22));
            GUILayout.BeginHorizontal();
            GUILayout.Label("조작 방법", valueStyle, GUILayout.Height(28));
            GUILayout.FlexibleSpace();
            if (Btn("닫기", Kind.Soft)) Later(() => cam.showHelp = false);
            GUILayout.EndHorizontal();
            GUILayout.Space(4);
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

        /// <summary>Content column inside a panel: narrower than the area so nothing ever scrolls sideways.</summary>
        Rect Inner(Rect r) { return new Rect(r.x + 16, r.y + 14, r.width - 32, r.height - 26); }
        float ColumnWidth(Rect inner) { return inner.width - 14f; } // room for the 6 px scrollbar + margin

        void DrawLeft(SimWorld w)
        {
            var r = LeftRect; Panel(r);
            if (draft == null) draft = env.config.Clone();
            var inner = Inner(r);
            GUILayout.BeginArea(inner);
            GUILayout.Label("설정", titleStyle, GUILayout.Height(26));
            leftScroll = Scroll(leftScroll);
            GUILayout.BeginVertical(GUILayout.Width(ColumnWidth(inner)));

            // ---------------- simulation
            Section("시뮬레이션");
            var d = draft;
            d.levels = StepInt("층 수", d.levels, 1, 1, 12);
            d.aisles = StepInt("메인 통로", d.aisles, 1, 1, 4);
            d.lanesPerAisle = StepInt("통로당 레인", d.lanesPerAisle, 1, 1, 20);
            d.cellsPerLane = StepInt("레인당 셀", d.cellsPerLane, 1, 1, 10);
            d.shuttlesPerLevel = StepInt("층당 셔틀", d.shuttlesPerLevel, 1, d.aisles, 12, "대");
            d.inboundPerHour = StepFloat("입고량", d.inboundPerHour, 10f, 0f, 1000f, "0", 1f, "/h");
            d.outboundPerHour = StepFloat("출고량", d.outboundPerHour, 10f, 0f, 1000f, "0", 1f, "/h");
            d.initialFill = StepFloat("초기 적재율", d.initialFill, 0.05f, 0f, 0.95f, "0", 100f, "%");
            d.episodeSeconds = StepFloat("에피소드 길이", d.episodeSeconds, 600f, 600f, 6f * 3600f, "0", 1f / 60f, "분");
            int autoLifts = AutoLiftCount(d);
            int dl = StepperRow("리프트", d.liftCountOverride > 0 ? $"{Math.Min(d.liftCountOverride, 2 * d.aisles)}대" : $"자동 {autoLifts}",
                                d.liftCountOverride > 0, d.liftCountOverride < 2 * d.aisles);
            if (dl < 0) d.liftCountOverride--;
            if (dl > 0) d.liftCountOverride = d.liftCountOverride == 0 ? Math.Min(2 * d.aisles, autoLifts + 1) : d.liftCountOverride + 1;
            d.deadlockSeconds = StepFloat("교착 판정", d.deadlockSeconds, 30f, 60f, 1800f, "0", 1f, "초");
            env.baseSeed = StepInt("시드", env.baseSeed, 1, 0, 9999);
            d.maskCollisions = Switch("충돌 행동 마스크", d.maskCollisions);

            bool dirty = !SameConfig(d, env.config);
            GUILayout.Space(8);
            GUILayout.BeginHorizontal();
            if (Btn(dirty ? "적용 · 새 에피소드" : "적용됨", Kind.Primary, false, 0, dirty, true)) { var apply = d.Clone(); Later(() => RestartWith(apply)); }
            if (Btn("되돌리기", Kind.Soft, false, 0, dirty)) draft = env.config.Clone();
            GUILayout.EndHorizontal();
            bool trainer = Academy.IsInitialized && Academy.Instance.IsCommunicatorOn;
            env.useEnvironmentParameters = Switch("커리큘럼 값 사용", env.useEnvironmentParameters);
            if (trainer)
                GUILayout.Label("학습 연결 중: 커리큘럼 값 사용을 켜면 yaml 값이 위 설정보다 우선합니다.", smallStyle);

            // ---------------- training
            Section("학습");
            Row("트레이너", trainer ? "연결됨" : "연결 안 됨");
            GUILayout.Space(4);
            var bt = env.Behavior;
            int bi = bt == BehaviorType.HeuristicOnly ? 0 : bt == BehaviorType.InferenceOnly ? 1 : 2;
            int nb = Segmented(bi, new[] { "규칙", "모델", "학습" }, 0f, new[] { true, env.ModelName != null || bi == 1, true });
            if (nb != bi) env.Behavior = nb == 0 ? BehaviorType.HeuristicOnly : nb == 1 ? BehaviorType.InferenceOnly : BehaviorType.Default;
            GUILayout.Label(bi == 0 ? "규칙 기반 기준 셔틀 (최단 경로, 레인 잠금, 양보)"
                          : bi == 1 ? "학습된 .onnx 모델로 움직입니다"
                          : "mlagents-learn에 연결되면 학습, 아니면 모델 또는 규칙으로 움직입니다", smallStyle);
            Row("모델", env.ModelName ?? "없음");
            if (Academy.IsInitialized)
            {
                Row("Academy 스텝", Academy.Instance.TotalStepCount.ToString("N0"));
                Row("완료 에피소드", Academy.Instance.EpisodeCount.ToString("N0"));
            }
            env.trainingMode = Switch("고속 모드 (이벤트 점프)", env.trainingMode);

            Section("보상 가중치 · 즉시 적용");
            w.rPick = StepFloat("들기", w.rPick, 0.05f, 0f, 5f, "0.00");
            w.rDrop = StepFloat("내리기", w.rDrop, 0.05f, 0f, 5f, "0.00");
            w.rGroupTask = StepFloat("그룹 작업 완료", w.rGroupTask, 0.05f, 0f, 5f, "0.00");
            w.rShaping = StepFloat("목표 접근", w.rShaping, 0.005f, 0f, 0.2f, "0.000");
            w.rInvalid = StepFloat("잘못된 행동", w.rInvalid, 0.01f, -1f, 0f, "0.00");
            w.rTimePerSec = StepFloat("시간 /초", w.rTimePerSec, 0.0001f, -0.01f, 0f, "0.0000");
            w.rDeadlock = StepFloat("교착", w.rDeadlock, 0.1f, -10f, 0f, "0.0");
            GUILayout.Space(8);

            GUILayout.EndVertical();
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
            var r = RightRect; Panel(r);
            var inner = Inner(r);
            GUILayout.BeginArea(inner);
            int tab = Segmented(rightTab, new[] { "대시보드", "속성" });
            if (tab != rightTab) Later(() => rightTab = tab);
            GUILayout.Space(6);
            if (rightTab == 0) dashScroll = Scroll(dashScroll); else propScroll = Scroll(propScroll);
            GUILayout.BeginVertical(GUILayout.Width(ColumnWidth(inner)));
            if (rightTab == 0) DrawDashboard(w); else DrawProperties(w);
            GUILayout.EndVertical();
            GUILayout.EndScrollView();
            GUILayout.EndArea();
        }

        void Cards(params (string label, string value)[] items)
        {
            Rect r = GUILayoutUtility.GetRect(10f, 60f, GUILayout.ExpandWidth(true), GUILayout.Height(60f));
            float gap = 6f, cw = (r.width - gap * (items.Length - 1)) / items.Length;
            for (int i = 0; i < items.Length; i++)
            {
                var c = new Rect(r.x + i * (cw + gap), r.y, cw, r.height);
                Fill(c, T.surface, ctrlShape);
                GUI.Label(new Rect(c.x + 10, c.y + 6, c.width - 14, 18), items[i].label, smallStyle);
                GUI.Label(new Rect(c.x + 10, c.y + 24, c.width - 14, 30), items[i].value, bigStyle);
            }
        }

        void DrawDashboard(SimWorld w)
        {
            var st = w.Stats;
            double hours = Math.Max(1e-6, w.Time / 3600.0);
            GUILayout.Space(4);
            Cards(("처리량 /h", $"{(st.inboundStored + st.outboundShipped) / hours:F0}"),
                  ("셔틀 가동률", $"{w.Utilization() * 100f:F0}%"),
                  ("교착", st.deadlocks.ToString()));
            GUILayout.Space(10);
            Row("에피소드 진행", $"{w.Time / Math.Max(1.0, w.Cfg.episodeSeconds) * 100:F0}%");
            Bar((float)(w.Time / Math.Max(1.0, w.Cfg.episodeSeconds)), T.accent);

            Section("입출고");
            Row("입고 (보관 / 도착)", $"{st.inboundStored} / {st.inboundArrived}");
            Row("출고 (출하 / 지시)", $"{st.outboundShipped} / {st.outboundOrdered}");
            Row("평균 입고 시간", st.inboundStored > 0 ? $"{st.inboundCycleSum / st.inboundStored:F0} s" : "-");
            Row("평균 출고 시간", st.outboundShipped > 0 ? $"{st.outboundCycleSum / st.outboundShipped:F0} s" : "-");
            Row("입고 대기 (최대)", $"{w.InboundQueue.Count} ({st.maxInboundQueue})");
            Row("진행 중 작업", w.Tasks.Count.ToString());
            Row("잘못된 행동", st.invalidActions.ToString());
            Row("에피소드 보상", env.EpisodeReward.ToString("F2"));
            var last = env.LastEpisode;
            if (last.valid) Row("지난 에피소드", $"#{last.episode}  {last.throughput:F0}/h{(last.deadlock ? "  교착" : "")}");

            Section("층별 적재율");
            var L = w.Layout;
            int storage = 0;
            for (int c = 0; c < L.CellCount; c++) if (L.Type[c] == CellType.Lane) storage++;
            for (int lv = w.Cfg.levels - 1; lv >= 0; lv--)
            {
                int used = 0;
                for (int c = 0; c < L.CellCount; c++) if (L.Type[c] == CellType.Lane && w.PalletAt[lv][c] >= 0) used++;
                Rect row = GUILayoutUtility.GetRect(10f, 22f, GUILayout.ExpandWidth(true), GUILayout.Height(22f));
                GUI.Label(new Rect(row.x, row.y, 36, row.height), $"{lv + 1}층", labelStyle);
                GUI.Label(row, $"{used}/{storage}", valueRight);
                BarAt(new Rect(row.x + 40, row.y + 8, row.width - 106, 6), storage > 0 ? (float)used / storage : 0f, new Color(0.25f, 0.75f, 0.5f));
            }

            Section("셔틀 · 누르면 선택");
            foreach (var s in w.Shuttles)
            {
                Rect row = GUILayoutUtility.GetRect(10f, 26f, GUILayout.ExpandWidth(true), GUILayout.Height(26f));
                bool isSel = sel == Sel.Shuttle && selId == s.id;
                if (isSel) Fill(row, T.surfaceHover, ctrlShape); else if (Hover(row)) Fill(row, T.surface, ctrlShape);
                GUI.Label(new Rect(row.x + 6, row.y, 36, row.height), $"S{s.id}", valueStyle);
                GUI.Label(new Rect(row.x + 42, row.y, 120, row.height), $"{s.level + 1}층 {ActName(s.act)}{(s.loaded ? " · 적재" : "")}", labelStyle);
                BarAt(new Rect(row.x + 166, row.y + 10, row.width - 172, 6), w.Time > 0 ? (float)(s.busyTime / w.Time) : 0f, new Color(0.96f, 0.62f, 0.2f));
                if (GUI.Button(row, GUIContent.none, GUIStyle.none)) { int id = s.id; Later(() => Select(Sel.Shuttle, id)); }
            }

            Section("리프트");
            for (int i = 0; i < w.Lifts.Count; i++)
            {
                var l = w.Lifts[i];
                Rect row = GUILayoutUtility.GetRect(10f, 22f, GUILayout.ExpandWidth(true), GUILayout.Height(22f));
                GUI.Label(new Rect(row.x, row.y, 64, row.height), $"리프트{i + 1}", valueStyle);
                GUI.Label(new Rect(row.x + 64, row.y, 48, row.height), $"{w.LiftLevel(l, w.Time) + 1f:0.0}층", labelStyle);
                GUI.Label(new Rect(row.x + 112, row.y, row.width - 112, row.height), LiftState(l), labelStyle);
            }
            GUILayout.Space(8);
        }

        static string LiftState(Lift l)
        {
            if (l.phase == 0) return "대기";
            int target = l.targetLevel + 1;
            if (l.outboundJob)
                switch (l.phase)
                {
                    case 1: return $"출고 · {target}층으로 이동";
                    case 2: return $"출고 · {target}층에서 싣는 중";
                    case 3: return "출고 · 1층으로 하강";
                    default: return "출고 · 컨베이어로 반출";
                }
            switch (l.phase)
            {
                case 1: return "입고 · 1층으로 이동";
                case 2: return "입고 · 컨베이어에서 싣는 중";
                case 3: return $"입고 · {target}층으로 상승";
                default: return $"입고 · {target}층 P&D로 반입";
            }
        }

        void DrawProperties(SimWorld w)
        {
            GUILayout.Space(4);
            if (sel == Sel.Shuttle && selId < w.Shuttles.Count) DrawShuttle(w, w.Shuttles[selId]);
            else if (sel == Sel.Pallet) DrawPallet(w, selId);
            else GUILayout.Label("화면에서 셔틀이나 화물을 왼쪽 클릭하면 속성이 여기에 보입니다. 선택한 대상은 윤곽선으로 표시됩니다.", eventStyle);
            if (sel != Sel.None)
            {
                GUILayout.Space(12);
                GUILayout.BeginHorizontal();
                var cam = Cam; var target = SelectedTransform();
                bool following = cam != null && cam.follow != null;
                if (cam != null && Btn(following ? "따라가기 끄기" : "카메라 따라가기", Kind.Soft, following, 0, target != null || following, true))
                    cam.Follow(following ? null : target, 8f);
                if (Btn("선택 해제", Kind.Soft)) Later(ClearSelection);
                GUILayout.EndHorizontal();
            }
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

        void Title(string kind, string name)
        {
            GUILayout.Label(kind, headStyle);
            GUILayout.Label(name, new GUIStyle(bigStyle) { fontSize = 20 }, GUILayout.Height(30));
            GUILayout.Space(4);
        }

        void DrawShuttle(SimWorld w, Shuttle s)
        {
            Title("셔틀", $"S{s.id}");
            Row("층", $"{s.level + 1}층");
            Row("위치", w.CellName(s.cell));
            Row("상태", ActName(s.act));
            Row("속도", $"{w.CurrentSpeed(s, w.Time):F2} m/s");
            Row("바퀴", s.axis == 0 ? "통로 방향 (X)" : "레인 방향 (Y)");
            Row("적재", s.loaded ? $"P{s.carried}" : "없음");
            Row("가동률", w.Time > 0 ? $"{s.busyTime / w.Time:P0}" : "-");
            Row("제어", BehaviorName(env.Behavior));
            Section("작업");
            if (s.task != null)
            {
                Row("종류", $"{(s.task.kind == TaskKind.Inbound ? "입고" : "출고")} P{s.task.pallet}");
                Row("단계", s.task.phase == TaskPhase.ToPick ? "들러 가는 중" : "내리러 가는 중");
                Row("목표", w.CellName(s.task.phase == TaskPhase.ToPick ? s.task.pickCell : s.task.dropCell));
                Row("경과", $"{w.Time - s.task.created:F0} s");
            }
            else Row("작업", "없음");
        }

        static string BehaviorName(BehaviorType b)
        {
            return b == BehaviorType.HeuristicOnly ? "규칙 기반" : b == BehaviorType.InferenceOnly ? "학습 모델" : "학습 (기본)";
        }

        void DrawPallet(SimWorld w, int pallet)
        {
            Title("파렛트", $"P{pallet}");
            if (!w.FindPallet(pallet, out var where, out int level, out var task))
            {
                Row("위치", "출고됨 (시스템 밖)");
                return;
            }
            if (level >= 0) Row("층", $"{level + 1}층");
            Row("위치", where);
            Section("작업");
            if (task != null)
            {
                Row("종류", task.kind == TaskKind.Inbound ? "입고" : "출고");
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
            var r = EventsRect; Panel(r);
            GUILayout.BeginArea(new Rect(r.x + 16, r.y + 6, r.width - 32, r.height - 12));
            GUILayout.BeginHorizontal(GUILayout.Height(28));
            GUILayout.Label("이벤트", valueStyle, GUILayout.Height(28));
            GUILayout.Label(w.Events.Count.ToString(), smallStyle, GUILayout.Height(28));
            GUILayout.Space(10);
            if (!eventsOpen && w.Events.Count > 0)
            {
                var e = w.Events[w.Events.Count - 1];
                GUILayout.Label(Clock(e.time), timeStyle, GUILayout.Width(62), GUILayout.Height(28));
                GUILayout.Label(e.text, labelStyle, GUILayout.Height(28));
            }
            GUILayout.FlexibleSpace();
            if (eventsOpen)
            {
                bool can = sel != Sel.None;
                GUILayout.BeginVertical(GUILayout.Width(190));
                bool only = Switch(can ? $"선택 항목만 ({SelName()})" : "선택 항목만", onlySelected && can, can);
                if (only != onlySelected) Later(() => onlySelected = only);
                GUILayout.EndVertical();
                GUILayout.Space(8);
            }
            if (Btn(eventsOpen ? "접기" : "펼치기", Kind.Soft, false, 64)) Later(() => eventsOpen = !eventsOpen);
            GUILayout.EndHorizontal();

            if (eventsOpen)
            {
                GUILayout.Space(4);
                eventScroll = Scroll(eventScroll);
                GUILayout.BeginVertical(GUILayout.Width(r.width - 32 - 14));
                int shown = 0;
                for (int i = w.Events.Count - 1; i >= 0; i--)
                {
                    var e = w.Events[i];
                    if (onlySelected && sel != Sel.None && !(sel == Sel.Shuttle ? e.shuttle == selId : e.pallet == selId)) continue;
                    Rect row = GUILayoutUtility.GetRect(10f, 22f, GUILayout.ExpandWidth(true), GUILayout.Height(22f));
                    bool clickable = e.shuttle >= 0 || e.pallet >= 0;
                    if (clickable && Hover(row)) Fill(row, T.surface, ctrlShape);
                    GUI.Label(new Rect(row.x + 6, row.y, 64, row.height), Clock(e.time), timeStyle);
                    GUI.Label(new Rect(row.x + 74, row.y, row.width - 80, row.height), e.text, labelStyle);
                    if (clickable && GUI.Button(row, GUIContent.none, GUIStyle.none)) // click an event to select what it is about
                    {
                        var ev = e;
                        Later(() => { if (ev.shuttle >= 0) Select(Sel.Shuttle, ev.shuttle); else Select(Sel.Pallet, ev.pallet); });
                    }
                    shown++;
                }
                if (shown == 0) GUILayout.Label(w.LogEvents ? "아직 이벤트가 없습니다." : "이벤트 기록이 꺼져 있습니다.", smallStyle);
                GUILayout.EndVertical();
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
