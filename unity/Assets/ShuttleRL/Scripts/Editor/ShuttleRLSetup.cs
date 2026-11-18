using ShuttleRL.Core;
using Unity.MLAgents.Actuators;
using Unity.MLAgents.Policies;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace ShuttleRL.Editor
{
    /// <summary>Menu items that build ready-to-run scenes, so nothing has to be wired by hand.</summary>
    public static class ShuttleRLSetup
    {
        const string ModelDir = "Assets/ShuttleRL/Models/";

        [MenuItem("ShuttleRL/Create Demo Scene (1 rack, 3D)")]
        public static void CreateDemo()
        {
            RemoveExisting();
            var template = CreateAgentTemplate(BehaviorType.HeuristicOnly);
            var env = CreateRack("ShuttleRack", Vector3.zero, template, true);
            env.config = new RackConfig { levels = 3, aisles = 1, lanesPerAisle = 6, cellsPerLane = 6, shuttlesPerLevel = 2, inboundPerHour = 90, outboundPerHour = 90 };
            env.useEnvironmentParameters = false;

            var cam = Camera.main;
            if (cam == null) { cam = new GameObject("Main Camera").AddComponent<Camera>(); cam.tag = "MainCamera"; }
            cam.transform.position = new Vector3(14f, 11f, -14f);
            cam.transform.LookAt(new Vector3(3.5f, 1.5f, 0f));
            if (!HasRoot<Light>())
            {
                var light = new GameObject("Sun").AddComponent<Light>();
                light.type = LightType.Directional; light.transform.rotation = Quaternion.Euler(50, -30, 0);
            }
            Selection.activeObject = env.gameObject;
            Debug.Log("ShuttleRL demo created. Press Play. The agent template uses Heuristic Only (rule-based baseline); " +
                      "set it to Inference Only with a trained model, or Default to train.");
        }

        [MenuItem("ShuttleRL/Create Training Scene (8 racks, headless)")]
        public static void CreateTraining()
        {
            RemoveExisting();
            var template = CreateAgentTemplate(BehaviorType.Default);
            for (int i = 0; i < 8; i++)
            {
                var env = CreateRack($"ShuttleRack_{i}", new Vector3(i * 60f, 0, 0), template, false);
                env.trainingMode = true;
                env.baseSeed = i * 100000;
            }
            Debug.Log("ShuttleRL training scene created. Build it (or press Play) and run mlagents-learn with config/shuttle_poca.yaml.");
        }

        /// <summary>Running a menu item again replaces the racks and agent templates instead of adding more.</summary>
        static void RemoveExisting()
        {
            foreach (var go in SceneManager.GetActiveScene().GetRootGameObjects())
                if (go.GetComponent<RackEnvironment>() != null || go.GetComponent<ShuttleAgent>() != null)
                    Undo.DestroyObjectImmediate(go);
        }

        static bool HasRoot<T>() where T : Component
        {
            foreach (var go in SceneManager.GetActiveScene().GetRootGameObjects())
                if (go.GetComponentInChildren<T>(true) != null) return true;
            return false;
        }

        static ShuttleAgent CreateAgentTemplate(BehaviorType type)
        {
            var go = new GameObject("ShuttleAgentTemplate");
            var bp = go.AddComponent<BehaviorParameters>();
            bp.BehaviorName = "Shuttle";
            bp.BrainParameters.VectorObservationSize = SimWorld.ObsSize;
            bp.BrainParameters.NumStackedVectorObservations = 1;
            bp.BrainParameters.ActionSpec = ActionSpec.MakeDiscrete(SimWorld.ActionCount);
            bp.BehaviorType = type;
            var agent = go.AddComponent<ShuttleAgent>();
            go.SetActive(false);
            Undo.RegisterCreatedObjectUndo(go, "Create Shuttle Agent Template");
            return agent;
        }

        static RackEnvironment CreateRack(string name, Vector3 pos, ShuttleAgent template, bool visuals)
        {
            var go = new GameObject(name);
            go.transform.position = pos;
            var env = go.AddComponent<RackEnvironment>();
            env.agentTemplate = template;
            env.buildVisuals = visuals;
            if (visuals)
            {
                env.models = new RackModels
                {
                    laneRail = Load("LaneRail"), aisleRail = Load("AisleRail"), junction = Load("Junction"),
                    pdStation = Load("PDStation"), liftCarriage = Load("LiftCarriage"), liftMastSegment = Load("LiftMastSegment"),
                    conveyor = Load("Conveyor"), upright = Load("Upright"), beam = Load("Beam"),
                    pallet = Load("Pallet"), palletLoad = Load("PalletLoad"), shuttle = Load("Shuttle"),
                };
            }
            Undo.RegisterCreatedObjectUndo(go, "Create Shuttle Rack");
            return env;
        }

        static GameObject Load(string model)
        {
            var g = AssetDatabase.LoadAssetAtPath<GameObject>(ModelDir + model + ".fbx");
            if (g == null) Debug.LogWarning($"ShuttleRL: model not found: {ModelDir}{model}.fbx");
            return g;
        }
    }
}
