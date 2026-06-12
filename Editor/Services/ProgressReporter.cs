#if !UNITY_6000_0_OR_NEWER
#error Creator SDK requires Unity 6 (6000.x) or newer. Please upgrade your Unity version.
#endif

using System;
using UnityEditor;

namespace CreatorSDK.Editor.Services
{
    /// <summary>
    /// Handles progress bar display during long-running operations.
    /// </summary>
    public class ProgressReporter : IProgress<float>, IDisposable
    {
        private readonly string _title;
        private readonly string _info;
        private bool _isActive;

        public ProgressReporter(string title, string info = "")
        {
            _title = title;
            _info = info;
            _isActive = true;
        }

        public void Report(float value)
        {
            if (!_isActive) return;

            // Clamp value between 0 and 1
            value = Math.Clamp(value, 0f, 1f);

            string info = string.IsNullOrEmpty(_info) ? $"{value:P0}" : $"{_info} - {value:P0}";
            EditorUtility.DisplayProgressBar(_title, info, value);
        }

        public void UpdateInfo(string newInfo)
        {
            if (!_isActive) return;
            EditorUtility.DisplayProgressBar(_title, newInfo, 0f);
        }

        public void Dispose()
        {
            if (_isActive)
            {
                EditorUtility.ClearProgressBar();
                _isActive = false;
            }
        }
    }
}