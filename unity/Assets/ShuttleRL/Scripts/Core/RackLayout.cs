using System.Collections.Generic;

namespace ShuttleRL.Core
{
    public enum CellType : byte { None = 0, Junction = 1, Lane = 2, PD = 3 }

    /// <summary>Grid directions. X = along the main aisle, Y = along the lanes.</summary>
    public static class Dir
    {
        public const int PX = 0, NX = 1, PY = 2, NY = 3;
        public static readonly int[] DX = { 1, -1, 0, 0 };
        public static readonly int[] DY = { 0, 0, 1, -1 };
        public static bool IsX(int d) { return d < 2; }
    }

    public struct LiftSite
    {
        public int aisle;   // aisle index
        public int end;     // 0 = aisle start (x = -1), 1 = aisle end (x = lanes)
        public int pdCell;  // grid index of the P&D cell (same on every level)
    }

    /// <summary>
    /// Per-level grid (identical on every level). Grid x runs -1..lanes (stored as gx = x + 1),
    /// grid y covers every aisle block: block a has rows a*(2C+1) .. a*(2C+1)+2C, aisle row in the middle.
    /// </summary>
    public class RackLayout
    {
        public readonly int Lanes, Cells, Aisles, W, H;
        public readonly CellType[] Type;
        public readonly int[] Block;          // aisle block of each cell
        public readonly List<LiftSite> Lifts = new List<LiftSite>();
        public readonly List<int> StorageCells = new List<int>();
        /// <summary>Each lane as its cells ordered from the junction outward (index 0 = front cell next to the aisle).</summary>
        public readonly List<int[]> LaneCells = new List<int[]>();
        public readonly int[] LaneOf;         // lane index of a storage cell, -1 otherwise

        public RackLayout(RackConfig c, int liftCount)
        {
            Lanes = c.lanesPerAisle; Cells = c.cellsPerLane; Aisles = c.aisles;
            W = Lanes + 2; H = Aisles * (2 * Cells + 1);
            Type = new CellType[W * H];
            Block = new int[W * H];
            LaneOf = new int[W * H];
            for (int i = 0; i < LaneOf.Length; i++) LaneOf[i] = -1;
            for (int a = 0; a < Aisles; a++)
            {
                int ya = AisleRow(a);
                for (int y = ya - Cells; y <= ya + Cells; y++)
                    for (int gx = 0; gx < W; gx++) Block[Index(gx, y)] = a;
                for (int x = 0; x < Lanes; x++)
                {
                    Type[Index(x + 1, ya)] = CellType.Junction;
                    for (int side = -1; side <= 1; side += 2)
                    {
                        var lane = new int[Cells];
                        for (int k = 1; k <= Cells; k++)
                        {
                            int ci = Index(x + 1, ya + side * k);
                            Type[ci] = CellType.Lane; lane[k - 1] = ci; LaneOf[ci] = LaneCells.Count;
                        }
                        LaneCells.Add(lane);
                    }
                }
            }
            for (int i = 0; i < liftCount; i++)
            {
                var s = new LiftSite { aisle = i % Aisles, end = (i / Aisles) % 2 };
                int gx = s.end == 0 ? 0 : W - 1;
                s.pdCell = Index(gx, AisleRow(s.aisle));
                Type[s.pdCell] = CellType.PD;
                Lifts.Add(s);
            }
            for (int i = 0; i < Type.Length; i++) if (Type[i] == CellType.Lane) StorageCells.Add(i);
        }

        public int AisleRow(int a) { return a * (2 * Cells + 1) + Cells; }
        public int Index(int gx, int y) { return y * W + gx; }
        public int GX(int idx) { return idx % W; }
        public int GY(int idx) { return idx / W; }
        /// <summary>Logical x (-1 = west P&D column).</summary>
        public int X(int idx) { return idx % W - 1; }
        public bool IsAisleRow(int idx) { return GY(idx) == AisleRow(Block[idx]); }

        /// <summary>Neighbor in direction d along the static rail graph, or -1.</summary>
        public int Neighbor(int idx, int d)
        {
            var t = Type[idx];
            if (t == CellType.None) return -1;
            int gx = GX(idx) + Dir.DX[d], gy = GY(idx) + Dir.DY[d];
            if (gx < 0 || gx >= W || gy < 0 || gy >= H) return -1;
            int n = Index(gx, gy);
            var tn = Type[n];
            if (tn == CellType.None || Block[n] != Block[idx]) return -1;
            if (Dir.IsX(d))
                return (IsAisleRow(idx) && IsAisleRow(n)) ? n : -1;   // X moves only along the aisle row
            // Y moves only within a lane column (junction <-> lane cells); P&D has no Y rails
            if (t == CellType.PD || tn == CellType.PD) return -1;
            return n;
        }

        public int CellCount => W * H;
    }
}
