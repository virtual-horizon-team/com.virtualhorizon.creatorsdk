#if !UNITY_6000_0_OR_NEWER
#error Creator SDK requires Unity 6 (6000.x) or newer. Please upgrade your Unity version.
#endif

using System;
using System.Collections.Generic;
using UnityEngine;

namespace CreatorSDK.Editor
{
    /// <summary>
    /// Represents the current lifecycle state of an upload item.
    /// </summary>
    public enum UploadStatus
    {
        Pending,
        Validating,
        Valid,
        Invalid,
        Exporting,
        Done,
        Error
    }

    /// <summary>
    /// Data container for a single prefab queued for export/upload.
    /// This class is intentionally a plain DTO with no behavior.
    /// </summary>
    [Serializable]
    public class UploadItem
    {
        // Core asset reference
        public GameObject prefab;

        // User-facing metadata
        public string displayName = "";
        public string description = "";
        public string category = "InteractiveAsset";
        public int categoryIndex = 0;
        public string tags = "";

        // Validation / export state
        public UploadStatus status = UploadStatus.Pending;
        public string statusMessage = "";
        public string generatedGuid = "";

        // Upload state (separate from export state to allow retry)
        public UploadStatus uploadStatus = UploadStatus.Pending;
        public string uploadMessage = "";

        // Thumbnail handling
        public string manualThumbnailPath = "";
        [NonSerialized] public Texture2D thumbnailTexture;

        // Set by AssetExporter so the upload phase knows which file to send
        public string lastExportedPackagePath = "";
    }

    /// <summary>
    /// Serializable descriptor for a Lua script attached to a ScenarioBridge.
    /// </summary>
    [Serializable]
    public class ScriptInfo
    {
        public string objectPath;
        public string scriptFileName;
        public string objectName;
    }

    /// <summary>
    /// JSON-serializable metadata written alongside every exported .unitypackage.
    /// </summary>
    [Serializable]
    public class AssetMetadata
    {
        public string assetId;
        public string displayName;
        public string description;
        public string category;
        public string[] tags;
        public string creatorId;
        public string createdAt;
        public string originalName;
        public string renderPipeline;
        public List<ScriptInfo> luaScripts = new List<ScriptInfo>();
        public int totalScriptCount;
    }

    /// <summary>
    /// Identifies the active render pipeline for metadata and shader compatibility tracking.
    /// </summary>
    public enum CreatorRenderPipeline
    {
        BuiltIn,
        URP,
        HDRP
    }

    /// <summary>
    /// Result container for the asset extraction phase.
    /// Passed from StandardAssetExtractor back to AssetExporter.
    /// </summary>
    public class ExtractionResult
    {
        /// <summary>Original material → extracted material (lives in temp folder).</summary>
        public Dictionary<Material, Material> MaterialMap { get; } = new Dictionary<Material, Material>();

        /// <summary>Number of textures copied via AssetDatabase.CopyAsset.</summary>
        public int CopiedTextureCount { get; set; }

        /// <summary>Number of textures extracted via AssetDatabase.ExtractAsset.</summary>
        public int ExtractedTextureCount { get; set; }

        /// <summary>Number of materials copied via AssetDatabase.CopyAsset.</summary>
        public int CopiedMaterialCount { get; set; }

        /// <summary>Number of materials extracted via AssetDatabase.ExtractAsset.</summary>
        public int ExtractedMaterialCount { get; set; }
    }
}