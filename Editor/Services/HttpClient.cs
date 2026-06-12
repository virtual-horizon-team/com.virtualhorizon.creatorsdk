#if !UNITY_6000_0_OR_NEWER
#error Creator SDK requires Unity 6 (6000.x) or newer. Please upgrade your Unity version.
#endif

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

namespace CreatorSDK.Editor.Services
{
    /// <summary>
    /// Handles HTTP communication with progress tracking and proper timeout handling.
    /// </summary>
    public static class HttpClient
    {
        private const string BASE_URL = "https://backend-production-1958b.up.railway.app";
        private const int TIMEOUT_SECONDS = 300; // 5 minutes for large uploads
        private const int MAX_RETRIES = 2; // Retry failed requests up to 2 times
        private const int RETRY_DELAY_MS = 2000; // 2 second delay between retries

        /// <summary>
        /// Uploads a file with progress reporting and automatic retry on network failures.
        /// </summary>
        public static async Task<(bool success, string message)> UploadFileWithProgress(
            string url,
            List<IMultipartFormSection> formData,
            string authToken,
            IProgress<float> progress = null)
        {
            for (int attempt = 0; attempt <= MAX_RETRIES; attempt++)
            {
                if (attempt > 0)
                {
                    await Task.Delay(RETRY_DELAY_MS);
                    progress?.Report(0.0f); // Reset progress for retry
                }

                using var req = UnityWebRequest.Post(url, formData);
                req.SetRequestHeader("Authorization", $"Bearer {authToken}");
                req.timeout = TIMEOUT_SECONDS;

                // Start the upload
                var operation = req.SendWebRequest();

                // Track progress with proper yielding
                while (!operation.isDone)
                {
                    if (progress != null)
                    {
                        float uploadProgress = operation.progress;
                        progress.Report(uploadProgress);
                    }
                    // Yield control properly without blocking
                    await Task.Yield();
                }

                // Final progress update
                if (progress != null)
                {
                    progress.Report(1.0f);
                }

                if (req.result == UnityWebRequest.Result.Success)
                {
                    return (true, req.downloadHandler?.text ?? "Upload successful");
                }

                // Handle different types of failures
                string errorMsg;
                bool shouldRetry = false;

                switch (req.result)
                {
                    case UnityWebRequest.Result.ConnectionError:
                        errorMsg = $"Connection failed: {req.error}. Check your internet connection.";
                        shouldRetry = attempt < MAX_RETRIES; // Retry connection errors
                        if (shouldRetry)
                            errorMsg += $" Retrying... ({attempt + 1}/{MAX_RETRIES + 1})";
                        break;
                    case UnityWebRequest.Result.ProtocolError:
                        if (req.responseCode == 401)
                        {
                            Debug.Log("[CreatorSDK] Access token expired (401). Attempting to refresh token...");
                            bool refreshed = await AuthService.RefreshToken();
                            if (refreshed)
                            {
                                authToken = AuthService.GetAccessToken();
                                errorMsg = "Token refreshed. Retrying request...";
                                shouldRetry = true;
                                break;
                            }
                            else
                            {
                                errorMsg = "Access token expired (401) and token refresh failed. Please log in again.";
                                shouldRetry = false;
                                break;
                            }
                        }
                        errorMsg = $"Server error ({req.responseCode}): {req.error}";
                        shouldRetry = false; // Don't retry server errors
                        break;
                    case UnityWebRequest.Result.DataProcessingError:
                        errorMsg = $"Data processing error: {req.error}";
                        shouldRetry = false; // Don't retry data processing errors
                        break;
                    default:
                        errorMsg = $"Upload failed: {req.error}";
                        shouldRetry = false;
                        break;
                }

                string body = req.downloadHandler?.text ?? "";
                if (!string.IsNullOrEmpty(body))
                {
                    errorMsg += $"\nServer response: {body}";
                }

                // If this is not the last attempt and we should retry, continue the loop
                if (shouldRetry && attempt < MAX_RETRIES)
                {
                    continue;
                }

                return (false, errorMsg);
            }

            // This should never be reached, but just in case
            return (false, "Upload failed after all retry attempts");
        }

        /// <summary>
        /// Sends a POST request with progress reporting and automatic retry on network failures.
        /// </summary>
        public static async Task<(bool success, string message)> PostWithProgress(
            string url,
            string authToken,
            IProgress<float> progress = null)
        {
            for (int attempt = 0; attempt <= MAX_RETRIES; attempt++)
            {
                if (attempt > 0)
                {
                    await Task.Delay(RETRY_DELAY_MS);
                    progress?.Report(0.0f); // Reset progress for retry
                }

                using var req = new UnityWebRequest(url, "POST");
                req.downloadHandler = new DownloadHandlerBuffer();
                req.SetRequestHeader("Authorization", $"Bearer {authToken}");
                req.SetRequestHeader("Content-Length", "0");
                req.timeout = TIMEOUT_SECONDS;

                // Start the request
                var operation = req.SendWebRequest();

                // Track progress with proper yielding
                while (!operation.isDone)
                {
                    if (progress != null)
                    {
                        float requestProgress = operation.progress;
                        progress.Report(requestProgress);
                    }
                    // Yield control properly without blocking
                    await Task.Yield();
                }

                // Final progress update
                if (progress != null)
                {
                    progress.Report(1.0f);
                }

                if (req.result == UnityWebRequest.Result.Success)
                {
                    return (true, req.downloadHandler?.text ?? "Request successful");
                }

                // Handle different types of failures
                string errorMsg;
                bool shouldRetry = false;

                switch (req.result)
                {
                    case UnityWebRequest.Result.ConnectionError:
                        errorMsg = $"Connection failed: {req.error}. Check your internet connection.";
                        shouldRetry = attempt < MAX_RETRIES; // Retry connection errors
                        if (shouldRetry)
                            errorMsg += $" Retrying... ({attempt + 1}/{MAX_RETRIES + 1})";
                        break;
                    case UnityWebRequest.Result.ProtocolError:
                        if (req.responseCode == 401)
                        {
                            Debug.Log("[CreatorSDK] Access token expired (401). Attempting to refresh token...");
                            bool refreshed = await AuthService.RefreshToken();
                            if (refreshed)
                            {
                                authToken = AuthService.GetAccessToken();
                                errorMsg = "Token refreshed. Retrying request...";
                                shouldRetry = true;
                                break;
                            }
                            else
                            {
                                errorMsg = "Access token expired (401) and token refresh failed. Please log in again.";
                                shouldRetry = false;
                                break;
                            }
                        }
                        errorMsg = $"Server error ({req.responseCode}): {req.error}";
                        shouldRetry = false; // Don't retry server errors
                        break;
                    case UnityWebRequest.Result.DataProcessingError:
                        errorMsg = $"Data processing error: {req.error}";
                        shouldRetry = false; // Don't retry data processing errors
                        break;
                    default:
                        errorMsg = $"Request failed: {req.error}";
                        shouldRetry = false;
                        break;
                }

                string body = req.downloadHandler?.text ?? "";
                if (!string.IsNullOrEmpty(body))
                {
                    errorMsg += $"\nServer response: {body}";
                }

                // If this is not the last attempt and we should retry, continue the loop
                if (shouldRetry && attempt < MAX_RETRIES)
                {
                    continue;
                }

                return (false, errorMsg);
            }

            // This should never be reached, but just in case
            return (false, "Request failed after all retry attempts");
        }

        /// <summary>
        /// Sends a POST request with a JSON payload, progress reporting, and automatic retry on network/401 failures.
        /// </summary>
        public static async Task<(bool success, string message)> PostJsonWithProgress(
            string url,
            string jsonPayload,
            string authToken,
            IProgress<float> progress = null)
        {
            for (int attempt = 0; attempt <= MAX_RETRIES; attempt++)
            {
                if (attempt > 0)
                {
                    await Task.Delay(RETRY_DELAY_MS);
                    progress?.Report(0.0f); // Reset progress for retry
                }

                using var req = new UnityWebRequest(url, "POST");
                byte[] body = System.Text.Encoding.UTF8.GetBytes(jsonPayload);
                req.uploadHandler = new UploadHandlerRaw(body);
                req.downloadHandler = new DownloadHandlerBuffer();
                req.SetRequestHeader("Content-Type", "application/json");
                req.SetRequestHeader("Authorization", $"Bearer {authToken}");
                req.timeout = TIMEOUT_SECONDS;

                // Start the request
                var operation = req.SendWebRequest();

                // Track progress with proper yielding
                while (!operation.isDone)
                {
                    if (progress != null)
                    {
                        float requestProgress = operation.progress;
                        progress.Report(requestProgress);
                    }
                    // Yield control properly without blocking
                    await Task.Yield();
                }

                // Final progress update
                if (progress != null)
                {
                    progress.Report(1.0f);
                }

                if (req.result == UnityWebRequest.Result.Success)
                {
                    return (true, req.downloadHandler?.text ?? "Request successful");
                }

                // Handle different types of failures
                string errorMsg;
                bool shouldRetry = false;

                switch (req.result)
                {
                    case UnityWebRequest.Result.ConnectionError:
                        errorMsg = $"Connection failed: {req.error}. Check your internet connection.";
                        shouldRetry = attempt < MAX_RETRIES; // Retry connection errors
                        if (shouldRetry)
                            errorMsg += $" Retrying... ({attempt + 1}/{MAX_RETRIES + 1})";
                        break;
                    case UnityWebRequest.Result.ProtocolError:
                        if (req.responseCode == 401)
                        {
                            Debug.Log("[CreatorSDK] Access token expired (401). Attempting to refresh token...");
                            bool refreshed = await AuthService.RefreshToken();
                            if (refreshed)
                            {
                                authToken = AuthService.GetAccessToken();
                                errorMsg = "Token refreshed. Retrying request...";
                                shouldRetry = true;
                                break;
                            }
                            else
                            {
                                errorMsg = "Access token expired (401) and token refresh failed. Please log in again.";
                                shouldRetry = false;
                                break;
                            }
                        }
                        errorMsg = $"Server error ({req.responseCode}): {req.error}";
                        shouldRetry = false; // Don't retry server errors
                        break;
                    case UnityWebRequest.Result.DataProcessingError:
                        errorMsg = $"Data processing error: {req.error}";
                        shouldRetry = false; // Don't retry data processing errors
                        break;
                    default:
                        errorMsg = $"Request failed: {req.error}";
                        shouldRetry = false;
                        break;
                }

                string bodyText = req.downloadHandler?.text ?? "";
                if (!string.IsNullOrEmpty(bodyText))
                {
                    errorMsg += $"\nServer response: {bodyText}";
                }

                // If this is not the last attempt and we should retry, continue the loop
                if (shouldRetry && attempt < MAX_RETRIES)
                {
                    continue;
                }

                return (false, errorMsg);
            }

            // This should never be reached, but just in case
            return (false, "Request failed after all retry attempts");
        }

        /// <summary>
        /// Uploads raw bytes to a URL (such as an Azure SAS URL) using PUT, with progress reporting and automatic retry on connection failures.
        /// </summary>
        public static async Task<(bool success, string message)> PutBytesWithProgress(
            string url,
            byte[] data,
            string contentType,
            IProgress<float> progress = null)
        {
            for (int attempt = 0; attempt <= MAX_RETRIES; attempt++)
            {
                if (attempt > 0)
                {
                    await Task.Delay(RETRY_DELAY_MS);
                    progress?.Report(0.0f); // Reset progress for retry
                }

                using var req = new UnityWebRequest(url, "PUT");
                req.uploadHandler = new UploadHandlerRaw(data);
                req.downloadHandler = new DownloadHandlerBuffer();
                req.SetRequestHeader("Content-Type", contentType);
                req.SetRequestHeader("x-ms-blob-type", "BlockBlob");
                req.timeout = TIMEOUT_SECONDS;

                // Start the request
                var operation = req.SendWebRequest();

                // Track progress with proper yielding
                while (!operation.isDone)
                {
                    if (progress != null)
                    {
                        float requestProgress = operation.progress;
                        progress.Report(requestProgress);
                    }
                    // Yield control properly without blocking
                    await Task.Yield();
                }

                // Final progress update
                if (progress != null)
                {
                    progress.Report(1.0f);
                }

                if (req.result == UnityWebRequest.Result.Success)
                {
                    return (true, "Upload successful");
                }

                // Handle different types of failures
                string errorMsg;
                bool shouldRetry = false;

                switch (req.result)
                {
                    case UnityWebRequest.Result.ConnectionError:
                        errorMsg = $"Connection failed: {req.error}. Check your internet connection.";
                        shouldRetry = attempt < MAX_RETRIES; // Retry connection errors
                        if (shouldRetry)
                            errorMsg += $" Retrying... ({attempt + 1}/{MAX_RETRIES + 1})";
                        break;
                    case UnityWebRequest.Result.ProtocolError:
                        errorMsg = $"Server error ({req.responseCode}): {req.error}";
                        shouldRetry = false; // Don't retry server protocol errors on PUT to Azure
                        break;
                    case UnityWebRequest.Result.DataProcessingError:
                        errorMsg = $"Data processing error: {req.error}";
                        shouldRetry = false;
                        break;
                    default:
                        errorMsg = $"Upload failed: {req.error}";
                        shouldRetry = false;
                        break;
                }

                string bodyText = req.downloadHandler?.text ?? "";
                if (!string.IsNullOrEmpty(bodyText))
                {
                    errorMsg += $"\nServer response: {bodyText}";
                }

                // If this is not the last attempt and we should retry, continue the loop
                if (shouldRetry && attempt < MAX_RETRIES)
                {
                    continue;
                }

                return (false, errorMsg);
            }

            // This should never be reached, but just in case
            return (false, "Upload failed after all retry attempts");
        }

        /// <summary>
        /// Gets the base URL for API calls.
        /// </summary>
        public static string GetBaseUrl() => BASE_URL;
    }
}