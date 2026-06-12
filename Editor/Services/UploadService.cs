using System;
using System.IO;
using System.Threading.Tasks;
using UnityEngine;

namespace CreatorSDK.Editor.Services
{
    /// <summary>
    /// Orchestrates the upload workflow for exported .unitypackage files to the Virtual Horizon backend.
    /// Handles authentication, file validation, progress reporting, and bundle generation.
    /// </summary>
    public static class UploadService
    {

        // ── Upload ───────────────────────────────────────────────────────

        [Serializable]
        private class SasDto
        {
            public string blobId;
            public string blobName;
            public string uploadUrl;
            public string expiresAt;
        }

        [Serializable]
        private class InitiateUploadResponse
        {
            public SasDto assetUpload;
            public SasDto thumbnailUpload;
        }

        private static string EscapeJson(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "";
            return raw.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r");
        }

        /// <summary>
        /// Uploads an exported .unitypackage using the new Initiate / SAS workflow.
        /// </summary>
        /// <param name="packageFilePath">Absolute path to the .unitypackage file.</param>
        /// <param name="assetId">UUID that was assigned during export.</param>
        /// <param name="assetName">Display name sent as query parameter.</param>
        /// <param name="assetType">Type / category string (e.g. "InteractiveAsset").</param>
        /// <param name="description">Description of the asset.</param>
        /// <param name="thumbnailPath">Absolute path to thumbnail image, or null.</param>
        /// <param name="progress">Optional progress reporter for UI feedback.</param>
        public static async Task<(bool success, string message)> UploadAsset(
            string packageFilePath,
            string assetId,
            string assetName,
            string assetType,
            string description,
            string thumbnailPath,
            IProgress<float> progress = null)
        {
            // Authentication check
            string token = AuthService.GetAccessToken();
            if (string.IsNullOrEmpty(token))
                return (false, "Not authenticated. Please login in the Account tab first.");

            // File validation
            if (!File.Exists(packageFilePath))
                return (false, $"Package file not found:\n{packageFilePath}");

            progress?.Report(0.05f);

            // Read package file
            byte[] packageBytes;
            try
            {
                packageBytes = File.ReadAllBytes(packageFilePath);
            }
            catch (Exception ex)
            {
                return (false, $"Failed to read package file: {ex.Message}");
            }

            progress?.Report(0.1f);

            // Read thumbnail if provided
            byte[] thumbnailBytes = null;
            string thumbContentType = "image/png";

            if (ThumbnailService.IsValidThumbnail(thumbnailPath))
            {
                try
                {
                    thumbnailBytes = File.ReadAllBytes(thumbnailPath);
                    thumbContentType = thumbnailPath.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
                                       thumbnailPath.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase)
                                       ? "image/jpeg" : "image/png";
                }
                catch (Exception)
                {
                    thumbnailBytes = null;
                }
            }

            progress?.Report(0.15f);

            // 1. Initiate Upload
            string initiateUrl = $"{HttpClient.GetBaseUrl()}/api/Asset/Upload/Initiate";
            string initiateJson = "{" +
                $"\"assetId\":\"{EscapeJson(assetId)}\"," +
                $"\"assetName\":\"{EscapeJson(assetName)}\"," +
                $"\"description\":\"{EscapeJson(description ?? "")}\"," +
                $"\"assetType\":\"{EscapeJson(assetType ?? "InteractiveAsset")}\"," +
                "\"price\":0," +
                "\"categoryId\":null," +
                "\"isFree\":true," +
                "\"detailImageCount\":0," +
                "\"includeVideo\":false," +
                "\"videoContentType\":null" +
            "}";

            var initiateProgress = new Progress<float>(p => progress?.Report(0.15f + p * 0.1f));
            var (initSuccess, initMsg) = await HttpClient.PostJsonWithProgress(initiateUrl, initiateJson, token, initiateProgress);

            if (!initSuccess)
            {
                return (false, $"Failed to initiate upload: {initMsg}");
            }

            // Parse response
            InitiateUploadResponse initResponse;
            try
            {
                initResponse = JsonUtility.FromJson<InitiateUploadResponse>(initMsg);
            }
            catch (Exception ex)
            {
                return (false, $"Failed to parse initiate upload response: {ex.Message}\nResponse: {initMsg}");
            }

            if (initResponse == null || initResponse.assetUpload == null || string.IsNullOrEmpty(initResponse.assetUpload.uploadUrl))
            {
                return (false, $"Invalid initiate upload response: Missing assetUpload or uploadUrl.\nResponse: {initMsg}");
            }

            progress?.Report(0.25f);

            // 2. Upload .unitypackage to Azure Blob Storage
            float packageStart = 0.25f;
            float packageEnd = thumbnailBytes != null ? 0.85f : 1.0f;
            float packageRange = packageEnd - packageStart;

            var packageProgress = new Progress<float>(p => progress?.Report(packageStart + p * packageRange));
            var (uploadSuccess, uploadMsg) = await HttpClient.PutBytesWithProgress(
                initResponse.assetUpload.uploadUrl,
                packageBytes,
                "application/octet-stream",
                packageProgress);

            if (!uploadSuccess)
            {
                return (false, $"Failed to upload package to Azure: {uploadMsg}");
            }

            // 3. Upload thumbnail to Azure Blob Storage if provided
            if (thumbnailBytes != null && initResponse.thumbnailUpload != null && !string.IsNullOrEmpty(initResponse.thumbnailUpload.uploadUrl))
            {
                var thumbProgress = new Progress<float>(p => progress?.Report(0.85f + p * 0.15f));
                var (thumbSuccess, thumbMsg) = await HttpClient.PutBytesWithProgress(
                    initResponse.thumbnailUpload.uploadUrl,
                    thumbnailBytes,
                    thumbContentType,
                    thumbProgress);

                if (!thumbSuccess)
                {
                    return (false, $"Failed to upload thumbnail to Azure: {thumbMsg}");
                }
            }

            progress?.Report(1.0f);
            return (true, $"Upload successful!\nAsset ID: {assetId}");
        }

        /// <summary>
        /// Uploads an exported .unitypackage with automatic progress bar display.
        /// </summary>
        public static async Task<(bool success, string message)> UploadAssetWithProgressBar(
            string packageFilePath,
            string assetId,
            string assetName,
            string assetType,
            string description,
            string thumbnailPath)
        {
            using var progress = new ProgressReporter("Uploading Asset", $"Uploading {assetName}");

            try
            {
                return await UploadAsset(packageFilePath, assetId, assetName, assetType, description, thumbnailPath, progress);
            }
            finally
            {
                // Progress bar is automatically cleared by Dispose()
            }
        }

        // ── Bundle Generation ────────────────────────────────────────────

        /// <summary>
        /// Asks the server to build an AssetBundle for the given platform.
        /// Must be called after a successful upload.
        /// </summary>
        /// <param name="assetId">UUID of the already-uploaded asset.</param>
        /// <param name="targetPlatform">e.g. "Windows" or "Android".</param>
        /// <param name="progress">Optional progress reporter for UI feedback.</param>
        public static async Task<(bool success, string message)> GenerateBundle(
            string assetId, string targetPlatform, IProgress<float> progress = null)
        {
            string token = AuthService.GetAccessToken();
            if (string.IsNullOrEmpty(token))
                return (false, "Not authenticated.");

            progress?.Report(0.1f);

            string url = $"{HttpClient.GetBaseUrl()}/api/AssetBundle/Generate" +
                         $"?AssetID={Uri.EscapeDataString(assetId)}" +
                         $"&TargetPlatform={Uri.EscapeDataString(targetPlatform)}";

            progress?.Report(0.5f);

            var (success, message) = await HttpClient.PostWithProgress(url, token, progress);

            if (success)
            {
                return (true, $"Bundle generation started for {targetPlatform}.");
            }

            return (false, message);
        }

        /// <summary>
        /// Generates an AssetBundle with automatic progress bar display.
        /// </summary>
        public static async Task<(bool success, string message)> GenerateBundleWithProgressBar(
            string assetId, string targetPlatform)
        {
            using var progress = new ProgressReporter("Generating Bundle", $"Platform: {targetPlatform}");

            try
            {
                return await GenerateBundle(assetId, targetPlatform, progress);
            }
            finally
            {
                // Progress bar is automatically cleared by Dispose()
            }
        }

        // ── Thumbnail Generation ─────────────────────────────────────────

        /// <summary>
        /// Captures an automatic thumbnail for a prefab.
        /// </summary>
        public static string CaptureAutoThumbnail(GameObject prefab, string outputDirectory)
        {
            return ThumbnailService.CaptureAutoThumbnail(prefab, outputDirectory);
        }
    }
}
