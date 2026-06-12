#if !UNITY_6000_0_OR_NEWER
#error Creator SDK requires Unity 6 (6000.x) or newer. Please upgrade your Unity version.
#endif

using System;
using System.IO;
using UnityEngine;

namespace CreatorSDK.Editor.Services
{
    /// <summary>
    /// Simple file logger that writes logs to a file on disk and optionally to Unity console.
    /// </summary>
    public static class FileLogger
    {
        private static string logFilePath;
        private static bool initialized = false;

        /// <summary>
        /// Initializes the logger with a file path. Call this once at the start of operations.
        /// </summary>
        /// <param name="filePath">Absolute path to the log file.</param>
        public static void Initialize(string filePath)
        {
            logFilePath = filePath;
            initialized = true;

            // Create directory if it doesn't exist
            string directory = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            // Write header to log file
            try
            {
                File.WriteAllText(logFilePath, $"Creator SDK Export Log - {DateTime.Now:yyyy-MM-dd HH:mm:ss}\n");
                File.AppendAllText(logFilePath, "=".PadRight(80, '=') + "\n\n");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[CreatorSDK] Failed to initialize log file: {ex.Message}");
            }
        }

        /// <summary>
        /// Logs an info message.
        /// </summary>
        public static void Log(string message)
        {
            if (initialized)
            {
                WriteToFile($"[INFO] {DateTime.Now:HH:mm:ss} - {message}");
            }
            Debug.Log($"[CreatorSDK] {message}");
        }

        /// <summary>
        /// Logs a warning message.
        /// </summary>
        public static void LogWarning(string message)
        {
            if (initialized)
            {
                WriteToFile($"[WARNING] {DateTime.Now:HH:mm:ss} - {message}");
            }
            Debug.LogWarning($"[CreatorSDK] {message}");
        }

        /// <summary>
        /// Logs an error message.
        /// </summary>
        public static void LogError(string message)
        {
            if (initialized)
            {
                WriteToFile($"[ERROR] {DateTime.Now:HH:mm:ss} - {message}");
            }
            Debug.LogError($"[CreatorSDK] {message}");
        }

        /// <summary>
        /// Logs an exception with stack trace.
        /// </summary>
        public static void LogException(Exception ex, string context = "")
        {
            string message = string.IsNullOrEmpty(context) ? ex.Message : $"{context}: {ex.Message}";
            if (initialized)
            {
                WriteToFile($"[EXCEPTION] {DateTime.Now:HH:mm:ss} - {message}\nStack Trace:\n{ex.StackTrace}\n");
            }
            Debug.LogError($"[CreatorSDK] {message}\n{ex.StackTrace}");
        }

        private static void WriteToFile(string message)
        {
            if (!initialized || string.IsNullOrEmpty(logFilePath))
            {
                Debug.LogError("[CreatorSDK] FileLogger not initialized!");
                return;
            }

            try
            {
                File.AppendAllText(logFilePath, message + "\n");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[CreatorSDK] Failed to write to log file: {ex.Message}");
            }
        }

        /// <summary>
        /// Gets the current log file path.
        /// </summary>
        public static string GetLogFilePath()
        {
            return logFilePath;
        }

        /// <summary>
        /// Opens the log file in the default editor.
        /// </summary>
        public static void OpenLogFile()
        {
            if (!string.IsNullOrEmpty(logFilePath) && File.Exists(logFilePath))
            {
                UnityEditor.EditorUtility.OpenWithDefaultApp(logFilePath);
            }
        }
    }
}