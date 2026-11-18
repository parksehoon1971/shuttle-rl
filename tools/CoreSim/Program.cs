using System;
using System.Linq;
using ShuttleRL.Core;

// Usage: dotnet run -- [episodes]
// Runs the rule-based baseline and a random (masked) policy on several rack sizes,
// checks simulation invariants, and prints KPIs.
class Program
{
    static int Main(string[] args)
    {
        int episodes = args.Length > 0 ? int.Parse(args[0]) : 3;
        var configs = new[]
        {
            new RackConfig { levels = 2, aisles = 1, lanesPerAisle = 3, cellsPerLane = 4, shuttlesPerLevel = 2, inboundPerHour = 40, outboundPerHour = 40 },
            new RackConfig { levels = 4, aisles = 1, lanesPerAisle = 6, cellsPerLane = 6, shuttlesPerLevel = 3, inboundPerHour = 120, outboundPerHour = 120 },
            new RackConfig { levels = 6, aisles = 2, lanesPerAisle = 10, cellsPerLane = 10, shuttlesPerLevel = 4, inboundPerHour = 300, outboundPerHour = 300 },
        };
        bool ok = true;
        foreach (var cfg in configs)
        {
            Console.WriteLine($"\n== levels {cfg.levels}, aisles {cfg.aisles}, lanes {cfg.lanesPerAisle}, cells {cfg.cellsPerLane}, shuttles/level {cfg.shuttlesPerLevel}, in/out {cfg.inboundPerHour}/{cfg.outboundPerHour} per h -> lifts {cfg.ComputeLiftCount()}");
            foreach (var policy in new[] { "rule", "random" })
            {
                for (int ep = 0; ep < episodes; ep++)
                {
                    var c = cfg.Clone(); c.seed = ep;
                    var w = new SimWorld(c);
                    int pallets0 = CountPallets(w);
                    var mask = new bool[SimWorld.ActionCount];
                    var rng = new Random(ep);
                    var obs = new float[SimWorld.ObsSize];
                    long decisions = 0; double reward = 0;
                    while (!w.Done)
                    {
                        if (w.Advance(10.0))
                        {
                            foreach (var s in w.Shuttles.Where(s => s.awaiting).ToList())
                            {
                                w.BuildObservation(s.id, obs);
                                if (obs.Any(float.IsNaN)) { Console.WriteLine("NaN in observation"); ok = false; }
                                int a;
                                if (policy == "rule") a = w.HeuristicAction(s.id);
                                else
                                {
                                    w.GetActionMask(s.id, mask);
                                    var valid = Enumerable.Range(0, mask.Length).Where(i => mask[i]).ToArray();
                                    a = valid[rng.Next(valid.Length)];
                                }
                                w.ApplyAction(s.id, a); decisions++;
                                reward += w.TakeReward(s.id);
                            }
                        }
                        reward += w.TakeGroupReward();
                        if (w.Deadlocked && Environment.GetEnvironmentVariable("DEBUG_DEADLOCK") == "1")
                        {
                            foreach (var s in w.Shuttles)
                            {
                                int t = w.TargetCell(s);
                                w.GetActionMask(s.id, mask);
                                Console.WriteLine($"    S{s.id} L{s.level} at ({w.Layout.X(s.cell)},{w.Layout.GY(s.cell)}) {w.Layout.Type[s.cell]} loaded={s.loaded} act={s.act} task={(s.task==null?"-":s.task.kind+"/"+s.task.phase)} target={(t<0?"-":"("+w.Layout.X(t)+","+w.Layout.GY(t)+")")} hint={w.HintDirection(s)} mask={string.Join("",mask.Select(m=>m?1:0))}");
                            }
                        }
                        if (!CheckInvariants(w, pallets0, out string err)) { Console.WriteLine("INVARIANT: " + err); ok = false; break; }
                    }
                    var st = w.Stats;
                    double hours = w.Time / 3600.0;
                    Console.WriteLine($"  {policy,-6} ep{ep}: t={w.Time,6:F0}s in {st.inboundStored,4}/{st.inboundArrived,-4} out {st.outboundShipped,4}/{st.outboundOrdered,-4} " +
                        $"thru {(st.inboundStored + st.outboundShipped) / Math.Max(1e-6, hours),6:F1}/h  inCyc {(st.inboundStored > 0 ? st.inboundCycleSum / st.inboundStored : 0),6:F1}s " +
                        $"outCyc {(st.outboundShipped > 0 ? st.outboundCycleSum / st.outboundShipped : 0),6:F1}s util {w.Utilization():P0} deadlock {st.deadlocks} " +
                        $"invalid {st.invalidActions} maxQ {st.maxInboundQueue} dec {decisions} R {reward:F1}");
                }
            }
        }
        Console.WriteLine(ok ? "\nALL CHECKS PASSED" : "\nCHECKS FAILED");
        return ok ? 0 : 1;
    }

    static int CountPallets(SimWorld w)
    {
        int n = 0;
        foreach (var lv in w.PalletAt) foreach (var p in lv) if (p >= 0) n++;
        return n;
    }

    static bool CheckInvariants(SimWorld w, int pallets0, out string err)
    {
        err = null;
        for (int l = 0; l < w.Cfg.levels; l++)
        {
            var seen = new bool[w.Layout.CellCount];
            foreach (var s in w.Shuttles.Where(s => s.level == l))
            {
                if (seen[s.cell]) { err = $"two shuttles on cell {s.cell}"; return false; }
                seen[s.cell] = true;
                if (w.Occ[l][s.cell] != s.id) { err = $"occupancy mismatch shuttle {s.id}"; return false; }
                if (s.loaded && w.PalletAt[l][s.cell] >= 0) { err = $"loaded shuttle {s.id} under pallet"; return false; }
                if (w.Layout.Type[s.cell] == CellType.None) { err = "shuttle off rail"; return false; }
            }
        }
        // pallet conservation: initial + arrived = in rack + carried + lifts + inbound queue + shipped
        int inRack = CountPallets(w);
        int carried = w.Shuttles.Count(s => s.loaded);
        int onLift = w.Lifts.Count(l => l.pallet >= 0);
        int total = inRack + carried + onLift + w.InboundQueue.Count + w.Stats.outboundShipped;
        if (total != pallets0 + w.Stats.inboundArrived) { err = $"pallet conservation {total} != {pallets0 + w.Stats.inboundArrived}"; return false; }
        return true;
    }
}
