using ShuttleRL.Core;
using UnityEngine;

namespace ShuttleRL
{
    /// <summary>
    /// Maps simulation grid cells to Unity world positions (local to the environment root).
    /// Unity: X = along the main aisle, Z = along the lanes, Y = up. Matches the Blender module sizes.
    /// </summary>
    public class RackGeometry
    {
        public const float RailHeight = 0.20f;   // pallet bottom when resting on a rail ledge / roller
        public const float PalletHeight = 0.15f;
        public const float JackStroke = 0.04f;
        public const float GaugeHalf = 0.495f;

        readonly RackConfig cfg;
        readonly RackLayout layout;
        readonly float y0, blockDepth;

        public RackGeometry(RackConfig cfg, RackLayout layout)
        {
            this.cfg = cfg; this.layout = layout;
            y0 = cfg.aisleHalfWidth;
            blockDepth = 2f * (y0 + cfg.cellsPerLane * cfg.pitchLane) + 0.6f;
        }

        public float LevelY(float level) { return level * cfg.levelHeight; }

        public float XOf(int x)
        {
            if (x < 0) return -(cfg.pitchAisle / 2f + cfg.carriageLength / 2f);
            if (x >= layout.Lanes) return (layout.Lanes - 1) * cfg.pitchAisle + cfg.pitchAisle / 2f + cfg.carriageLength / 2f;
            return x * cfg.pitchAisle;
        }

        public float ZOfRow(int gy)
        {
            int a = gy / (2 * cfg.cellsPerLane + 1);
            int k = gy - layout.AisleRow(a);
            float za = a * blockDepth;
            if (k == 0) return za;
            return za + Mathf.Sign(k) * (y0 + (Mathf.Abs(k) - 0.5f) * cfg.pitchLane);
        }

        public float AisleZ(int aisle) { return aisle * blockDepth; }

        public Vector3 Cell(int cell, float level)
        {
            return new Vector3(XOf(layout.X(cell)), LevelY(level), ZOfRow(layout.GY(cell)));
        }

        /// <summary>Lift mast position (one carriage length beyond the P&amp;D station).</summary>
        public Vector3 Lift(LiftSite site, float level)
        {
            float x = site.end == 0
                ? -(cfg.pitchAisle / 2f + 1.5f * cfg.carriageLength)
                : (layout.Lanes - 1) * cfg.pitchAisle + cfg.pitchAisle / 2f + 1.5f * cfg.carriageLength;
            return new Vector3(x, LevelY(level), AisleZ(site.aisle));
        }

        /// <summary>Direction pointing from the P&amp;D away from the rack (where the floor conveyor goes).</summary>
        public float OutwardSign(LiftSite site) { return site.end == 0 ? -1f : 1f; }
    }
}
