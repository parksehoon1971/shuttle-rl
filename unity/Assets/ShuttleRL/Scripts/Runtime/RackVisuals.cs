using System.Collections.Generic;
using ShuttleRL.Core;
using UnityEngine;

namespace ShuttleRL
{
    /// <summary>References to the imported Blender FBX models (Assets/ShuttleRL/Models).</summary>
    [System.Serializable]
    public class RackModels
    {
        public GameObject laneRail, aisleRail, junction, pdStation, liftCarriage, liftMastSegment,
                          conveyor, upright, beam, pallet, palletLoad, shuttle;
    }

    /// <summary>Builds the static rack from the layout and moves shuttles, lifts and pallets every frame.</summary>
    public class RackVisuals
    {
        readonly Transform root;
        readonly RackModels m;
        Transform staticRoot, dynamicRoot;
        readonly List<ShuttleView> shuttles = new List<ShuttleView>();
        readonly List<Transform> carriages = new List<Transform>();
        readonly List<Transform> pallets = new List<Transform>();

        class ShuttleView
        {
            public Transform t, jack, wheelsX, wheelsY;
            public Vector3 jack0, wx0, wy0;
        }

        public RackVisuals(Transform root, RackModels models) { this.root = root; m = models; }

        GameObject Spawn(GameObject prefab, Transform parent, Vector3 localPos, string name = null)
        {
            if (prefab == null) return null;
            var go = Object.Instantiate(prefab, parent);
            go.transform.localPosition = localPos;
            // keep the model's own import rotation (Blender Z-up -> Unity Y-up); overwriting it stood models on end
            go.transform.localRotation = prefab.transform.localRotation;
            go.transform.localScale = prefab.transform.localScale;
            if (name != null) go.name = name;
            return go;
        }

        static Transform Find(Transform t, string name)
        {
            if (t.name == name) return t;
            foreach (Transform c in t) { var r = Find(c, name); if (r != null) return r; }
            return null;
        }

        // ------------------------------------------------------------------ static rack
        public void BuildStatic(SimWorld w, RackGeometry g)
        {
            if (staticRoot != null) Object.Destroy(staticRoot.gameObject);
            if (dynamicRoot != null) Object.Destroy(dynamicRoot.gameObject);
            shuttles.Clear(); carriages.Clear(); pallets.Clear();
            staticRoot = new GameObject("RackStatic").transform; staticRoot.SetParent(root, false);
            dynamicRoot = new GameObject("RackDynamic").transform; dynamicRoot.SetParent(root, false);
            var c = w.Cfg; var L = w.Layout;

            for (int lv = 0; lv < c.levels; lv++)
            {
                var levelRoot = new GameObject($"Level_{lv}").transform; levelRoot.SetParent(staticRoot, false);
                float y = g.LevelY(lv);
                for (int cell = 0; cell < L.CellCount; cell++)
                {
                    var p = g.Cell(cell, lv);
                    switch (L.Type[cell])
                    {
                        case CellType.Lane:
                            Spawn(m.laneRail, levelRoot, p);
                            Spawn(m.beam, levelRoot, p + new Vector3(0, 0, Mathf.Sign(p.z - g.AisleZ(L.Block[cell])) * c.pitchLane / 2f));
                            break;
                        case CellType.Junction:
                            Spawn(m.junction, levelRoot, p);
                            if (L.X(cell) < L.Lanes - 1)
                            {
                                var ar = Spawn(m.aisleRail, levelRoot, p + new Vector3(c.pitchAisle / 2f, 0, 0));
                                if (ar != null) ar.transform.localScale = new Vector3((c.pitchAisle - 1.02f) / c.pitchAisle, 1, 1);
                            }
                            break;
                        case CellType.PD:
                            Spawn(m.pdStation, levelRoot, p);
                            break;
                    }
                }
                // uprights at lane boundaries
                foreach (var lane in L.LaneCells)
                {
                    var front = g.Cell(lane[0], lv);
                    var back = g.Cell(lane[lane.Length - 1], lv);
                    float dir = Mathf.Sign(back.z - front.z);
                    for (int k = 0; k <= lane.Length; k++)
                    {
                        float z = front.z + dir * (k - 0.5f) * c.pitchLane;
                        Spawn(m.upright, levelRoot, new Vector3(front.x - c.pitchAisle / 2f, y, z));
                        if (L.X(lane[0]) == L.Lanes - 1) Spawn(m.upright, levelRoot, new Vector3(front.x + c.pitchAisle / 2f, y, z));
                    }
                }
            }
            // lifts: mast segments, carriage, floor conveyors
            foreach (var site in L.Lifts)
            {
                for (int lv = 0; lv <= c.levels; lv++) Spawn(m.liftMastSegment, staticRoot, g.Lift(site, lv));
                var conv0 = g.Lift(site, 0);
                float s = g.OutwardSign(site);
                for (int j = 0; j < 2; j++)
                    Spawn(m.conveyor, staticRoot, conv0 + new Vector3(s * (c.carriageLength / 2f + 1.3f * (j + 0.5f)), 0, 0));
                var car = Spawn(m.liftCarriage, dynamicRoot, conv0, "LiftCarriage");
                carriages.Add(car != null ? car.transform : new GameObject("LiftCarriage").transform);
            }
        }

        // ------------------------------------------------------------------ dynamic objects
        public void EnsureDynamic(SimWorld w)
        {
            while (shuttles.Count < w.Shuttles.Count)
            {
                var go = Spawn(m.shuttle, dynamicRoot, Vector3.zero, $"Shuttle_{shuttles.Count}") ?? new GameObject("Shuttle");
                var v = new ShuttleView { t = go.transform, jack = Find(go.transform, "Jack"), wheelsX = Find(go.transform, "WheelsX"), wheelsY = Find(go.transform, "WheelsY") };
                if (v.jack) v.jack0 = v.jack.localPosition;
                if (v.wheelsX) v.wx0 = v.wheelsX.localPosition;
                if (v.wheelsY) v.wy0 = v.wheelsY.localPosition;
                shuttles.Add(v);
            }
            for (int i = 0; i < shuttles.Count; i++) shuttles[i].t.gameObject.SetActive(i < w.Shuttles.Count);
        }

        Transform PalletAt(int index)
        {
            while (pallets.Count <= index)
            {
                var go = new GameObject($"Pallet_{pallets.Count}");
                go.transform.SetParent(dynamicRoot, false);
                Spawn(m.pallet, go.transform, Vector3.zero);
                Spawn(m.palletLoad, go.transform, new Vector3(0, RackGeometry.PalletHeight, 0));
                pallets.Add(go.transform);
            }
            var t = pallets[index];
            if (!t.gameObject.activeSelf) t.gameObject.SetActive(true);
            return t;
        }

        /// <summary>World-up offset of d meters expressed in the parent's local space (models may be imported rotated).</summary>
        static Vector3 Up(Transform child, float d)
        {
            var parent = child.parent;
            return parent != null ? parent.InverseTransformVector(Vector3.up * d) : Vector3.up * d;
        }

        public void Sync(SimWorld w, RackGeometry g)
        {
            if (dynamicRoot == null) return;
            double now = w.Time;
            int p = 0;
            const float wheelDrop = 0.015f;
            // shuttles (+ carried pallets)
            for (int i = 0; i < w.Shuttles.Count && i < shuttles.Count; i++)
            {
                var s = w.Shuttles[i]; var v = shuttles[i];
                Vector3 a = g.Cell(s.fromCell, s.level), b = g.Cell(s.toCell, s.level);
                v.t.localPosition = Vector3.Lerp(a, b, w.MoveProgress(s, now));
                float jack = w.JackHeight(s, now);
                if (v.jack) v.jack.localPosition = v.jack0 + Up(v.jack, jack * RackGeometry.JackStroke);
                if (v.wheelsX) v.wheelsX.localPosition = v.wx0 + Up(v.wheelsX, s.axis == 0 ? 0 : wheelDrop);
                if (v.wheelsY) v.wheelsY.localPosition = v.wy0 + Up(v.wheelsY, s.axis == 1 ? 0 : wheelDrop);
                bool carrying = s.loaded || s.act == ShuttleAct.JackDown;
                if (carrying)
                    PalletAt(p++).localPosition = v.t.localPosition + Vector3.up * (RackGeometry.RailHeight + jack * (RackGeometry.JackStroke - 0.01f));
            }
            // pallets stored in cells and on P&D stations (hidden while a shuttle is jacking them up)
            for (int lv = 0; lv < w.Cfg.levels; lv++)
            {
                var arr = w.PalletAt[lv];
                for (int c = 0; c < arr.Length; c++)
                    if (arr[c] >= 0) PalletAt(p++).localPosition = g.Cell(c, lv) + Vector3.up * RackGeometry.RailHeight;
            }
            // lifts
            for (int i = 0; i < w.Lifts.Count && i < carriages.Count; i++)
            {
                var lift = w.Lifts[i];
                var pos = g.Lift(lift.site, w.LiftLevel(lift, now));
                carriages[i].localPosition = pos;
                if (w.LiftHasPallet(lift)) PalletAt(p++).localPosition = pos + Vector3.up * RackGeometry.RailHeight;
            }
            // inbound queue: show up to two pallets on each lift's floor conveyor
            int q = w.InboundQueue.Count;
            for (int i = 0; i < w.Lifts.Count && q > 0; i++)
            {
                var site = w.Lifts[i].site; float s = g.OutwardSign(site);
                for (int j = 0; j < 2 && q > 0; j++, q--)
                    PalletAt(p++).localPosition = g.Lift(site, 0) + new Vector3(s * (w.Cfg.carriageLength / 2f + 1.3f * (j + 0.5f)), RackGeometry.RailHeight, 0);
            }
            for (int i = p; i < pallets.Count; i++) if (pallets[i].gameObject.activeSelf) pallets[i].gameObject.SetActive(false);
        }
    }
}
