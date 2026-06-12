#if !UNITY_6000_0_OR_NEWER
#error Creator SDK requires Unity 6 (6000.x) or newer. Please upgrade your Unity version.
#endif

using System.IO;
using UnityEditor;
using UnityEngine;

namespace CreatorSDK.Editor.Services
{
    /// <summary>
    /// Handles automatic thumbnail generation for prefabs.
    /// </summary>
    public static class ThumbnailService
    {
        /// <summary>
        /// Captures a PNG thumbnail for a prefab using Unity's AssetPreview,
        /// writes it to a temp file, and returns the file path.
        /// Returns null if a preview could not be generated.
        /// </summary>
        public static string CaptureAutoThumbnail(GameObject prefab, string outputDirectory)
        {
            if (prefab == null) return null;

            // Ensure output directory exists
            if (!Directory.Exists(outputDirectory))
            {
                Directory.CreateDirectory(outputDirectory);
            }

            Texture2D preview = AssetPreview.GetAssetPreview(prefab);

            // AssetPreview may need a frame to bake — try mini thumbnail as fallback
            if (preview == null)
                preview = AssetPreview.GetMiniThumbnail(prefab);

            if (preview == null) return null;

            // Blit to a readable texture
            RenderTexture rt = RenderTexture.GetTemporary(preview.width, preview.height);
            Graphics.Blit(preview, rt);
            RenderTexture prev = RenderTexture.active;
            RenderTexture.active = rt;

            Texture2D readable = new Texture2D(preview.width, preview.height, TextureFormat.RGBA32, false);
            readable.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
            readable.Apply();

            RenderTexture.active = prev;
            RenderTexture.ReleaseTemporary(rt);

            byte[] png = readable.EncodeToPNG();
            UnityEngine.Object.DestroyImmediate(readable);

            string path = Path.Combine(outputDirectory, $"thumb_{prefab.name}_{System.Guid.NewGuid():N}.png");
            File.WriteAllBytes(path, png);
            return path;
        }

        /// <summary>
        /// Validates if a thumbnail file exists and is readable.
        /// </summary>
        public static bool IsValidThumbnail(string thumbnailPath)
        {
            return !string.IsNullOrEmpty(thumbnailPath) && File.Exists(thumbnailPath);
        }
    }
}