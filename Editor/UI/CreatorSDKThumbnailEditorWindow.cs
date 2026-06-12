#if !UNITY_6000_0_OR_NEWER
#error Creator SDK requires Unity 6 (6000.x) or newer. Please upgrade your Unity version.
#endif

using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace CreatorSDK.Editor.UI
{
    /// <summary>
    /// Interactive editor window using PreviewRenderUtility to allow creators
    /// to adjust prefab rotation, camera zoom, and offset before capturing a thumbnail.
    /// </summary>
    public class CreatorSDKThumbnailEditorWindow : EditorWindow
    {
        private PreviewRenderUtility previewUtility;
        private GameObject prefab;
        private GameObject instantiatedObject;
        private Action<string> callback;

        private Vector2 dragStart;
        private Vector3 cameraRotation = new Vector3(15, -135, 0);
        private float cameraDistance = 5.0f;
        private Vector3 cameraTarget = Vector3.zero;
        private bool shouldSaveThumbnail = false;

        public static void ShowWindow(GameObject prefab, Action<string> onSave)
        {
            var window = GetWindow<CreatorSDKThumbnailEditorWindow>(true, "Thumbnail Designer", true);
            window.prefab = prefab;
            window.callback = onSave;
            window.minSize = new Vector2(450, 500);
            window.maxSize = new Vector2(700, 750);
            window.ShowUtility();
        }

        private void OnEnable()
        {
            previewUtility = new PreviewRenderUtility();
            // Configure default lighting setup in the preview scene
            previewUtility.lights[0].enabled = true;
            previewUtility.lights[1].enabled = true;
        }

        private void OnDisable()
        {
            if (previewUtility != null)
            {
                previewUtility.Cleanup();
                previewUtility = null;
            }
            if (instantiatedObject != null)
            {
                DestroyImmediate(instantiatedObject);
                instantiatedObject = null;
            }
        }

        private void OnGUI()
        {
            if (previewUtility == null) return;

            // Instantiate object if needed
            if (instantiatedObject == null && prefab != null)
            {
                instantiatedObject = previewUtility.InstantiatePrefabInScene(prefab);
                instantiatedObject.transform.position = Vector3.zero;

                Bounds bounds = GetObjectBounds(instantiatedObject);
                cameraTarget = Vector3.zero;
                // Offset object pivot so its bounds center is at (0,0,0)
                instantiatedObject.transform.position = -bounds.center;

                cameraDistance = bounds.extents.magnitude * 2.5f;
                if (cameraDistance < 1f) cameraDistance = 3f;
            }

            // Title Banner
            EditorGUILayout.BeginVertical();
            GUILayout.Space(12);
            var titleStyle = new GUIStyle(EditorStyles.boldLabel)
            {
                alignment = TextAnchor.MiddleCenter,
                fontSize = 15,
                normal = { textColor = Color.white }
            };
            EditorGUILayout.LabelField("Interactive Thumbnail Designer", titleStyle);

            var subtitleStyle = new GUIStyle(EditorStyles.miniLabel)
            {
                alignment = TextAnchor.MiddleCenter,
                normal = { textColor = new Color(0.65f, 0.65f, 0.65f) }
            };
            EditorGUILayout.LabelField("Left-Drag to Rotate • Right-Drag/Shift+Drag to Pan • Scroll to Zoom", subtitleStyle);
            GUILayout.Space(10);

            // Preview viewport container (stretches borderless across the full window width)
            Rect previewRect = GUILayoutUtility.GetRect(position.width, position.height - 120);
            previewRect.x = 0;
            previewRect.width = position.width;

            // Handle inputs in viewport
            HandleMouseInput(previewRect);

            // Draw a solid background for the preview area in the Editor GUI
            EditorGUI.DrawRect(previewRect, new Color(0.3f, 0.3f, 0.3f, 1f));

            // Render interactive viewport
            previewUtility.BeginPreview(previewRect, GUIStyle.none);

            // Configure Camera with a solid, pleasant medium-light grey background (matching standard Unity asset previews)
            var rot = Quaternion.Euler(cameraRotation.x, cameraRotation.y, 0);
            var pos = cameraTarget - (rot * Vector3.forward * cameraDistance);
            previewUtility.camera.transform.position = pos;
            previewUtility.camera.transform.rotation = rot;
            previewUtility.camera.nearClipPlane = 0.01f;
            previewUtility.camera.farClipPlane = 100f;
            previewUtility.camera.clearFlags = CameraClearFlags.Color;
            previewUtility.camera.backgroundColor = new Color(0.3f, 0.3f, 0.3f, 1f);

            // Set Directional lights matching camera angle for bright, clear shading
            previewUtility.lights[0].intensity = 1.8f;
            previewUtility.lights[0].color = Color.white;
            previewUtility.lights[0].transform.rotation = rot * Quaternion.Euler(40, 40, 0);

            previewUtility.lights[1].intensity = 1.0f;
            previewUtility.lights[1].color = new Color(0.85f, 0.9f, 1.0f); // cool fill light
            previewUtility.lights[1].transform.rotation = rot * Quaternion.Euler(-30, -50, 0);

            // Temporarily set ambient light to illuminate shadows
            Color oldAmbient = RenderSettings.ambientLight;
            UnityEngine.Rendering.AmbientMode oldAmbientMode = RenderSettings.ambientMode;
            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.35f, 0.35f, 0.35f, 1f);

            previewUtility.camera.Render();

            // Restore ambient settings
            RenderSettings.ambientLight = oldAmbient;
            RenderSettings.ambientMode = oldAmbientMode;

            if (shouldSaveThumbnail)
            {
                DoSaveThumbnail();
                shouldSaveThumbnail = false;
                previewUtility.EndPreview();
                Close();
                GUIUtility.ExitGUI();
            }

            Texture previewTexture = previewUtility.EndPreview();
            GUI.DrawTexture(previewRect, previewTexture);

            GUILayout.Space(12);

            // Controls row
            EditorGUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();

            // Reset View
            var resetStyle = new GUIStyle(GUI.skin.button)
            {
                fontSize = 11,
                padding = new RectOffset(10, 10, 4, 4)
            };
            if (GUILayout.Button("Reset View", resetStyle, GUILayout.Width(100), GUILayout.Height(24)))
            {
                cameraRotation = new Vector3(15, -135, 0);
                if (instantiatedObject != null)
                {
                    Bounds bounds = GetObjectBounds(instantiatedObject);
                    cameraDistance = bounds.extents.magnitude * 2.5f;
                    cameraTarget = Vector3.zero;
                }
                Repaint();
            }
            // Add hover cursor for Reset View
            EditorGUIUtility.AddCursorRect(GUILayoutUtility.GetLastRect(), MouseCursor.Link);

            GUILayout.Space(10);

            // Save
            GUI.backgroundColor = new Color(0.7f, 0.05f, 0.15f); // Brand red
            var saveStyle = new GUIStyle(GUI.skin.button)
            {
                fontStyle = FontStyle.Bold,
                fontSize = 11,
                normal = { textColor = Color.white }
            };
            if (GUILayout.Button("SAVE THUMBNAIL", saveStyle, GUILayout.Width(160), GUILayout.Height(24)))
            {
                shouldSaveThumbnail = true;
                Repaint();
            }
            // Add hover cursor for Save
            EditorGUIUtility.AddCursorRect(GUILayoutUtility.GetLastRect(), MouseCursor.Link);
            GUI.backgroundColor = Color.white;

            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();

            GUILayout.Space(12);
            EditorGUILayout.EndVertical();
        }

        private void HandleMouseInput(Rect rect)
        {
            Event e = Event.current;
            if (!rect.Contains(e.mousePosition)) return;

            if (e.type == EventType.MouseDown)
            {
                dragStart = e.mousePosition;
                e.Use();
            }
            else if (e.type == EventType.MouseDrag)
            {
                Vector2 delta = e.mousePosition - dragStart;
                dragStart = e.mousePosition;

                if (e.button == 0 && !e.shift) // Left drag: Rotate camera
                {
                    cameraRotation.y += delta.x * 0.6f;
                    cameraRotation.x += delta.y * 0.6f;
                    cameraRotation.x = Mathf.Clamp(cameraRotation.x, -85f, 85f);
                }
                else if (e.button == 1 || e.button == 2 || (e.button == 0 && e.shift)) // Right/Middle drag or Shift+Left drag: Pan target
                {
                    var rot = Quaternion.Euler(cameraRotation.x, cameraRotation.y, 0);
                    Vector3 right = rot * Vector3.right;
                    Vector3 up = rot * Vector3.up;
                    float factor = cameraDistance * 0.002f;
                    cameraTarget -= right * delta.x * factor + up * delta.y * factor;
                }
                Repaint();
                e.Use();
            }
            else if (e.type == EventType.ScrollWheel) // Mouse scroll: Zoom
            {
                cameraDistance += e.delta.y * (cameraDistance * 0.06f);
                cameraDistance = Mathf.Clamp(cameraDistance, 0.1f, 100f);
                Repaint();
                e.Use();
            }
        }

        private void DoSaveThumbnail()
        {
            if (previewUtility == null || previewUtility.camera.targetTexture == null) return;

            string tempDir = Path.Combine(Application.dataPath, "../Temp/CreatorSDK_Thumbs");
            if (!Directory.Exists(tempDir))
            {
                Directory.CreateDirectory(tempDir);
            }

            int width = previewUtility.camera.targetTexture.width;
            int height = previewUtility.camera.targetTexture.height;

            // Define temporary descriptor with proper color space setting (sRGB conversion)
            RenderTextureDescriptor desc = new RenderTextureDescriptor(width, height, RenderTextureFormat.ARGB32, 24);
            desc.sRGB = QualitySettings.activeColorSpace == ColorSpace.Linear;

            RenderTexture tempRT = RenderTexture.GetTemporary(desc);

            // Blit performs the Linear -> sRGB color space conversion automatically
            Graphics.Blit(previewUtility.camera.targetTexture, tempRT);

            RenderTexture currentRT = RenderTexture.active;
            RenderTexture.active = tempRT;

            Texture2D readable = new Texture2D(width, height, TextureFormat.RGBA32, false);
            readable.ReadPixels(new Rect(0, 0, width, height), 0, 0);
            readable.Apply();

            RenderTexture.active = currentRT;
            RenderTexture.ReleaseTemporary(tempRT);

            byte[] png = readable.EncodeToPNG();
            DestroyImmediate(readable);

            string path = Path.Combine(tempDir, $"thumb_{prefab.name}_{System.Guid.NewGuid():N}.png");
            File.WriteAllBytes(path, png);

            callback?.Invoke(path);
        }

        private static Bounds GetObjectBounds(GameObject go)
        {
            var renderers = go.GetComponentsInChildren<Renderer>(true);
            if (renderers.Length == 0) return new Bounds(go.transform.position, Vector3.one);

            Bounds b = renderers[0].bounds;
            for (int i = 1; i < renderers.Length; i++)
            {
                b.Encapsulate(renderers[i].bounds);
            }
            return b;
        }
    }
}
