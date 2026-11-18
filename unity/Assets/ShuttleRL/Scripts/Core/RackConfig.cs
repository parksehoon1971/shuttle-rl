using System;

namespace ShuttleRL.Core
{
    /// <summary>
    /// All tunable parameters of one rack instance. Plain C# so it can run outside Unity
    /// (fast tests) and inside Unity (Inspector / ML-Agents curriculum parameters).
    /// Distances in meters, times in seconds, speeds in m/s.
    /// </summary>
    [Serializable]
    public class RackConfig
    {
        // ---- Layout -------------------------------------------------------------
        public int levels = 2;            // rack levels (shuttles stay on their own level for now)
        public int aisles = 1;            // main aisles (parallel)
        public int lanesPerAisle = 4;     // lane columns along each aisle (lanes on both sides)
        public int cellsPerLane = 6;      // cells in one lane (<= 10)
        public int shuttlesPerLevel = 2;

        // ---- Demand -------------------------------------------------------------
        public float inboundPerHour = 120f;
        public float outboundPerHour = 120f;
        public float initialFill = 0.5f;  // fraction of storage cells holding a pallet at reset
        public int liftCountOverride = 0; // 0 = automatic (see ComputeLiftCount)

        // ---- Geometry (match blender/dimensions.json) ------------------------------
        public float pitchAisle = 1.40f;  // lane-to-lane distance along the aisle
        public float pitchLane = 1.30f;   // cell length inside a lane
        public float aisleHalfWidth = 0.70f; // aisle center to first lane cell edge (clears a 1.1 m pallet past the uprights)
        public float levelHeight = 1.60f;
        public float carriageLength = 1.40f;

        // ---- Shuttle --------------------------------------------------------------
        public float shuttleSpeedEmpty = 1.5f;
        public float shuttleSpeedLoaded = 1.0f;
        public float shuttleAccel = 0.5f;
        public float wheelSwitchTime = 2.0f;
        public float jackTime = 2.5f;
        public float waitTime = 1.0f;

        // ---- Lift -----------------------------------------------------------------
        public float liftSpeed = 1.0f;
        public float liftAccel = 0.8f;
        public float conveySpeed = 0.5f;
        public float stopTime = 0.5f;
        public float liftPositioningTime = 4.0f; // fine positioning + settling at each stop
        public float liftUtilization = 0.8f;

        // ---- Episode / learning ------------------------------------------------------
        public float episodeSeconds = 3600f;
        public float deadlockSeconds = 300f; // no pallet pick/drop for this long while shuttles have tasks
        public bool maskCollisions = true;   // true: moves into occupied cells are masked out
        public int seed = 0;

        public RackConfig Clone() { return (RackConfig)MemberwiseClone(); }

        public void Validate()
        {
            levels = Math.Max(1, levels);
            aisles = Math.Max(1, aisles);
            lanesPerAisle = Math.Max(1, lanesPerAisle);
            cellsPerLane = Math.Max(1, Math.Min(10, cellsPerLane));
            shuttlesPerLevel = Math.Max(aisles, shuttlesPerLevel); // at least one shuttle per aisle
            initialFill = Math.Max(0f, Math.Min(0.95f, initialFill));
        }

        /// <summary>Time to travel distance d from rest to rest with a trapezoidal profile.</summary>
        public static float TravelTime(float d, float v, float a)
        {
            if (d <= 0f) return 0f;
            if (d < v * v / a) return 2f * (float)Math.Sqrt(d / a);
            return d / v + v / a;
        }

        public float LiftTransferTime => carriageLength / conveySpeed + stopTime + liftPositioningTime;

        /// <summary>
        /// Lift count from rack size and demand:
        ///   cycle = 2 * travel(avg height) + 2 * transfer   (one pallet = one round trip)
        ///   n = ceil((in + out) / (3600 / cycle * utilization)), clamped to [aisles, 2 * aisles]
        /// One lift sits at the start of each aisle; extra lifts go to the far end of aisles.
        /// </summary>
        public int ComputeLiftCount()
        {
            if (liftCountOverride > 0) return Math.Min(liftCountOverride, 2 * aisles);
            float avgH = 0f;
            for (int l = 0; l < levels; l++) avgH += l * levelHeight;
            avgH /= levels;
            float cycle = 2f * TravelTime(avgH, liftSpeed, liftAccel) + 2f * LiftTransferTime;
            float perLift = 3600f / cycle * liftUtilization;
            int n = (int)Math.Ceiling((inboundPerHour + outboundPerHour) / Math.Max(1e-3f, perLift));
            return Math.Max(aisles, Math.Min(2 * aisles, n));
        }
    }
}
