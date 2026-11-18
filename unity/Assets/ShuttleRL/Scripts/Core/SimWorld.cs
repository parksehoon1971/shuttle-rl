using System;
using System.Collections.Generic;

namespace ShuttleRL.Core
{
    public enum TaskKind { Inbound, Outbound }
    public enum TaskPhase { Waiting, Released, ToPick, ToDrop, Done }
    public enum PDState { Free, ReservedIn, OccupiedIn, ReservedOut, OccupiedOut }
    public enum ShuttleAct { Idle, Wait, Move, JackUp, JackDown }

    public class ShuttleTask
    {
        public int id, level, pickCell, dropCell, lift = -1, shuttle = -1, pallet = -1;
        public TaskKind kind;
        public TaskPhase phase;
        public double created;
    }

    public class Shuttle
    {
        public int id, level, cell, fromCell, toCell, axis; // axis 0 = X wheels down, 1 = Y wheels down
        public bool loaded, awaiting;
        public int carried = -1;
        public ShuttleAct act;
        public double actStart, actEnd, switchEnd;
        public ShuttleTask task;
        public float reward;
        public int lastDist = -1, blockedCount, yieldWait;
        public double lastDecision;
        public double busyTime;
    }

    public class Lift
    {
        public LiftSite site;
        public int phase;            // 0 idle, 1..4 job phases
        public bool outboundJob;
        public int targetLevel, pallet = -1;
        public double phaseStart, phaseEnd;
        public float fromLevel, toLevel, level;
        public ShuttleTask task;     // inbound task created for this job
    }

    public class SimStats
    {
        public int inboundArrived, inboundStored, outboundOrdered, outboundShipped;
        public double inboundCycleSum, outboundCycleSum;
        public int invalidActions, collisionAttempts, deadlocks, moves;
        public int maxInboundQueue;
        public double shuttleBusySum;
        public void Reset() { this.inboundArrived = inboundStored = outboundOrdered = outboundShipped = 0; inboundCycleSum = outboundCycleSum = 0; invalidActions = collisionAttempts = deadlocks = moves = 0; maxInboundQueue = 0; shuttleBusySum = 0; }
    }

    /// <summary>
    /// Event-driven simulation of a multi-level pallet shuttle rack.
    /// Shuttles are decision makers: whenever one finishes an action it becomes "awaiting" and the
    /// caller (ML-Agents agent or heuristic) must call ApplyAction for it. Time never advances while
    /// any shuttle is awaiting an action, so results do not depend on the Unity frame rate.
    /// </summary>
    public class SimWorld
    {
        public const int ActionCount = 6; // 0 wait, 1 +X, 2 -X, 3 +Y, 4 -Y, 5 handle (jack up/down)
        public const int Win = 7, WinR = 3, WinCh = 6, StateSize = 14;
        public const int ObsSize = StateSize + Win * Win * WinCh;

        // reward constants
        public float rPick = 0.1f, rDrop = 0.5f, rGroupTask = 0.5f, rInvalid = -0.05f, rShaping = 0.01f, rTimePerSec = -0.0005f, rDeadlock = -1f;

        public RackConfig Cfg { get; private set; }
        public RackLayout Layout { get; private set; }
        public double Time { get; private set; }
        public readonly List<Shuttle> Shuttles = new List<Shuttle>();
        public readonly List<Lift> Lifts = new List<Lift>();
        public readonly List<ShuttleTask> Tasks = new List<ShuttleTask>();     // open tasks
        public readonly Queue<(int pallet, double t)> InboundQueue = new Queue<(int, double)>();
        public readonly SimStats Stats = new SimStats();
        public float GroupReward;      // consumed by the caller
        public bool Deadlocked { get; private set; }
        public bool Done => Deadlocked || Time >= Cfg.episodeSeconds;

        public int[][] PalletAt;   // [level][cell] pallet id or -1
        public bool[][] Reserved;  // [level][cell] storage cell reserved by a task
        public int[][] Occ;        // [level][cell] shuttle id (current or move target) or -1
        public PDState[][] PD;     // [level][lift]
        public double[][] PDReady; // time pallet arrived on PD (outbound)

        Random rng;
        int nextPallet, nextTask;
        double nextArrival, nextOrder, lastProgress;
        readonly List<ShuttleTask> outboundWaiting = new List<ShuttleTask>();
        int[] bfsBuf; int[] bfsQueue;

        public SimWorld(RackConfig cfg) { Reset(cfg); }

        // ------------------------------------------------------------------ reset
        public void Reset(RackConfig cfg)
        {
            Cfg = cfg.Clone(); Cfg.Validate();
            rng = new Random(Cfg.seed);
            Layout = new RackLayout(Cfg, Cfg.ComputeLiftCount());
            int n = Layout.CellCount, L = Cfg.levels;
            bfsBuf = new int[n]; bfsQueue = new int[n];
            PalletAt = new int[L][]; Reserved = new bool[L][]; Occ = new int[L][]; PD = new PDState[L][]; PDReady = new double[L][];
            Time = 0; nextPallet = 0; nextTask = 0; Deadlocked = false; GroupReward = 0; lastProgress = 0;
            Shuttles.Clear(); Lifts.Clear(); Tasks.Clear(); InboundQueue.Clear(); outboundWaiting.Clear(); Stats.Reset();
            for (int l = 0; l < L; l++)
            {
                PalletAt[l] = new int[n]; Reserved[l] = new bool[n]; Occ[l] = new int[n];
                PD[l] = new PDState[Layout.Lifts.Count]; PDReady[l] = new double[Layout.Lifts.Count];
                for (int i = 0; i < n; i++) { PalletAt[l][i] = -1; Occ[l][i] = -1; }
                foreach (var c in Layout.StorageCells)
                    if (rng.NextDouble() < Cfg.initialFill) PalletAt[l][c] = nextPallet++;
                // shuttles on random rail cells (not P&D). Aisle blocks are not connected to each other
                // (no cross aisle yet), so shuttles are spread over the aisles round-robin.
                for (int s = 0; s < Cfg.shuttlesPerLevel; s++)
                {
                    int block = s % Cfg.aisles;
                    var cand = new List<int>();
                    for (int i = 0; i < n; i++)
                        if ((Layout.Type[i] == CellType.Lane || Layout.Type[i] == CellType.Junction) && Layout.Block[i] == block && Occ[l][i] < 0) cand.Add(i);
                    if (cand.Count == 0) continue;
                    int c = cand[rng.Next(cand.Count)];
                    var sh = new Shuttle { id = Shuttles.Count, level = l, cell = c, fromCell = c, toCell = c,
                        axis = Layout.Type[c] == CellType.Lane ? 1 : 0, awaiting = true };
                    Occ[l][c] = sh.id; Shuttles.Add(sh);
                }
            }
            for (int i = 0; i < Layout.Lifts.Count; i++) Lifts.Add(new Lift { site = Layout.Lifts[i] });
            nextArrival = Exp(Cfg.inboundPerHour); nextOrder = Exp(Cfg.outboundPerHour);
        }

        double Exp(float perHour) { return perHour <= 0 ? double.MaxValue : -Math.Log(1 - rng.NextDouble()) * 3600.0 / perHour; }

        // ------------------------------------------------------------------ stepping
        /// <summary>Advance until a shuttle needs an action or maxDt passes. Returns true if actions are needed.</summary>
        public bool Advance(double maxDt)
        {
            double limit = Time + maxDt;
            while (true)
            {
                if (AnyAwaiting()) return true;
                if (Done) return false;
                double t = NextEventTime();
                if (t > limit) { Time = Math.Min(limit, Cfg.episodeSeconds); return false; }
                Time = Math.Max(Time, t);
                ProcessEvents();
            }
        }

        public bool AnyAwaiting() { foreach (var s in Shuttles) if (s.awaiting) return true; return false; }

        double NextEventTime()
        {
            double t = Math.Min(nextArrival, nextOrder);
            foreach (var s in Shuttles) if (s.act != ShuttleAct.Idle) t = Math.Min(t, s.actEnd);
            foreach (var l in Lifts) if (l.phase != 0) t = Math.Min(t, l.phaseEnd);
            return Math.Min(t, Cfg.episodeSeconds);
        }

        void ProcessEvents()
        {
            foreach (var s in Shuttles)
                if (s.act != ShuttleAct.Idle && s.actEnd <= Time + 1e-9) CompleteShuttle(s);
            foreach (var l in Lifts)
                if (l.phase != 0 && l.phaseEnd <= Time + 1e-9) AdvanceLift(l);
            while (nextArrival <= Time)
            {
                InboundQueue.Enqueue((nextPallet++, nextArrival)); Stats.inboundArrived++;
                Stats.maxInboundQueue = Math.Max(Stats.maxInboundQueue, InboundQueue.Count);
                nextArrival += Exp(Cfg.inboundPerHour);
            }
            while (nextOrder <= Time) { CreateOutboundOrder(); nextOrder += Exp(Cfg.outboundPerHour); }
            Dispatch();
            bool work = false;
            foreach (var s in Shuttles) if (s.task != null) { work = true; break; }
            if (work && Time - lastProgress > Cfg.deadlockSeconds && !Deadlocked)
            {
                Deadlocked = true; Stats.deadlocks++; GroupReward += rDeadlock;
            }
            if (!work) lastProgress = Time;
        }

        // ------------------------------------------------------------------ WCS (controller)
        void CreateOutboundOrder()
        {
            // Lanes are deep (LIFO): only the front pallet of a lane can be carried out, because a loaded
            // shuttle cannot pass under other pallets. One open task per lane keeps the order consistent.
            var cand = new List<(int l, int c)>();
            for (int l = 0; l < Cfg.levels; l++)
                foreach (var lane in Layout.LaneCells)
                {
                    if (LaneBusy(l, lane)) continue;
                    foreach (var c in lane) if (PalletAt[l][c] >= 0) { cand.Add((l, c)); break; }
                }
            if (cand.Count == 0) return;
            var p = cand[rng.Next(cand.Count)];
            Reserved[p.l][p.c] = true;
            var t = new ShuttleTask { id = nextTask++, kind = TaskKind.Outbound, level = p.l, pickCell = p.c, dropCell = -1,
                pallet = PalletAt[p.l][p.c], phase = TaskPhase.Waiting, created = Time };
            outboundWaiting.Add(t); Stats.outboundOrdered++;
        }

        void Dispatch()
        {
            // 1) release outbound orders when a P&D of their aisle is free on their level
            for (int i = 0; i < outboundWaiting.Count; i++)
            {
                var t = outboundWaiting[i];
                int best = -1, bestD = int.MaxValue;
                for (int li = 0; li < Lifts.Count; li++)
                {
                    var site = Lifts[li].site;
                    if (site.aisle != Layout.Block[t.pickCell] || PD[t.level][li] != PDState.Free) continue;
                    int d = Math.Abs(Layout.GX(site.pdCell) - Layout.GX(t.pickCell));
                    if (d < bestD) { bestD = d; best = li; }
                }
                if (best < 0) continue;
                PD[t.level][best] = PDState.ReservedOut;
                t.lift = best; t.dropCell = Lifts[best].site.pdCell; t.phase = TaskPhase.Released;
                Tasks.Add(t); outboundWaiting.RemoveAt(i--);
            }
            // 2) idle lifts pick a job
            foreach (var lift in Lifts) if (lift.phase == 0) StartLiftJob(lift);
            // 3) assign released tasks to idle shuttles on the same level (nearest first)
            foreach (var t in Tasks)
            {
                if (t.phase != TaskPhase.Released) continue;
                BfsFrom(t.level, t.pickCell, false);
                Shuttle best = null; int bestD = int.MaxValue;
                foreach (var s in Shuttles)
                    if (s.level == t.level && s.task == null && bfsBuf[s.cell] < bestD && bfsBuf[s.cell] < (1 << 20)) { bestD = bfsBuf[s.cell]; best = s; }
                if (best == null) continue;
                t.phase = TaskPhase.ToPick; t.shuttle = best.id; best.task = t; best.lastDist = -1;
            }
        }

        void StartLiftJob(Lift lift)
        {
            int li = Lifts.IndexOf(lift);
            // oldest outbound pallet waiting on this lift's P&D
            int outLevel = -1; double outT = double.MaxValue;
            for (int l = 0; l < Cfg.levels; l++)
                if (PD[l][li] == PDState.OccupiedOut && PDReady[l][li] < outT) { outT = PDReady[l][li]; outLevel = l; }
            double inT = InboundQueue.Count > 0 ? InboundQueue.Peek().t : double.MaxValue;
            if (outLevel >= 0 && outT <= inT)
            {
                lift.outboundJob = true; lift.targetLevel = outLevel;
                BeginLiftPhase(lift, 1, lift.level, outLevel);
                return;
            }
            if (InboundQueue.Count == 0) { if (outLevel >= 0) { lift.outboundJob = true; lift.targetLevel = outLevel; BeginLiftPhase(lift, 1, lift.level, outLevel); } return; }
            // inbound: choose level with free P&D and a free cell in this aisle, least open tasks
            int bestL = -1, bestLoad = int.MaxValue, bestCell = -1;
            for (int l = 0; l < Cfg.levels; l++)
            {
                if (PD[l][li] != PDState.Free) continue;
                int cell = NearestFreeCell(l, lift.site);
                if (cell < 0) continue;
                int load = 0; foreach (var t in Tasks) if (t.level == l) load++;
                if (load < bestLoad || load == bestLoad && rng.Next(2) == 0) { bestLoad = load; bestL = l; bestCell = cell; }
            }
            if (bestL < 0) { if (outLevel >= 0) { lift.outboundJob = true; lift.targetLevel = outLevel; BeginLiftPhase(lift, 1, lift.level, outLevel); } return; }
            var item = InboundQueue.Dequeue();
            PD[bestL][li] = PDState.ReservedIn; Reserved[bestL][bestCell] = true;
            lift.task = new ShuttleTask { id = nextTask++, kind = TaskKind.Inbound, level = bestL, pickCell = lift.site.pdCell,
                dropCell = bestCell, lift = li, pallet = item.pallet, phase = TaskPhase.Waiting, created = item.t };
            lift.outboundJob = false; lift.targetLevel = bestL; lift.pallet = item.pallet;
            BeginLiftPhase(lift, 1, lift.level, 0);
        }

        bool LaneBusy(int level, int[] lane)
        {
            foreach (var c in lane) if (Reserved[level][c]) return true;
            return false;
        }

        /// <summary>Inbound target: in each free lane of the aisle, the deepest empty cell reachable from the
        /// aisle without passing under a pallet; the lane closest to the P&amp;D wins.</summary>
        int NearestFreeCell(int level, LiftSite site)
        {
            BfsFrom(level, site.pdCell, false);
            int best = -1, bestD = int.MaxValue;
            foreach (var lane in Layout.LaneCells)
            {
                if (Layout.Block[lane[0]] != site.aisle || LaneBusy(level, lane)) continue;
                int deepest = -1;
                foreach (var c in lane) { if (PalletAt[level][c] >= 0) break; deepest = c; }
                if (deepest < 0) continue;
                int d = bfsBuf[lane[0]];
                if (d < bestD) { bestD = d; best = deepest; }
            }
            return best;
        }

        void BeginLiftPhase(Lift lift, int phase, float from, float to)
        {
            lift.phase = phase; lift.phaseStart = Time; lift.fromLevel = from; lift.toLevel = to;
            double dur = (phase == 1 || phase == 3)
                ? RackConfig.TravelTime(Math.Abs(to - from) * Cfg.levelHeight, Cfg.liftSpeed, Cfg.liftAccel)
                : Cfg.LiftTransferTime;
            lift.phaseEnd = Time + dur;
        }

        void AdvanceLift(Lift lift)
        {
            int li = Lifts.IndexOf(lift);
            lift.level = lift.toLevel;
            switch (lift.phase)
            {
                case 1: BeginLiftPhase(lift, 2, lift.level, lift.level); break; // arrive -> load
                case 2:
                    if (lift.outboundJob)
                    {
                        int l = lift.targetLevel;
                        lift.pallet = PalletAt[l][lift.site.pdCell]; PalletAt[l][lift.site.pdCell] = -1;
                        PD[l][li] = PDState.Free;
                        BeginLiftPhase(lift, 3, lift.level, 0);
                    }
                    else BeginLiftPhase(lift, 3, lift.level, lift.targetLevel);
                    break;
                case 3: BeginLiftPhase(lift, 4, lift.level, lift.level); break; // arrive -> unload
                case 4:
                    if (lift.outboundJob)
                    {
                        Stats.outboundShipped++;
                        if (outboundCreated.TryGetValue(lift.pallet, out double c)) { Stats.outboundCycleSum += Time - c; outboundCreated.Remove(lift.pallet); }
                    }
                    else
                    {
                        int l = lift.targetLevel;
                        int occ = Occ[l][lift.site.pdCell];
                        if (occ >= 0 && Shuttles[occ].loaded) { lift.phaseEnd = Time + 1.0; return; } // wait until the P&D is clear
                        PalletAt[l][lift.site.pdCell] = lift.pallet; PD[l][li] = PDState.OccupiedIn;
                        lift.task.phase = TaskPhase.Released; Tasks.Add(lift.task); lift.task = null;
                    }
                    lift.pallet = -1; lift.phase = 0;
                    StartLiftJob(lift);
                    break;
            }
        }
        readonly Dictionary<int, double> outboundCreated = new Dictionary<int, double>();

        // ------------------------------------------------------------------ shuttle actions
        /// <summary>A loaded shuttle cannot pass under stored pallets and only enters a P&amp;D cell that is its drop target
        /// (callers allow the target explicitly), so a lift never unloads onto a loaded shuttle.</summary>
        bool Passable(int level, int cell, bool loaded)
        {
            if (!loaded) return true;
            return PalletAt[level][cell] < 0 && Layout.Type[cell] != CellType.PD;
        }
        bool PassableFor(Shuttle s, int cell)
        {
            if (s.loaded && cell == TargetCell(s)) return PalletAt[s.level][cell] < 0;
            return Passable(s.level, cell, s.loaded);
        }

        bool CanPick(Shuttle s)
        {
            return !s.loaded && s.task != null && s.task.phase == TaskPhase.ToPick && s.cell == s.task.pickCell && PalletAt[s.level][s.cell] >= 0;
        }
        bool CanDrop(Shuttle s)
        {
            return s.loaded && s.task != null && s.task.phase == TaskPhase.ToDrop && s.cell == s.task.dropCell && PalletAt[s.level][s.cell] < 0;
        }

        /// <summary>Valid-action mask: mask[a] = true means action a is allowed.</summary>
        public void GetActionMask(int id, bool[] mask)
        {
            var s = Shuttles[id];
            mask[0] = true;
            for (int d = 0; d < 4; d++)
            {
                int n = Layout.Neighbor(s.cell, d);
                bool ok = n >= 0 && PassableFor(s, n);
                if (ok && Cfg.maskCollisions && Occ[s.level][n] >= 0) ok = false;
                mask[d + 1] = ok;
            }
            mask[5] = CanPick(s) || CanDrop(s);
        }

        public void ApplyAction(int id, int a)
        {
            var s = Shuttles[id];
            if (!s.awaiting) return;
            s.awaiting = false;
            // time penalty + progress shaping for the interval since the last decision
            if (s.task != null) s.reward += rTimePerSec * (float)(Time - s.lastDecision);
            s.lastDecision = Time;
            s.actStart = Time;
            if (a >= 1 && a <= 4)
            {
                int d = a - 1, n = Layout.Neighbor(s.cell, d);
                bool ok = n >= 0 && PassableFor(s, n);
                if (ok && Occ[s.level][n] >= 0) { ok = false; Stats.collisionAttempts++; }
                if (ok)
                {
                    int newAxis = Dir.IsX(d) ? 0 : 1;
                    double sw = newAxis != s.axis ? Cfg.wheelSwitchTime : 0;
                    float pitch = Dir.IsX(d) ? Cfg.pitchAisle : Cfg.pitchLane;
                    if (Dir.IsX(d) && (Layout.Type[s.cell] == CellType.PD || Layout.Type[n] == CellType.PD))
                        pitch = (Cfg.pitchAisle + Cfg.carriageLength) / 2f;
                    double mv = RackConfig.TravelTime(pitch, s.loaded ? Cfg.shuttleSpeedLoaded : Cfg.shuttleSpeedEmpty, Cfg.shuttleAccel);
                    s.axis = newAxis; s.switchEnd = Time + sw;
                    s.fromCell = s.cell; s.toCell = n; Occ[s.level][n] = s.id;
                    s.act = ShuttleAct.Move; s.actEnd = Time + sw + mv;
                    return;
                }
                Invalid(s); return;
            }
            if (a == 5)
            {
                if (CanPick(s)) { s.act = ShuttleAct.JackUp; s.actEnd = Time + Cfg.jackTime; return; }
                if (CanDrop(s)) { s.act = ShuttleAct.JackDown; s.actEnd = Time + Cfg.jackTime; return; }
                Invalid(s); return;
            }
            s.act = ShuttleAct.Wait; s.actEnd = Time + Cfg.waitTime;
        }

        void Invalid(Shuttle s)
        {
            Stats.invalidActions++; s.reward += rInvalid;
            s.act = ShuttleAct.Wait; s.actEnd = Time + Cfg.waitTime;
        }

        void CompleteShuttle(Shuttle s)
        {
            var act = s.act;
            if (act != ShuttleAct.Wait) { s.busyTime += s.actEnd - s.actStart; Stats.shuttleBusySum += s.actEnd - s.actStart; }
            if (act == ShuttleAct.JackUp || act == ShuttleAct.JackDown) lastProgress = Time;
            s.act = ShuttleAct.Idle; s.awaiting = true;
            if (act == ShuttleAct.Move)
            {
                Occ[s.level][s.fromCell] = -1; s.cell = s.toCell; s.fromCell = s.cell; Stats.moves++;
            }
            else if (act == ShuttleAct.JackUp)
            {
                var t = s.task;
                s.carried = PalletAt[s.level][s.cell]; PalletAt[s.level][s.cell] = -1; s.loaded = true;
                if (t.kind == TaskKind.Inbound) PD[s.level][t.lift] = PDState.Free;
                else Reserved[s.level][s.cell] = false;
                t.phase = TaskPhase.ToDrop; s.lastDist = -1; s.reward += rPick;
            }
            else if (act == ShuttleAct.JackDown)
            {
                var t = s.task;
                PalletAt[s.level][s.cell] = s.carried; s.carried = -1; s.loaded = false;
                if (t.kind == TaskKind.Inbound)
                {
                    Reserved[s.level][s.cell] = false; Stats.inboundStored++; Stats.inboundCycleSum += Time - t.created;
                }
                else
                {
                    PD[s.level][t.lift] = PDState.OccupiedOut; PDReady[s.level][t.lift] = Time;
                    outboundCreated[PalletAt[s.level][s.cell]] = t.created;
                }
                t.phase = TaskPhase.Done; Tasks.Remove(t); s.task = null; s.lastDist = -1;
                s.reward += rDrop; GroupReward += rGroupTask;
            }
            // shaping: progress toward the current target
            if (s.task != null)
            {
                int d = DistanceToTarget(s);
                if (s.lastDist >= 0 && d < 10000 && s.lastDist < 10000) s.reward += rShaping * (s.lastDist - d);
                s.lastDist = d;
            }
        }

        // ------------------------------------------------------------------ paths
        /// <summary>BFS distance map from 'target' (reverse search). loaded: cells with pallets can't be traversed.</summary>
        public int[] BfsFrom(int level, int target, bool loaded)
        {
            const int INF = 1 << 20;
            for (int i = 0; i < bfsBuf.Length; i++) bfsBuf[i] = INF;
            int h = 0, tl = 0; bfsBuf[target] = 0; bfsQueue[tl++] = target;
            while (h < tl)
            {
                int u = bfsQueue[h++];
                if (u != target && !Passable(level, u, loaded)) continue;
                for (int d = 0; d < 4; d++)
                {
                    int v = Layout.Neighbor(u, d);
                    if (v >= 0 && bfsBuf[v] == INF) { bfsBuf[v] = bfsBuf[u] + 1; bfsQueue[tl++] = v; }
                }
            }
            return bfsBuf;
        }

        public int TargetCell(Shuttle s)
        {
            if (s.task == null) return -1;
            return s.task.phase == TaskPhase.ToPick ? s.task.pickCell : s.task.dropCell;
        }

        int DistanceToTarget(Shuttle s)
        {
            int t = TargetCell(s); if (t < 0) return -1;
            return BfsFrom(s.level, t, s.loaded)[s.cell];
        }

        /// <summary>Direction (0..3) of the shortest path step toward the target ignoring other shuttles, or -1.</summary>
        public int HintDirection(Shuttle s)
        {
            int t = TargetCell(s); if (t < 0 || t == s.cell) return -1;
            var dist = BfsFrom(s.level, t, s.loaded);
            int best = -1, bestD = int.MaxValue, here = dist[s.cell];
            for (int d = 0; d < 4; d++)
            {
                int n = Layout.Neighbor(s.cell, d);
                if (n < 0 || !PassableFor(s, n)) continue;
                int nd = dist[n];
                if (nd >= here) continue;
                bool sameAxis = Dir.IsX(d) == (s.axis == 0);
                if (nd < bestD || nd == bestD && sameAxis) { bestD = nd; best = d; }
            }
            return best;
        }

        // ------------------------------------------------------------------ observations
        public void BuildObservation(int id, float[] o)
        {
            Array.Clear(o, 0, ObsSize);
            var s = Shuttles[id];
            int target = TargetCell(s);
            o[0] = s.loaded ? 1 : 0;
            o[1] = s.task != null ? 1 : 0;
            o[2] = s.task != null && s.task.phase == TaskPhase.ToPick ? 1 : 0;
            o[3] = s.task != null && s.task.phase == TaskPhase.ToDrop ? 1 : 0;
            o[4] = s.axis;
            o[5] = Layout.Type[s.cell] == CellType.Junction ? 1 : 0;
            o[6] = CanPick(s) || CanDrop(s) ? 1 : 0;
            int hint = -1;
            if (target >= 0)
            {
                o[7] = Clamp((Layout.GX(target) - Layout.GX(s.cell)) / 10f);
                o[8] = Clamp((Layout.GY(target) - Layout.GY(s.cell)) / 10f);
                int d = BfsFrom(s.level, target, s.loaded)[s.cell];
                o[9] = Math.Min(1f, d / 30f);
                hint = HintDirection(s);
            }
            if (hint >= 0) o[10 + hint] = 1;
            int k = StateSize;
            int cx = Layout.GX(s.cell), cy = Layout.GY(s.cell);
            for (int dy = -WinR; dy <= WinR; dy++)
                for (int dx = -WinR; dx <= WinR; dx++, k += WinCh)
                {
                    int gx = cx + dx, gy = cy + dy;
                    if (gx < 0 || gy < 0 || gx >= Layout.W || gy >= Layout.H) continue;
                    int c = Layout.Index(gx, gy);
                    if (Layout.Type[c] == CellType.None) continue;
                    o[k] = 1;
                    int occ = Occ[s.level][c];
                    o[k + 1] = PassableFor(s, c) && (occ < 0 || occ == s.id) ? 1 : 0;
                    if (occ >= 0 && occ != s.id)
                    {
                        o[k + 2] = 1;
                        var other = Shuttles[occ];
                        o[k + 3] = other.loaded ? 1f : other.task != null ? 0.5f : 0f;
                    }
                    o[k + 4] = PalletAt[s.level][c] >= 0 ? 1 : 0;
                    o[k + 5] = c == target ? 1 : 0;
                }
        }

        static float Clamp(float v) { return v < -1 ? -1 : v > 1 ? 1 : v; }

        public float TakeReward(int id) { var s = Shuttles[id]; float r = s.reward; s.reward = 0; return r; }
        public float TakeGroupReward() { float r = GroupReward; GroupReward = 0; return r; }

        // ------------------------------------------------------------------ rule-based baseline
        /// <summary>Rule-based policy: follow the shortest path, wait when blocked, idle shuttles leave the aisle row.</summary>
        public int HeuristicAction(int id)
        {
            var s = Shuttles[id];
            var mask = new bool[ActionCount];
            GetActionMask(id, mask);
            if (mask[5]) return 5;
            if (s.yieldWait > 0) { s.yieldWait--; return 0; } // let the other shuttle pass after yielding
            if (s.task != null)
            {
                int h = HintDirection(s);
                // lane lock: do not enter a lane while another busy shuttle is inside it; clear the junction instead
                if (h >= 0 && !Dir.IsX(h) && Layout.Type[s.cell] == CellType.Junction)
                {
                    int entry = Layout.Neighbor(s.cell, h);
                    if (entry >= 0 && LaneHasBusyShuttle(s, Layout.LaneOf[entry]))
                    {
                        s.blockedCount++;
                        if (mask[1] && !IsSomeoneTarget(s.level, Layout.Neighbor(s.cell, Dir.PX))) return 1;
                        if (mask[2] && !IsSomeoneTarget(s.level, Layout.Neighbor(s.cell, Dir.NX))) return 2;
                        return 0;
                    }
                }
                if (h >= 0 && mask[h + 1]) { s.blockedCount = 0; return h + 1; }
                s.blockedCount++;
                int n = h >= 0 ? Layout.Neighbor(s.cell, h) : -1;
                int oc = n >= 0 ? Occ[s.level][n] : -1;
                if (oc >= 0 && Shuttles[oc].task != null)
                {
                    var o = Shuttles[oc];
                    int oh = HintDirection(o);
                    bool headOn = oh >= 0 && Layout.Neighbor(o.cell, oh) == s.cell;
                    if (headOn && ShouldYield(s, h, o))
                    {
                        s.yieldWait = 5;
                        // step aside: from the aisle into a free lane cell, otherwise back off
                        if (Layout.IsAisleRow(s.cell))
                        {
                            if (mask[3] && !IsSomeoneTarget(s.level, Layout.Neighbor(s.cell, Dir.PY))) return 3;
                            if (mask[4] && !IsSomeoneTarget(s.level, Layout.Neighbor(s.cell, Dir.NY))) return 4;
                        }
                        int back = (h ^ 1) + 1; // opposite direction
                        if (mask[back]) return back;
                        for (int d = 0; d < 4; d++) if (mask[d + 1] && d != h) return d + 1;
                    }
                }
                if (s.blockedCount > 5 && rng.NextDouble() < 0.3)
                {
                    int r = RandomMove(mask); if (r > 0) return r;
                }
                return 0;
            }
            // idle: get off the aisle row / out of someone's way
            bool inWay = Layout.IsAisleRow(s.cell) || Layout.Type[s.cell] == CellType.PD;
            foreach (var o in Shuttles)
            {
                if (o.level != s.level || o.task == null || o == s) continue;
                if (TargetCell(o) == s.cell) inWay = true;
                int h = HintDirection(o);
                if (h >= 0 && Layout.Neighbor(o.cell, h) == s.cell) inWay = true;
            }
            if (!inWay) return 0;
            if (Layout.IsAisleRow(s.cell))
            {
                if (mask[3] && !IsSomeoneTarget(s.level, Layout.Neighbor(s.cell, Dir.PY))) return 3;
                if (mask[4] && !IsSomeoneTarget(s.level, Layout.Neighbor(s.cell, Dir.NY))) return 4;
            }
            int rm = RandomMove(mask);
            return rm > 0 ? rm : 0;
        }

        /// <summary>Head-on conflict rule: in a lane the shuttle heading deeper backs off (lanes are dead ends);
        /// in the aisle a loaded shuttle has priority over an empty one, then the lower id.</summary>
        bool ShouldYield(Shuttle s, int h, Shuttle o)
        {
            if (!Dir.IsX(h))
            {
                int ya = Layout.AisleRow(Layout.Block[s.cell]);
                bool deeper = Math.Abs(Layout.GY(Layout.Neighbor(s.cell, h)) - ya) > Math.Abs(Layout.GY(s.cell) - ya);
                return deeper;
            }
            if (s.loaded != o.loaded) return !s.loaded;
            return s.id > o.id;
        }

        bool LaneHasBusyShuttle(Shuttle s, int lane)
        {
            if (lane < 0) return false;
            foreach (var o in Shuttles)
                if (o != s && o.level == s.level && o.task != null && Layout.LaneOf[o.cell] == lane) return true;
            return false;
        }

        bool IsSomeoneTarget(int level, int cell)
        {
            foreach (var o in Shuttles) if (o.level == level && TargetCell(o) == cell) return true;
            return false;
        }

        int RandomMove(bool[] mask)
        {
            int cnt = 0; for (int i = 1; i <= 4; i++) if (mask[i]) cnt++;
            if (cnt == 0) return 0;
            int pick = rng.Next(cnt);
            for (int i = 1; i <= 4; i++) if (mask[i] && pick-- == 0) return i;
            return 0;
        }

        // ------------------------------------------------------------------ helpers for views
        /// <summary>0..1 progress of the current move (after wheel switching), for interpolation.</summary>
        public float MoveProgress(Shuttle s, double now)
        {
            if (s.act != ShuttleAct.Move) return 0f;
            if (now <= s.switchEnd) return 0f;
            double span = s.actEnd - s.switchEnd;
            if (span <= 0) return 1f;
            double p = (now - s.switchEnd) / span;
            p = Math.Max(0, Math.Min(1, p));
            return (float)(p * p * (3 - 2 * p)); // ease in/out
        }

        public float JackHeight(Shuttle s, double now)
        {
            if (s.act == ShuttleAct.JackUp) return (float)Math.Min(1, (now - s.actStart) / Cfg.jackTime);
            if (s.act == ShuttleAct.JackDown) return 1f - (float)Math.Min(1, (now - s.actStart) / Cfg.jackTime);
            return s.loaded ? 1f : 0f;
        }

        public float LiftLevel(Lift l, double now)
        {
            if (l.phase != 1 && l.phase != 3) return l.level;
            double span = l.phaseEnd - l.phaseStart;
            double p = span <= 0 ? 1 : Math.Max(0, Math.Min(1, (now - l.phaseStart) / span));
            p = p * p * (3 - 2 * p);
            return (float)(l.fromLevel + (l.toLevel - l.fromLevel) * p);
        }

        public bool LiftHasPallet(Lift l)
        {
            if (l.pallet < 0) return false;
            if (l.outboundJob) return l.phase >= 3;
            return l.phase >= 3 || l.phase == 2;
        }

        public float Utilization()
        {
            if (Time <= 0 || Shuttles.Count == 0) return 0;
            return (float)(Stats.shuttleBusySum / (Time * Shuttles.Count));
        }
    }
}
