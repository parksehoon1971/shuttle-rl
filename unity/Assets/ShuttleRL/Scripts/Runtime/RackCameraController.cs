using UnityEngine;

namespace ShuttleRL
{
    /// <summary>
    /// Orbit / pan / zoom camera for watching the rack in Play mode (legacy Input Manager).
    ///   Right mouse drag : rotate around the pivot      Middle mouse drag (or Alt + left) : pan
    ///   Mouse wheel      : zoom                         W A S D / arrow keys : move the pivot
    ///   Q / E            : move the pivot down / up     Shift : faster
    ///   F                : frame the whole rack         R : reset view        H : show / hide help
    /// RackEnvironment adds this to the main camera automatically in visual mode.
    /// </summary>
    public class RackCameraController : MonoBehaviour
    {
        public Vector3 pivot = Vector3.zero;
        public float distance = 20f;
        public float yaw = -45f, pitch = 35f;
        public float rotateSpeed = 4f, panSpeed = 1f, zoomSpeed = 0.15f, moveSpeed = 6f;
        public float minDistance = 1.5f, maxDistance = 200f;
        public bool showHelp = true;

        Vector3 homePivot; float homeDistance, homeYaw, homePitch;
        Bounds frameBounds; bool hasBounds;

        /// <summary>Set the area to look at (called by RackEnvironment when the rack is built).</summary>
        public void Frame(Bounds b, bool resetView)
        {
            frameBounds = b; hasBounds = true;
            homePivot = b.center;
            homeDistance = Mathf.Clamp(b.extents.magnitude * 2.2f, minDistance, maxDistance);
            homeYaw = -45f; homePitch = 35f;
            if (resetView) ResetView();
        }

        void ResetView() { pivot = homePivot; distance = homeDistance; yaw = homeYaw; pitch = homePitch; Apply(); }

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
#if ENABLE_LEGACY_INPUT_MANAGER
            float fast = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift) ? 3f : 1f;
            float dt = Time.unscaledDeltaTime; // works even when the simulation runs fast

            if (Input.GetMouseButton(1))
            {
                yaw += Input.GetAxis("Mouse X") * rotateSpeed;
                pitch -= Input.GetAxis("Mouse Y") * rotateSpeed;
                pitch = Mathf.Clamp(pitch, -5f, 89f);
            }
            bool panDrag = Input.GetMouseButton(2) || (Input.GetMouseButton(0) && (Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt)));
            if (panDrag)
            {
                float k = panSpeed * distance * 0.02f;
                pivot -= transform.right * Input.GetAxis("Mouse X") * k + transform.up * Input.GetAxis("Mouse Y") * k;
            }
            float wheel = Input.mouseScrollDelta.y;
            if (Mathf.Abs(wheel) > 0.01f) distance = Mathf.Clamp(distance * (1f - wheel * zoomSpeed), minDistance, maxDistance);

            // keyboard: move the pivot on the floor plane relative to the view direction
            Vector3 fwd = Quaternion.Euler(0, yaw, 0) * Vector3.forward, right = Quaternion.Euler(0, yaw, 0) * Vector3.right;
            Vector3 move = Vector3.zero;
            if (Input.GetKey(KeyCode.W) || Input.GetKey(KeyCode.UpArrow)) move += fwd;
            if (Input.GetKey(KeyCode.S) || Input.GetKey(KeyCode.DownArrow)) move -= fwd;
            if (Input.GetKey(KeyCode.D) || Input.GetKey(KeyCode.RightArrow)) move += right;
            if (Input.GetKey(KeyCode.A) || Input.GetKey(KeyCode.LeftArrow)) move -= right;
            if (Input.GetKey(KeyCode.E)) move += Vector3.up;
            if (Input.GetKey(KeyCode.Q)) move -= Vector3.up;
            pivot += move * moveSpeed * fast * dt * Mathf.Max(1f, distance / 15f);

            if (Input.GetKeyDown(KeyCode.F) && hasBounds) { pivot = frameBounds.center; distance = homeDistance; }
            if (Input.GetKeyDown(KeyCode.R)) ResetView();
            if (Input.GetKeyDown(KeyCode.H)) showHelp = !showHelp;
#endif
            Apply();
        }

        void Apply()
        {
            var rot = Quaternion.Euler(pitch, yaw, 0);
            transform.SetPositionAndRotation(pivot - rot * Vector3.forward * distance, rot);
        }

        void OnGUI()
        {
            if (!showHelp) return;
            const string text =
                "카메라 조작\n" +
                "오른쪽 버튼 드래그: 회전\n" +
                "가운데 버튼 (또는 Alt+왼쪽) 드래그: 이동\n" +
                "휠: 확대/축소\n" +
                "W A S D / 방향키: 앞뒤좌우,  Q / E: 아래/위\n" +
                "Shift: 빠르게,  F: 랙 전체 보기,  R: 처음 시점,  H: 도움말 숨기기";
            var style = new GUIStyle(GUI.skin.box) { alignment = TextAnchor.UpperLeft, fontSize = 13, wordWrap = false };
            style.normal.textColor = Color.white;
            var size = style.CalcSize(new GUIContent(text));
            GUI.Box(new Rect(10, 10, size.x + 12, size.y + 8), text, style);
        }
    }
}
