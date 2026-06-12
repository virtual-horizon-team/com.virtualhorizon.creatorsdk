using System;
using System.Text;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using UnityEngine.Networking;

namespace CreatorSDK.Editor.Services
{
    /// <summary>
    /// Handles authentication with the Virtual Horizon backend.
    /// Tokens are persisted in EditorPrefs across sessions.
    /// </summary>
    public static class AuthService
    {
        private const string BASE_URL = "https://backend-production-1958b.up.railway.app";

        private const string PREF_ACCESS_TOKEN  = "CreatorSDK_AccessToken";
        private const string PREF_REFRESH_TOKEN = "CreatorSDK_RefreshToken";
        private const string PREF_USER_NAME     = "CreatorSDK_UserName";

        // ── Public State ────────────────────────────────────────────────

        public static bool   IsLoggedIn   => !string.IsNullOrEmpty(GetAccessToken());
        public static string CurrentUser  => EditorPrefs.GetString(PREF_USER_NAME, "");

        public static string GetAccessToken()  => EditorPrefs.GetString(PREF_ACCESS_TOKEN,  "");
        public static string GetRefreshToken() => EditorPrefs.GetString(PREF_REFRESH_TOKEN, "");

        // ── Login ────────────────────────────────────────────────────────

        public static async Task<(bool success, string message)> Login(string userName, string password)
        {
            string json    = $"{{\"userName\":\"{EscapeJson(userName)}\",\"password\":\"{EscapeJson(password)}\"}}";
            byte[] body    = Encoding.UTF8.GetBytes(json);

            using var req  = new UnityWebRequest($"{BASE_URL}/api/Auth/Login", "POST");
            req.uploadHandler   = new UploadHandlerRaw(body);
            req.downloadHandler = new DownloadHandlerBuffer();
            req.SetRequestHeader("Content-Type", "application/json");

            await SendRequest(req);

            if (req.result == UnityWebRequest.Result.Success)
            {
                try
                {
                    var resp = JsonUtility.FromJson<AuthResponse>(req.downloadHandler.text);
                    string token = resp.accessToken ?? resp.token;

                    if (string.IsNullOrEmpty(token))
                        return (false, "Login response did not contain a token.");

                    EditorPrefs.SetString(PREF_ACCESS_TOKEN,  token);
                    EditorPrefs.SetString(PREF_REFRESH_TOKEN, resp.refreshToken ?? "");
                    EditorPrefs.SetString(PREF_USER_NAME,     userName);
                    return (true, "Login successful");
                }
                catch (Exception e)
                {
                    return (false, $"Failed to parse login response: {e.Message}");
                }
            }

            string detail = req.downloadHandler?.text;

            // Provide clearer error messages for different failure types
            string errorMsg;
            switch (req.result)
            {
                case UnityWebRequest.Result.ConnectionError:
                    errorMsg = $"Connection failed: {req.error}. Check your internet connection and try again.";
                    break;
                case UnityWebRequest.Result.ProtocolError:
                    errorMsg = $"Server error ({req.responseCode}): {req.error}";
                    break;
                case UnityWebRequest.Result.DataProcessingError:
                    errorMsg = $"Data processing error: {req.error}";
                    break;
                default:
                    errorMsg = $"Login failed: {req.error}";
                    break;
            }

            if (!string.IsNullOrEmpty(detail))
            {
                errorMsg += $"\nServer response: {detail}";
            }

            return (false, errorMsg);
        }

        // ── Logout ───────────────────────────────────────────────────────

        public static async Task<(bool success, string message)> Logout()
        {
            string token = GetAccessToken();

            if (!string.IsNullOrEmpty(token))
            {
                using var req  = new UnityWebRequest($"{BASE_URL}/api/Auth/Logout", "POST");
                req.downloadHandler = new DownloadHandlerBuffer();
                req.SetRequestHeader("Authorization", $"Bearer {token}");
                await SendRequest(req);
            }

            ClearTokens();
            return (true, "Logged out successfully");
        }

        // ── Token Refresh ────────────────────────────────────────────────

        public static async Task<bool> RefreshToken()
        {
            string refresh = GetRefreshToken();
            if (string.IsNullOrEmpty(refresh)) return false;

            string accessToken = GetAccessToken();

            string json = $"\"{EscapeJson(refresh)}\"";
            byte[] body = Encoding.UTF8.GetBytes(json);

            using var req  = new UnityWebRequest($"{BASE_URL}/api/Auth/Refresh", "POST");
            req.uploadHandler   = new UploadHandlerRaw(body);
            req.downloadHandler = new DownloadHandlerBuffer();
            req.SetRequestHeader("Content-Type", "application/json");
            if (!string.IsNullOrEmpty(accessToken))
            {
                req.SetRequestHeader("Authorization", $"Bearer {accessToken}");
            }

            await SendRequest(req);

            if (req.result == UnityWebRequest.Result.Success)
            {
                var resp  = JsonUtility.FromJson<AuthResponse>(req.downloadHandler.text);
                string nt = resp.accessToken ?? resp.token;
                if (!string.IsNullOrEmpty(nt))
                    EditorPrefs.SetString(PREF_ACCESS_TOKEN, nt);
                if (!string.IsNullOrEmpty(resp.refreshToken))
                    EditorPrefs.SetString(PREF_REFRESH_TOKEN, resp.refreshToken);
                return true;
            }

            return false;
        }

        // ── Helpers ──────────────────────────────────────────────────────

        private static void ClearTokens()
        {
            EditorPrefs.DeleteKey(PREF_ACCESS_TOKEN);
            EditorPrefs.DeleteKey(PREF_REFRESH_TOKEN);
            EditorPrefs.DeleteKey(PREF_USER_NAME);
        }

        /// <summary>Awaitable helper for UnityWebRequest in editor context.</summary>
        private static async Task SendRequest(UnityWebRequest req)
        {
            var op = req.SendWebRequest();
            while (!op.isDone)
                await Task.Yield();
        }

        private static string EscapeJson(string s) =>
            s?.Replace("\\", "\\\\").Replace("\"", "\\\"") ?? "";

        [Serializable]
        private class AuthResponse
        {
            public string token;         // fallback field name
            public string accessToken;
            public string refreshToken;
        }
    }
}
