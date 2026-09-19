using UnityEngine;
using System.Collections.Generic;

namespace AreaCapture.Runtime
{
    /// <summary>
    /// Runtime capture system for 2D areas. Renders capture zones to textures.
    /// </summary>
    public class RuntimeAreaCapture
    {
        /// <summary>Distance the capture camera sits back from the box face, in world units.</summary>
        private const float CameraStandoff = 10f;

        private Camera persistentCamera;
        private GameObject cameraHolder;

        private Camera GetOrCreateCamera()
        {
            if (persistentCamera != null) return persistentCamera;

            // Try to find existing hidden camera first
            GameObject existing = GameObject.Find("_CaptureCamera_Internal");
            if (existing != null)
            {
                cameraHolder = existing;
                persistentCamera = existing.GetComponent<Camera>();
                return persistentCamera;
            }

            cameraHolder = new GameObject("_CaptureCamera_Internal");
            cameraHolder.hideFlags = HideFlags.HideAndDontSave;
            persistentCamera = cameraHolder.AddComponent<Camera>();
            persistentCamera.enabled = false; // We use manual Render()
            persistentCamera.useOcclusionCulling = false; // Ignore occlusion mapping of objects
            TryAddUrpCameraData(cameraHolder);
            return persistentCamera;
        }

        private static void TryAddUrpCameraData(GameObject go)
        {
            const string typeName =
                "UnityEngine.Rendering.Universal.UniversalAdditionalCameraData, Unity.RenderPipelines.Universal.Runtime";
            var t = System.Type.GetType(typeName);
            if (t != null && go.GetComponent(t) == null)
                go.AddComponent(t);
        }

        public void Cleanup()
        {
            if (cameraHolder != null)
            {
                Object.DestroyImmediate(cameraHolder);
                persistentCamera = null;
                cameraHolder = null;
            }
        }

        /// <summary>
        /// Renders one tile of one face of a zone. The camera looks in from the face along the zone's own
        /// axes, so a rotated zone is captured aligned to itself, and is shifted in the image plane to the
        /// tile's centre. Returns null if the tile exceeds the hardware texture limit.
        /// </summary>
        public Texture2D CaptureTile(CaptureZone zone, TileJob job, CameraClearFlags clearFlags = CameraClearFlags.SolidColor,
            Color backgroundColor = default, int cullingMask = -1)
        {
            if (zone == null)
                return null;

            if (zone.GetComponent<BoxCollider>() == null)
            {
                Debug.LogWarning($"CaptureZone on {zone.gameObject.name} is missing a BoxCollider.");
                return null;
            }

            int maxResolution = SystemInfo.maxTextureSize;
            if (job.PixelWidth > maxResolution || job.PixelHeight > maxResolution)
            {
                Debug.LogError($"Capture resolution ({job.PixelWidth}x{job.PixelHeight}) exceeds system maximum ({maxResolution}). Lower 'Tile Pixels'.");
                return null;
            }

            Transform t = zone.transform;
            Vector3 center = zone.WorldCenter;
            Vector3 size = zone.OrientedSize; // along the zone's own axes, not the world AABB

            // The camera sits on the face's side of the box (toCamera) and looks back at it.
            Vector3 toCamera;
            Vector3 cameraUp;
            float depth;
            switch (job.Face)
            {
                case CaptureFace.Front:  toCamera = -t.forward; cameraUp = t.up;      depth = size.z; break;
                case CaptureFace.Back:   toCamera =  t.forward; cameraUp = t.up;      depth = size.z; break;
                case CaptureFace.Left:   toCamera = -t.right;   cameraUp = t.up;      depth = size.x; break;
                case CaptureFace.Right:  toCamera =  t.right;   cameraUp = t.up;      depth = size.x; break;
                case CaptureFace.Top:    toCamera =  t.up;      cameraUp = t.forward; depth = size.y; break;
                default:                 toCamera = -t.up;      cameraUp = t.forward; depth = size.y; break;
            }

            Camera cam = GetOrCreateCamera();
            cam.orthographic = true;

            // Tile framing: an orthographic camera covering exactly this tile
            cam.orthographicSize = job.TileHeightUnits * 0.5f;
            cam.aspect = job.TileWidthUnits / job.TileHeightUnits;

            cam.clearFlags = clearFlags;
            cam.backgroundColor = backgroundColor == default ? new Color(0, 0, 0, 0) : backgroundColor;
            cam.cullingMask = cullingMask;

            // Face-centred pose, then shifted within the image plane to the tile centre
            cam.transform.rotation = Quaternion.LookRotation(-toCamera, cameraUp);
            cam.transform.position = center + toCamera * (depth * 0.5f + CameraStandoff)
                                     + cam.transform.right * job.OffsetU
                                     + cam.transform.up * job.OffsetV;

            // Strict Clipping: show only stuff inside the box depth
            if (zone.UseStrictClipping)
            {
                cam.nearClipPlane = CameraStandoff - 0.01f;
                cam.farClipPlane = CameraStandoff + depth + 0.01f;
            }
            else
            {
                cam.nearClipPlane = 0.3f;
                cam.farClipPlane = 1000f;
            }

            RenderTexture rt = RenderTexture.GetTemporary(job.PixelWidth, job.PixelHeight, 24, RenderTextureFormat.ARGB32);
            Texture2D tex = null;
            try
            {
                cam.targetTexture = rt;

                // Get best models for render
                float savedLodBias = QualitySettings.lodBias;
                QualitySettings.lodBias = float.MaxValue;
                try { cam.Render(); }
                finally { QualitySettings.lodBias = savedLodBias; }

                RenderTexture.active = rt;
                // Always use RGBA32 to ensure alpha consistency
                tex = new Texture2D(job.PixelWidth, job.PixelHeight, TextureFormat.RGBA32, false);
                tex.ReadPixels(new Rect(0, 0, job.PixelWidth, job.PixelHeight), 0, 0);
                tex.Apply();
            }
            finally
            {
                RenderTexture.active = null;
                cam.targetTexture = null;
                RenderTexture.ReleaseTemporary(rt);
            }

            return tex;
        }

        /// <summary>
        /// Renders a whole face of a zone as one image at a single resolution (no tiling or LoD).
        /// Kept for callers that only need a single texture; the editor export uses <see cref="CaptureTile"/>.
        /// </summary>
        public Texture2D CaptureArea(CaptureZone captureZone, int pixelPerUnit, CameraClearFlags clearFlags = CameraClearFlags.SolidColor, Color backgroundColor = default, int cullingMask = -1, CaptureAxis? axisOverride = null)
        {
            if (captureZone == null)
                return null;

            CaptureFace face = (axisOverride ?? captureZone.Axis).ToFace();
            Vector3 size = captureZone.OrientedSize;

            // A single level with a tile budget so large that the whole face is one tile
            List<TileJob> jobs = CapturePlanner.PlanFace(face, size.x, size.y, size.z, pixelPerUnit, 1, 0, int.MaxValue);
            return CaptureTile(captureZone, jobs[0], clearFlags, backgroundColor, cullingMask);
        }

        public Dictionary<CaptureZone, Texture2D> CaptureAllZones(CaptureZone[] zones, int pixelPerUnit)
        {
            var results = new Dictionary<CaptureZone, Texture2D>();
            foreach (var zone in zones)
            {
                if (zone != null) results[zone] = CaptureArea(zone, pixelPerUnit);
            }
            return results;
        }
    }
}
