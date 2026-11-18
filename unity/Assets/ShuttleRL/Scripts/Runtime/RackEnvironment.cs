using System.Collections.Generic;
using ShuttleRL.Core;
using Unity.MLAgents;
using Unity.MLAgents.Actuators;
using Unity.MLAgents.Policies;
using UnityEngine;

namespace ShuttleRL
{
    /// <summary>
    /// One rack instance: owns the simulation, the shuttle agents (one SimpleMultiAgentGroup) and,
    /// optionally, the 3D visuals. Put several instances in a scene to train faster.
    ///
    /// Stepping: the simulation is event driven. Each FixedUpdate it advances until a shuttle needs an
    /// action, requests decisions for those shuttles, and does not advance again until all of them have
    /// received their action. Simulated time therefore never depends on the frame rate.
    /// </summary>
    public class RackEnvironment : MonoBehaviour
    {
        [Header("Rack (overridden by curriculum parameters while training)")]
        public RackConfig config = new RackConfig();
        [Tooltip("Read levels/lanes/cells/... from ML-Agents environment parameters at every episode start.")]
        public bool useEnvironmentParameters = true;
        [Tooltip("Different seed per episode (seed = baseSeed + episode).")]
        public int baseSeed = 0;

        [Header("Agents")]
        [Tooltip("Inactive GameObject with BehaviorParameters + ShuttleAgent; cloned once per shuttle.")]
        public ShuttleAgent agentTemplate;

        [Header("Time")]
        [Tooltip("Visual mode: simulated seconds per real second. Training mode jumps straight to the next event.")]
        public float visualTimeScale = 4f;
        public bool trainingMode = false;
        [Tooltip("Upper bound of simulated seconds per FixedUpdate in training mode.")]
        public float trainingMaxJump = 30f;

        [Header("Visuals (optional, leave empty for headless training)")]
        public bool buildVisuals = true;
        public RackModels models;

        public SimWorld World { get; private set; }
        public RackGeometry Geometry { get; private set; }
        public int Episode { get; private set; }

        readonly List<ShuttleAgent> agents = new List<ShuttleAgent>();
        SimpleMultiAgentGroup group;
        int pending;
        bool[] awaitingDecision = new bool[0];
        RackVisuals visuals;
        string layoutKey = "";
        static int instanceCounter;   // distinct seeds per rack without GetInstanceID (obsolete in Unity 6.6)
        int instanceIndex;

        void Awake() { instanceIndex = instanceCounter++; }

        void Start()
        {
            group = new SimpleMultiAgentGroup();
            if (agentTemplate != null)
            {
                agentTemplate.gameObject.SetActive(false);
                // keep the template in sync with the code (observation size changes between versions)
                var bp = agentTemplate.GetComponent<BehaviorParameters>();
                if (bp != null)
                {
                    bp.BrainParameters.VectorObservationSize = SimWorld.ObsSize;
                    bp.BrainParameters.ActionSpec = ActionSpec.MakeDiscrete(SimWorld.ActionCount);
                }
            }
            if (Application.isBatchMode) { trainingMode = true; buildVisuals = false; }
            ResetEpisode();
        }

        // ------------------------------------------------------------------ episode
        void ResetEpisode()
        {
            var cfg = config.Clone();
            if (useEnvironmentParameters) ApplyEnvironmentParameters(cfg);
            cfg.seed = baseSeed + Episode * 7919 + instanceIndex * 104729;
            if (World == null) World = new SimWorld(cfg); else World.Reset(cfg);
            Geometry = new RackGeometry(World.Cfg, World.Layout);
            EnsureAgents(World.Shuttles.Count);
            pending = 0;
            awaitingDecision = new bool[World.Shuttles.Count];

            string key = $"{cfg.levels}/{cfg.aisles}/{cfg.lanesPerAisle}/{cfg.cellsPerLane}/{World.Layout.Lifts.Count}";
            if (buildVisuals && models != null)
            {
                if (visuals == null) visuals = new RackVisuals(transform, models);
                if (key != layoutKey) visuals.BuildStatic(World, Geometry);
                visuals.EnsureDynamic(World);
            }
            layoutKey = key;
        }

        void ApplyEnvironmentParameters(RackConfig c)
        {
            var p = Academy.Instance.EnvironmentParameters;
            c.levels = Mathf.RoundToInt(p.GetWithDefault("levels", c.levels));
            c.aisles = Mathf.RoundToInt(p.GetWithDefault("aisles", c.aisles));
            c.lanesPerAisle = Mathf.RoundToInt(p.GetWithDefault("lanes", c.lanesPerAisle));
            c.cellsPerLane = Mathf.RoundToInt(p.GetWithDefault("cells", c.cellsPerLane));
            c.shuttlesPerLevel = Mathf.RoundToInt(p.GetWithDefault("shuttles_per_level", c.shuttlesPerLevel));
            c.inboundPerHour = p.GetWithDefault("inbound_per_hour", c.inboundPerHour);
            c.outboundPerHour = p.GetWithDefault("outbound_per_hour", c.outboundPerHour);
            c.initialFill = p.GetWithDefault("initial_fill", c.initialFill);
            c.episodeSeconds = p.GetWithDefault("episode_seconds", c.episodeSeconds);
        }

        void EnsureAgents(int count)
        {
            if (agentTemplate == null) { Debug.LogError("RackEnvironment: agentTemplate is not set."); return; }
            foreach (var a in agents) group.UnregisterAgent(a);
            while (agents.Count < count)
            {
                var go = Instantiate(agentTemplate.gameObject, transform);
                go.name = $"ShuttleAgent_{agents.Count}";
                var ag = go.GetComponent<ShuttleAgent>();
                agents.Add(ag);
            }
            for (int i = 0; i < agents.Count; i++)
            {
                var a = agents[i];
                a.env = this; a.shuttleId = i;
                bool on = i < count;
                a.gameObject.SetActive(on);
                if (on) group.RegisterAgent(a);
            }
        }

        void EndEpisode(bool deadlock)
        {
            // flush individual rewards, then end for the whole group
            for (int i = 0; i < World.Shuttles.Count; i++) agents[i].AddReward(World.TakeReward(i));
            group.AddGroupReward(World.TakeGroupReward());
            RecordStats(deadlock);
            if (deadlock) group.EndGroupEpisode();
            else group.GroupEpisodeInterrupted(); // time limit, not a terminal state
            Episode++;
            ResetEpisode();
        }

        void RecordStats(bool deadlock)
        {
            var st = World.Stats; var rec = Academy.Instance.StatsRecorder;
            double hours = System.Math.Max(1e-6, World.Time / 3600.0);
            rec.Add("Rack/Throughput per hour", (float)((st.inboundStored + st.outboundShipped) / hours));
            rec.Add("Rack/Inbound cycle s", st.inboundStored > 0 ? (float)(st.inboundCycleSum / st.inboundStored) : 0f);
            rec.Add("Rack/Outbound cycle s", st.outboundShipped > 0 ? (float)(st.outboundCycleSum / st.outboundShipped) : 0f);
            rec.Add("Rack/Shuttle utilization", World.Utilization());
            rec.Add("Rack/Deadlock", deadlock ? 1f : 0f);
            rec.Add("Rack/Episode sim seconds", (float)World.Time);
            rec.Add("Rack/Max inbound queue", st.maxInboundQueue);
            rec.Add("Rack/Invalid actions", st.invalidActions);
        }

        // ------------------------------------------------------------------ stepping
        void FixedUpdate()
        {
            if (World == null || pending > 0) return;
            double dt = trainingMode ? trainingMaxJump : Time.fixedDeltaTime * visualTimeScale;
            bool need = World.Advance(dt);
            group.AddGroupReward(World.TakeGroupReward());
            if (World.Done) { EndEpisode(World.Deadlocked); return; }
            if (!need) return;
            for (int i = 0; i < World.Shuttles.Count; i++)
            {
                if (!World.Shuttles[i].awaiting || awaitingDecision[i]) continue;
                awaitingDecision[i] = true; pending++;
                agents[i].AddReward(World.TakeReward(i));
                agents[i].RequestDecision();
            }
        }

        /// <summary>Called by ShuttleAgent.OnActionReceived.</summary>
        public void OnAgentAction(int id, int action)
        {
            if (id >= awaitingDecision.Length || !awaitingDecision[id]) return;
            awaitingDecision[id] = false; pending--;
            World.ApplyAction(id, action);
        }

        void Update()
        {
            if (visuals != null && World != null) visuals.Sync(World, Geometry);
        }
    }
}
