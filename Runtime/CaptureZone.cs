using UnityEngine;
#if NAUGHTY_ATTRIBUTES
using NaughtyAttributes;
#endif

namespace AreaCapture
{
    public enum CaptureAxis
    {
        PositiveX, // From +X looking towards -X
        NegativeX, // From -X looking towards +X
        PositiveY, // From +Y looking towards -Y
        NegativeY, // From -Y looking towards +Y
        PositiveZ, // From +Z looking towards -Z
        NegativeZ  // From -Z looking towards +Z
    }

    public static class CaptureAxisExtensions
    {
        /// <summary>The face name used in the metadata: Front = -Z, Back = +Z, Left = -X, Right = +X, Top = +Y, Bottom = -Y.</summary>
        public static CaptureFace ToFace(this CaptureAxis axis)
        {
            switch (axis)
            {
                case CaptureAxis.PositiveX: return CaptureFace.Right;
                case CaptureAxis.NegativeX: return CaptureFace.Left;
                case CaptureAxis.PositiveY: return CaptureFace.Top;
                case CaptureAxis.NegativeY: return CaptureFace.Bottom;
                case CaptureAxis.PositiveZ: return CaptureFace.Back;
                default: return CaptureFace.Front;
            }
        }
    }

    [RequireComponent(typeof(BoxCollider))]
    public class CaptureZone : MonoBehaviour
    {
        [Tooltip("Color of the zone bounding box drawn in the Scene view.")]
        [SerializeField]
        private Color gizmoColor = new Color(0.1f, 0.1f, 0.1f, 0.2f);

        [Tooltip("Color of the highlighted face showing which side will be captured.")]
        [SerializeField]
        private Color capturedFaceColor = new Color(0f, 1f, 0f, 0.5f);

        // Gizmo geometry (not user-facing settings, unlike the colors above): the highlighted face's
        // thickness, and the arrow drawn on it to show capture direction.
        private const float FaceThickness = 0.02f;
        private const float ArrowPadding = 0.2f;
        private const float ArrowLength = 1f;
        private const float ArrowWidth = 0.05f;
        private const float ArrowHeadSize = 0.2f;

        [Tooltip("Show or hide this zone's gizmo in the Scene view.")]
        [SerializeField]
        private bool showGizmo = true;

        [Tooltip("Capture all 6 faces (±X, ±Y, ±Z). When enabled, Capture Axis and Filename Override are ignored.")]
        [SerializeField]
        private bool exportCubemap = false;

#if NAUGHTY_ATTRIBUTES
        [HideIf(nameof(exportCubemap))]
#endif
        [Tooltip("The face of the bounding box the orthographic camera looks inward from to produce the captured image.")]
        [SerializeField]
        private CaptureAxis captureAxis = CaptureAxis.NegativeZ;

#if NAUGHTY_ATTRIBUTES
        [InfoBox("Enable 'Export Cubemap' to capture all 6 faces. In cubemap mode, Capture Axis and Filename Override are ignored.", EInfoBoxType.Normal)]
#endif
        [Tooltip("Clamp camera near/far planes to the exact depth of the zone, so nothing outside its bounds renders. Disable for a looser clip range (0.3–1000 units).")]
        [SerializeField]
        private bool useStrictClipping = true;

#if NAUGHTY_ATTRIBUTES
        [HideIf(nameof(exportCubemap))]
#endif
        [Tooltip("Name of this box in the exported metadata and the prefix of its PNG files ('{name}_{Face}_L{level}_{col}x{row}.png'). If empty, the GameObject name is used.")]
        [SerializeField]
        private string filenameOverride = "";

        [Tooltip("Pixels per world unit of this box's finest (max quality) level; the Area Capture window's LoD levels are halved down from it. 0 uses the value from the Area Capture window.")]
        [Min(0)]
        [SerializeField]
        private int pixelsPerUnitOverride = 0;

        public CaptureAxis Axis => captureAxis;
        public bool ExportCubemap => exportCubemap;
        public bool UseStrictClipping => useStrictClipping;
        public string FilenameOverride { get => filenameOverride; set => filenameOverride = value; }
        public bool ShowGizmo { get => showGizmo; set => showGizmo = value; }
        public int PixelsPerUnitOverride => Mathf.Max(0, pixelsPerUnitOverride);

        public Vector3 GetGlobalPosition()
        {
            return transform.position;
        }

        public float GetGlobalRotation()
        {
            return transform.eulerAngles.z;
        }

        /// <summary>World-space centre of the box.</summary>
        public Vector3 WorldCenter
        {
            get
            {
                BoxCollider col = GetComponent<BoxCollider>();
                return col != null ? transform.TransformPoint(col.center) : transform.position;
            }
        }

        /// <summary>Size of the box along its own axes in world units (collider size × scale). Not the world AABB.</summary>
        public Vector3 OrientedSize
        {
            get
            {
                BoxCollider col = GetComponent<BoxCollider>();
                if (col == null) return Vector3.zero;
                Vector3 s = transform.lossyScale;
                return new Vector3(col.size.x * Mathf.Abs(s.x), col.size.y * Mathf.Abs(s.y), col.size.z * Mathf.Abs(s.z));
            }
        }

        /// <summary>Which single world axis the box is rotated about (None, or Unsupported for a tilted box).</summary>
        public RotationAxis RotationAbout
        {
            get
            {
                Quaternion q = transform.rotation;
                return CapturePlanner.ClassifyRotation(q.x, q.y, q.z, q.w);
            }
        }

        /// <summary>The faces this zone exports: all six for a cubemap zone, otherwise the chosen one.</summary>
        public System.Collections.Generic.IEnumerable<CaptureFace> FacesToExport()
        {
            if (exportCubemap)
            {
                // Enum order (Front, Back, Left, Right, Top, Bottom) is the export order
                foreach (CaptureFace face in System.Enum.GetValues(typeof(CaptureFace))) yield return face;
            }
            else
            {
                yield return captureAxis.ToFace();
            }
        }

        private void OnDrawGizmos()
        {
            DrawGizmo(false);
        }

        private void OnDrawGizmosSelected()
        {
            DrawGizmo(true);
        }

        private void DrawGizmo(bool selected)
        {
            if (!showGizmo) return;

            BoxCollider col = GetComponent<BoxCollider>();
            if (col == null) return;

            Color color = gizmoColor;
            if (selected) color.a = Mathf.Min(1f, color.a * 2f);

            Gizmos.color = color;

            Matrix4x4 oldMatrix = Gizmos.matrix;
            Gizmos.matrix = transform.localToWorldMatrix;

            Gizmos.DrawCube(col.center, col.size);

            Color wireColor = color;
            wireColor.a = selected ? 1f : 0.8f;
            Gizmos.color = wireColor;
            Gizmos.DrawWireCube(col.center, col.size);

            if (exportCubemap)
            {
                foreach (CaptureAxis ax in new[] {
                    CaptureAxis.PositiveX, CaptureAxis.NegativeX,
                    CaptureAxis.PositiveY, CaptureAxis.NegativeY,
                    CaptureAxis.PositiveZ, CaptureAxis.NegativeZ })
                {
                    DrawCapturedFace(col.center, col.size, ax);
                    DrawArrowForAxis(col.center, col.size, ax);
                }
            }
            else
            {
                DrawCapturedFace(col.center, col.size, captureAxis);
                DrawArrowForAxis(col.center, col.size, captureAxis);
            }

            Gizmos.matrix = oldMatrix;

#if UNITY_EDITOR
            UnityEditor.Handles.Label(WorldCenter, name);
#endif
        }

        private void DrawCapturedFace(Vector3 center, Vector3 size, CaptureAxis axis)
        {
            Vector3 direction;
            Vector3 faceSize;

            switch (axis)
            {
                case CaptureAxis.PositiveX: direction = Vector3.right;   faceSize = new Vector3(FaceThickness, size.y, size.z); break;
                case CaptureAxis.NegativeX: direction = Vector3.left;    faceSize = new Vector3(FaceThickness, size.y, size.z); break;
                case CaptureAxis.PositiveY: direction = Vector3.up;      faceSize = new Vector3(size.x, FaceThickness, size.z); break;
                case CaptureAxis.NegativeY: direction = Vector3.down;    faceSize = new Vector3(size.x, FaceThickness, size.z); break;
                case CaptureAxis.PositiveZ: direction = Vector3.forward; faceSize = new Vector3(size.x, size.y, FaceThickness); break;
                default:                    direction = Vector3.back;    faceSize = new Vector3(size.x, size.y, FaceThickness); break;
            }

            Vector3 faceCenter = center + Vector3.Scale(direction, size * 0.5f);

            Gizmos.color = capturedFaceColor;
            Gizmos.DrawCube(faceCenter, faceSize);
        }

        private void DrawArrowForAxis(Vector3 center, Vector3 size, CaptureAxis axis)
        {
            Vector3 direction = Vector3.forward;
            float halfSize = 0f;

            switch (axis)
            {
                case CaptureAxis.PositiveX: direction = Vector3.right;   halfSize = size.x * 0.5f; break;
                case CaptureAxis.NegativeX: direction = Vector3.left;    halfSize = size.x * 0.5f; break;
                case CaptureAxis.PositiveY: direction = Vector3.up;      halfSize = size.y * 0.5f; break;
                case CaptureAxis.NegativeY: direction = Vector3.down;    halfSize = size.y * 0.5f; break;
                case CaptureAxis.PositiveZ: direction = Vector3.forward; halfSize = size.z * 0.5f; break;
                case CaptureAxis.NegativeZ: direction = Vector3.back;    halfSize = size.z * 0.5f; break;
            }

            Vector3 start = center + direction * (halfSize + ArrowPadding + ArrowLength);
            Vector3 end   = center + direction * (halfSize + ArrowPadding);

            Gizmos.DrawLine(start, end);

            Vector3 up   = (direction == Vector3.up || direction == Vector3.down) ? Vector3.forward : Vector3.up;
            Vector3 side = Vector3.Cross(direction, up).normalized;
            up           = Vector3.Cross(side, direction).normalized;

            Gizmos.DrawLine(end, end + (direction * ArrowHeadSize) + (up   * ArrowWidth));
            Gizmos.DrawLine(end, end + (direction * ArrowHeadSize) - (up   * ArrowWidth));
            Gizmos.DrawLine(end, end + (direction * ArrowHeadSize) + (side * ArrowWidth));
            Gizmos.DrawLine(end, end + (direction * ArrowHeadSize) - (side * ArrowWidth));
        }
    }
}
