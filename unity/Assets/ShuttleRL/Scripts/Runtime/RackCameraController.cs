using UnityEngine;

namespace ShuttleRL
{
    /// <summary>
    /// Orbit / pan / zoom camera for watching the rack in Play mode (legacy Input Manager).
    ///   Right mouse drag : rotate around the pivot      Middle mouse drag (or Alt + left) : pan
    ///   Mouse wheel      : zoom                         W A S D / arrow keys : move the pivot
    ///   Q / E            : move the pivot down / up     Shift : faster
    ///   F                : frame the whole rack         R : reset view        H : show / hide help
    /// Follow modes (set by the HUD):
    ///   Chase    : camera sits behind the object, low, and turns with its direction of travel
    ///   Overview : the whole rack stays in view while the pivot tracks the object
    /// Panning or keyboard movement turns following off. RackEnvironment adds this to the main camera in visual mode.
    /// </summary>
    public class RackCameraController : MonoBehaviour
    {
        public enum FollowMode { Off, Chase, Overview }

        public Vector3 pivot = Vector3.zero;
        public float distance = 20f;
        public float yaw = -45f, pitch = 35f;
        public float rotateSpeed = 4f, panSpeed = 1f, zoomSpeed = 0.15f, moveSpeed = 6f;
        public float minDistance = 1.5f, maxDistance = 200f;
        public bool showHelp = true;
        [Tooltip("Draw the help box itself (RackHud turns this off and shows the help in its own popup).")]
        public bool drawHelp = true;

        [Header("Follow")]
        public FollowMode followMode = FollowMode.Off;
        [Tooltip("Object to follow; the HUD updates it every frame. Null holds the camera until a target appears again.")]
        public Transform follow;
        public float chaseDistance = 4.5f, chasePitch = 22f;
        [Tooltip("Overview follow distance as a fraction of the whole-rack distance.")]
        public float overviewScale = 0.8f;
        public float overviewPitch = 42f;

        [System.NonSerialized] public bool blockWheel, blockKeys, blockDrag; // set by the HUD while the mouse is over a panel

        Vector3 homePivot; float homeDistance, homeYaw, homePitch;
        Bounds frameBounds; bool hasBounds;
        float chaseYawOffset;           // right-drag while chasing turns around the object
        float heading; bool hasHeading; Vector3 lastFollowPos; bool hasLastFollow;
        float overviewSettle;           // seconds left to ease into the overview distance / pitch

        /// <summary>Set the area to look at (called by RackEnvironment when the rack is built).</summary>
        public void Frame(Bounds b, bool resetView)
        {
            frameBounds = b; hasBounds = true;
            homePivot = b.center;
            homeDistance = Mathf.Clamp(b.extents.magnitude * 2.2f, minDistance, maxDistance);
            homeYaw = -45f; homePitch = 35f;
            if (resetView) ResetView();
        }

        public void StopFollow() { followMode = FollowMode.Off; follow = null; }
        public void ResetView() { StopFollow(); pivot = homePivot; distance = homeDistance; yaw = homeYaw; pitch = homePitch; Apply(); }
        public void FrameAll() { StopFollow(); if (hasBounds) { pivot = frameBounds.center; distance = homeDistance; } Apply(); }
        public void TopView() { if (hasBounds && followMode == FollowMode.Off) pivot = frameBounds.center; yaw = 0f; pitch = 89f; if (followMode == FollowMode.Chase) followMode = FollowMode.Overview; Apply(); }
        public void SideView() { if (hasBounds && followMode == FollowMode.Off) pivot = frameBounds.center; yaw = 0f; pitch = 5f; if (followMode == FollowMode.Chase) followMode = FollowMode.Overview; Apply(); }
        public void Zoom(float factor) { distance = Mathf.Clamp(distance * factor, minDistance, maxDistance); Apply(); }

        /// <summary>Start following t in the given mode (Off stops).</summary>
        public void Follow(Transform t, FollowMode mode)
        {
            if (mode == FollowMode.Off) { StopFollow(); return; }
            bool changed = mode != followMode;
            followMode = mode; follow = t;
            if (changed)
            {
                chaseYawOffset = 0f; hasHeading = false; hasLastFollow = false;
                if (mode == FollowMode.Overview) overviewSettle = 1.2f;
            }
        }

        void Start()
        {
            if (!hasBounds)
            {
                // derive the current view so the camera does not jump
                var e = transform.eulerAngles; yaw = e.y; pitch = e.x > 180 ? e.x - 360 : e.x;
                pivot = transform.position + transform.forward * distance;
                homePivot = pivot; homeDistance = distance; homeYaw = yaw; homePitch = pitch;
            }
            Apply();
        }

        void LateUpdate()
        {
            float dt = Time.unscaledDeltaTime; // works even when the simulation runs fast
            bool chasing = followMode == FollowMode.Chase;
#if ENABLE_LEGACY_INPUT_MANAGER
            float fast = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift) ? 3f : 1f;
            bool keys = !blockKeys, drag = !blockDrag;
            if (drag && Input.GetMouseButton(1))
            {
                float dx = Input.GetAxis("Mouse X") * rotateSpeed, dy = Input.GetAxis("Mouse Y") * rotateSpeed;
                if (chasing) { chaseYawOffset += dx; chasePitch = Mathf.Clamp(chasePitch - dy, 2f, 80f); }
                else { yaw += dx; pitch = Mathf.Clamp(pitch - dy, -5f, 89f); }
                if (Mathf.Abs(dx) + Mathf.Abs(dy) > 0.01f) overviewSettle = 0f;
            }
            bool panDrag = drag && (Input.GetMouseButton(2) || (Input.GetMouseButton(0) && (Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt))));
            if (panDrag && (Mathf.Abs(Input.GetAxis("Mouse X")) + Mathf.Abs(Input.GetAxis("Mouse Y")) > 0.001f))
            {
                StopFollow();
                float k = panSpeed * distance * 0.02f;
                pivot -= transform.right * Input.GetAxis("Mouse X") * k + transform.up * Input.GetAxis("Mouse Y") * k;
            }
            float wheel = blockWheel ? 0f : Input.mouseScrollDelta.y;
            if (Mathf.Abs(wheel) > 0.01f)
            {
                if (chasing) chaseDistance = Mathf.Clamp(chaseDistance * (1f - wheel * zoomSpeed), minDistance, 30f);
                else distance = Mathf.Clamp(distance * (1f - wheel * zoomSpeed), minDistance, maxDistance);
                overviewSettle = 0f;
            }

            // keyboard: move the pivot on the floor plane relative to the view direction
            Vector3 fwd = Quaternion.Euler(0, yaw, 0) * Vector3.forward, right = Quaternion.Euler(0, yaw, 0) * Vector3.right;
            Vector3 move = Vector3.zero;
            if (keys)
            {
                if (Input.GetKey(KeyCode.W) || Input.GetKey(KeyCode.UpArrow)) move += fwd;
                if (Input.GetKey(KeyCode.S) || Input.GetKey(KeyCode.DownArrow)) move -= fwd;
                if (Input.GetKey(KeyCode.D) || Input.GetKey(KeyCode.RightArrow)) move += right;
                if (Input.GetKey(KeyCode.A) || Input.GetKey(KeyCode.LeftArrow)) move -= right;
                if (Input.GetKey(KeyCode.E)) move += Vector3.up;
                if (Input.GetKey(KeyCode.Q)) move -= Vector3.up;
            }
            if (move != Vector3.zero) StopFollow();
            pivot += move * moveSpeed * fast * dt * Mathf.Max(1f, distance / 15f);

            if (keys)
            {
                if (Input.GetKeyDown(KeyCode.F)) FrameAll();
                if (Input.GetKeyDown(KeyCode.R)) ResetView();
                if (Input.GetKeyDown(KeyCode.H)) showHelp = !showHelp;
            }
#endif
            if (followMode != FollowMode.Off && follow != null && follow.gameObject.activeInHierarchy) UpdateFollow(dt);
            Apply();
        }

        void UpdateFollow(float dt)
        {
            Vector3 target = follow.position;
            float k = 1f - Mathf.Exp(-6f * dt);
            if (followMode == FollowMode.Chase)
            {
                // heading from the object's motion on the floor plane (kept while it stands or moves vertically)
                if (hasLastFollow)
                {
                    Vector3 d = target - lastFollowPos; d.y = 0f;
                    if (d.sqrMagnitude > 1e-6f)
                    {
                        float h = Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg;
                        if (!hasHeading) { heading = h; hasHeading = true; }
                        else heading = Mathf.LerpAngle(heading, h, 1f - Mathf.Exp(-4f * dt));
                    }
                }
                if (!hasHeading) { heading = yaw; hasHeading = true; }
                yaw = Mathf.LerpAngle(yaw, heading + chaseYawOffset, 1f - Mathf.Exp(-3f * dt));
                pitch = Mathf.Lerp(pitch, chasePitch, k);
                distance = Mathf.Lerp(distance, chaseDistance, k);
                pivot = Vector3.Lerp(pivot, target + Vector3.up * 0.4f, 1f - Mathf.Exp(-10f * dt));
            }
            else
            {
                if (overviewSettle > 0f)
                {
                    overviewSettle -= dt;
                    distance = Mathf.Lerp(distance, Mathf.Max(minDistance, homeDistance * overviewScale), k);
                    pitch = Mathf.Lerp(pitch, overviewPitch, k);
                }
                // keep the rack in view: the pivot leans toward the rack center while tracking the object
                Vector3 aim = hasBounds ? Vector3.Lerp(target, frameBounds.center, 0.35f) : target;
                pivot = Vector3.Lerp(pivot, aim, 1f - Mathf.Exp(-5f * dt));
            }
            lastFollowPos = target; hasLastFollow = true;
        }

        void Apply()
        {
            var rot = Quaternion.Euler(pitch, yaw, 0);
            transform.SetPositionAndRotation(pivot - rot * Vector3.forward * distance, rot);
        }

        void OnGUI()
        {
            if (!showHelp || !drawHelp) return;
            string text = HelpText;
            var style = new GUIStyle(GUI.skin.box) { alignment = TextAnchor.UpperLeft, fontSize = 13, wordWrap = false };
            style.normal.textColor = Color.white;
            var size = style.CalcSize(new GUIContent(text));
            GUI.Box(new Rect(10, Screen.height - size.y - 18, size.x + 12, size.y + 8), text, style); // bottom-left
        }

        public const string HelpText =
                "카메라 조작\n" +
                "오른쪽 버튼 드래그: 회전 (뒤에서 따라가기 중에는 대상 주위로 회전)\n" +
                "가운데 버튼 (또는 Alt+왼쪽) 드래그: 이동\n" +
                "휠: 확대/축소\n" +
                "W A S D / 방향키: 앞뒤좌우,  Q / E: 아래/위 (따라가기 해제)\n" +
                "Shift: 빠르게,  F: 랙 전체 보기,  R: 처음 시점,  H: 도움말 숨기기\n" +
                "왼쪽 클릭: 셔틀/화물/리프트 선택, 빈 곳 클릭: 선택 해제\n" +
                "Esc: 선택 해제,  Tab: HUD 숨기기";
    }
}
