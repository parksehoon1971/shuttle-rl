using ShuttleRL.Core;
using Unity.MLAgents;
using Unity.MLAgents.Actuators;
using Unity.MLAgents.Sensors;
using UnityEngine;

namespace ShuttleRL
{
    /// <summary>
    /// One 4-way shuttle as an ML-Agents agent. Decisions are requested by RackEnvironment whenever the
    /// shuttle finishes an action (event driven), so do NOT add a DecisionRequester component.
    ///
    /// Behavior Parameters (set by the ShuttleRL menu, or by hand):
    ///   Behavior Name  = Shuttle
    ///   Vector Observation Space Size = SimWorld.ObsSize (308), Stacked Vectors = 1
    ///   Actions: Discrete, 1 branch, size 6  (0 wait, 1 +X, 2 -X, 3 +Y, 4 -Y, 5 jack up/down)
    ///   Behavior Type: Default for training, Heuristic Only for the rule-based baseline,
    ///                  Inference Only with a trained .onnx model.
    /// </summary>
    public class ShuttleAgent : Agent
    {
        [HideInInspector] public RackEnvironment env;
        [HideInInspector] public int shuttleId;

        readonly float[] obs = new float[SimWorld.ObsSize];
        readonly bool[] mask = new bool[SimWorld.ActionCount];

        public override void Initialize()
        {
            MaxStep = 0; // episode length is controlled by RackEnvironment (simulated time)
        }

        public override void CollectObservations(VectorSensor sensor)
        {
            if (env == null || env.World == null) { foreach (var _ in obs) sensor.AddObservation(0f); return; }
            env.World.BuildObservation(shuttleId, obs);
            sensor.AddObservation(obs);
        }

        public override void WriteDiscreteActionMask(IDiscreteActionMask actionMask)
        {
            if (env == null || env.World == null) return;
            env.World.GetActionMask(shuttleId, mask);
            for (int a = 0; a < mask.Length; a++)
                if (!mask[a]) actionMask.SetActionEnabled(0, a, false);
        }

        public override void OnActionReceived(ActionBuffers actions)
        {
            if (env != null) env.OnAgentAction(shuttleId, actions.DiscreteActions[0]);
        }

        /// <summary>Rule-based baseline (shortest path, lane lock, head-on yielding).</summary>
        public override void Heuristic(in ActionBuffers actionsOut)
        {
            var d = actionsOut.DiscreteActions;
            d[0] = env != null && env.World != null ? env.World.HeuristicAction(shuttleId) : 0;
        }
    }
}
