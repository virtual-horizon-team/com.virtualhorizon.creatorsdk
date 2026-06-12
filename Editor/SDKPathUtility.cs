using UnityEditor;

namespace CreatorSDK.Editor
{
    /// <summary>
    /// Helper utility to dynamically resolve SDK paths depending on whether the SDK is used
    /// as a standard Assets folder or imported as a Unity Package Manager (UPM) package.
    /// </summary>
    public static class SDKPathUtility
    {
        public const string PackagePath = "Packages/com.virtualhorizon.creatorsdk";
        public const string AssetPath = "Assets/CreatorSDK";

        /// <summary>
        /// Resolves the root path of the SDK.
        /// </summary>
        public static string GetRootPath()
        {
            if (AssetDatabase.IsValidFolder(PackagePath))
                return PackagePath;
            return AssetPath;
        }

        /// <summary>
        /// Resolves a project-relative path to a specific SDK resource.
        /// </summary>
        public static string GetPath(string relativePath)
        {
            return $"{GetRootPath()}/{relativePath.TrimStart('/')}";
        }
    }
}
