using UnityEngine;
using UnityEditor;
using System.IO;

namespace CreatorSDK.Editor
{
    /// <summary>
    /// Utility to export the Creator SDK as a distributable package
    /// </summary>
    public static class SDKExporter
    {
        public static string SDK_PATH => SDKPathUtility.GetRootPath();
        
        [MenuItem("Creator SDK/Open Documentation")]
        public static void OpenDocumentation()
        {
            string readmePath = SDKPathUtility.GetPath("README.md");
            
            if (File.Exists(readmePath))
            {
                var readme = AssetDatabase.LoadAssetAtPath<TextAsset>(readmePath);
                if (readme != null)
                {
                    AssetDatabase.OpenAsset(readme);
                    return;
                }
            }
            
            EditorUtility.DisplayDialog("Documentation", 
                $"README.md not found. Check {SDKPathUtility.GetPath("README.md")}", 
                "OK");
        }
    }
}
