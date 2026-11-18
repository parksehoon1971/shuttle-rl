using System;
using ShuttleRL.Core;
using UnityEngine;

namespace ShuttleRL
{
    /// <summary>
    /// Heads-up display (IMGUI, no Canvas needed):
    ///   top    : long rounded bar with simulation KPIs and speed buttons
    ///   right  : rounded panel, selected shuttle/pallet properties on top, event list below
    /// Left-click a shuttle or a pallet in the Game view to select it (Esc clears).
    /// RackEnvironment adds this automatically in visual mode.
    /// </summary>
    public class RackHud : MonoBehaviour
    {
        public RackEnvironment env;
        public float panelWidth = 340f;
        public bool showHud = true;

        enum Sel { None, Shuttle, Pallet }
        Sel sel; int selId = -1;
        bool onlySelected;
        Vector2 eventScroll;
        Texture2D roundTex;
        GUIStyle boxStyle, titleStyle, labelStyle, valueStyle, eventStyle, timeStyle, buttonStyle;
        GameObject marker;

        const float Pad = 10f, BarH = 46f;

        Rect BarRect => new Rect(Pad, Pad, Screen.width - 2 * Pad, BarH);
        Rect PanelRect => new Rect(Screen.width - panelWidth - Pad, Pad * 2 + BarH, panelWidth, Screen.height - BarH - Pad * 3);

        // ------------------------------------------------------------------ selection
        void Update()
        {
            if (env == null || env.World == null || env.Visuals == null) return;
#if ENABLE_LEGACY_INPUT_MANAGER
            if (Input.GetKeyDown(KeyCode.Escape)) { sel = Sel.None; selId = -1; }
            if (Input.GetKeyDown(KeyCode.Tab)) showHud = !showHud;
            bool alt = Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt);
            if (Input.GetMouseButtonDown(0) && !alt && !OverHud(Input.mousePosition)) Pick(Input.mousePosition);
#endif
            UpdateMarker();
        }

        bool OverHud(Vector3 mouse)
        {
            if (!showHud) return false;
            var gui = new Vector2(mouse.x, Screen.height - mouse.y); // IMGUI y is top-down
            return BarRect.Contains(gui) || PanelRect.Contains(gui);
        }

        void Pick(Vector3 mouse)
        {
            var cam = Camera.main; if (cam == null) return;
            if (!Physics.Raycast(cam.ScreenPointToRay(mouse), out var hit, 1000f)) return;
            var tag = hit.collider.GetComponentInParent<RackSelectable>();
            if (tag == null) return;
            if (tag.isShuttle) { sel = Sel.Shuttle; selId = tag.index; }
            else
            {
                int pid = env.Visuals.SlotPallet(tag.index);
                if (pid >= 0) { sel = Sel.Pallet; selId = pid; }
            }
        }

        void UpdateMarker()
        {
            Transform target = null;
            if (sel == Sel.Shuttle) target = env.Visuals.ShuttleTransform(selId);
            else if (sel == Sel.Pallet) target = env.Visuals.PalletTransform(selId);
            if (marker == null)
            {
                marker = GameObject.CreatePrimitive(PrimitiveType.Cube);
                marker.name = "SelectionMarker";
                Destroy(marker.GetComponent<Collider>());
                marker.transform.localScale = new Vector3(0.25f, 0.25f, 0.25f);
                var r = marker.GetComponent<Renderer>();
                if (r != null) r.material.color = new Color(1f, 0.8f, 0.1f);
            }
            marker.SetActive(target != null);
            if (target == null) return;
            float top = sel == Sel.Pallet ? 1.6f : 0.9f;
            marker.transform.position = target.position + Vector3.up * (top + 0.1f * Mathf.Sin(Time.unscaledTime * 4f));
            marker.transform.rotation = Quaternion.Euler(45f, Time.unscaledTime * 90f, 45f);
        }

        // ------------------------------------------------------------------ drawing
        void EnsureStyles()
        {
            if (roundTex != null && boxStyle != null) return;
            roundTex = MakeRoundTexture(32, 10);
            boxStyle = new GUIStyle { normal = { background = roundTex }, border = new RectOffset(12, 12, 12, 12), padding = new RectOffset(14, 14, 10, 10) };
            titleStyle = new GUIStyle(GUI.skin.label) { fontSize = 15, fontStyle = FontStyle.Bold, normal = { textColor = new Color(1f, 0.85f, 0.35f) } };
            labelStyle = new GUIStyle(GUI.skin.label) { fontSize = 13, normal = { textColor = new Color(0.75f, 0.8f, 0.88f) } };
            valueStyle = new GUIStyle(GUI.skin.label) { fontSize = 13, fontStyle = FontStyle.Bold, normal = { textColor = Color.white } };
            eventStyle = new GUIStyle(GUI.skin.label) { fontSize = 12, wordWrap = true, normal = { textColor = new Color(0.9f, 0.92f, 0.95f) } };
            timeStyle = new GUIStyle(GUI.skin.label) { fontSize = 12, normal = { textColor = new Color(0.55f, 0.8f, 1f) } };
            buttonStyle = new GUIStyle(GUI.skin.button) { fontSize = 13 };
        }

        static Texture2D MakeRoundTexture(int size, int radius)
        {
            var t = new Texture2D(size, size, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    float cx = Mathf.Clamp(x + 0.5f, radius, size - radius), cy = Mathf.Clamp(y + 0.5f, radius, size - radius);
                    float d = Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), new Vector2(cx, cy));
                    float a = Mathf.Clamp01(radius - d + 0.5f);
                    t.SetPixel(x, y, new Color(1, 1, 1, a));
                }
            t.Apply();
            return t;
        }

        void RoundBox(Rect r)
        {
            var c = GUI.backgroundColor;
            GUI.backgroundColor = new Color(0.08f, 0.1f, 0.14f, 0.82f);
            GUI.Box(r, GUIContent.none, boxStyle);
            GUI.backgroundColor = c;
        }

        static string Clock(double t)
        {
            var ts = TimeSpan.FromSeconds(Math.Max(0, t));
            return $"{(int)ts.TotalHours:00}:{ts.Minutes:00}:{ts.Seconds:00}";
        }

        void OnGUI()
        {
            if (!showHud || env == null || env.World == null) return;
            EnsureStyles();
            DrawTopBar(env.World);
            DrawPanel(env.World);
        }

        void DrawTopBar(SimWorld w)
        {
            var r = BarRect; RoundBox(r);
            var st = w.Stats;
            double hours = Math.Max(1e-6, w.Time / 3600.0);
            string[] items =
            {
                $"시간  {Clock(w.Time)}",
                $"처리량  {(st.inboundStored + st.outboundShipped) / hours:F0}/h",
                $"입고  {st.inboundStored}/{st.inboundArrived}",
                $"출고  {st.outboundShipped}/{st.outboundOrdered}",
                $"입고 대기  {w.InboundQueue.Count}",
                $"셔틀 {w.Shuttles.Count}대 · 가동률  {w.Utilization():P0}",
                $"리프트  {w.Lifts.Count}대",
                $"교착  {st.deadlocks}",
                $"에피소드  {env.Episode + 1}",
            };
            GUILayout.BeginArea(new Rect(r.x + 16, r.y + 12, r.width - 32, r.height - 16));
            GUILayout.BeginHorizontal();
            foreach (var it in items) { GUILayout.Label(it, valueStyle); GUILayout.Space(18); }
            GUILayout.FlexibleSpace();
            GUILayout.Label($"배속 x{env.visualTimeScale:0.#}", valueStyle);
            if (GUILayout.Button("−", buttonStyle, GUILayout.Width(26))) env.visualTimeScale = Mathf.Max(0.25f, env.visualTimeScale / 2f);
            if (GUILayout.Button("+", buttonStyle, GUILayout.Width(26))) env.visualTimeScale = Mathf.Min(64f, env.visualTimeScale * 2f);
            GUILayout.EndHorizontal();
            GUILayout.EndArea();
        }

        void Row(string k, string v)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(k, labelStyle, GUILayout.Width(96));
            GUILayout.Label(v, valueStyle);
            GUILayout.EndHorizontal();
        }

        void DrawPanel(SimWorld w)
        {
            var r = PanelRect; RoundBox(r);
            GUILayout.BeginArea(new Rect(r.x + 14, r.y + 10, r.width - 28, r.height - 20));

            // ---- properties (top)
            GUILayout.Label("선택 항목", titleStyle);
            if (sel == Sel.Shuttle && selId < w.Shuttles.Count) DrawShuttle(w, w.Shuttles[selId]);
            else if (sel == Sel.Pallet) DrawPallet(w, selId);
            else GUILayout.Label("셔틀이나 화물을 클릭하세요 (Esc 해제, Tab HUD 숨기기)", eventStyle);

            GUILayout.Space(10);
            // ---- events (bottom)
            GUILayout.BeginHorizontal();
            GUILayout.Label("이벤트", titleStyle);
            GUILayout.FlexibleSpace();
            GUI.enabled = sel != Sel.None;
            onlySelected = GUILayout.Toggle(onlySelected && sel != Sel.None, " 선택 항목만", labelStyle);
            GUI.enabled = true;
            GUILayout.EndHorizontal();
            eventScroll = GUILayout.BeginScrollView(eventScroll);
            for (int i = w.Events.Count - 1; i >= 0; i--)
            {
                var e = w.Events[i];
                if (onlySelected && sel != Sel.None && !(sel == Sel.Shuttle ? e.shuttle == selId : e.pallet == selId)) continue;
                GUILayout.BeginHorizontal();
                GUILayout.Label(Clock(e.time), timeStyle, GUILayout.Width(62));
                GUILayout.Label(e.text, eventStyle);
                GUILayout.EndHorizontal();
            }
            GUILayout.EndScrollView();
            GUILayout.EndArea();
        }

        static string ActName(ShuttleAct a)
        {
            switch (a)
            {
                case ShuttleAct.Move: return "주행";
                case ShuttleAct.Brake: return "감속 정지";
                case ShuttleAct.JackUp: return "잭 올림 (들기)";
                case ShuttleAct.JackDown: return "잭 내림 (내리기)";
                case ShuttleAct.Wait: return "대기";
                default: return "정지";
            }
        }

        void DrawShuttle(SimWorld w, Shuttle s)
        {
            Row("종류", $"셔틀 S{s.id}");
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
        }

        void DrawPallet(SimWorld w, int pallet)
        {
            Row("종류", $"파렛트 P{pallet}");
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
    }
}
