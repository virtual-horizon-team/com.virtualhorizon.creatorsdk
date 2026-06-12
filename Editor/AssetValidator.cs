using UnityEngine;
using UnityEditor;
using System.Collections.Generic;
using System.Text.RegularExpressions; 
using System.IO;
using System.Linq;

namespace CreatorSDK.Editor
{
    /// <summary>
    /// Comprehensive validation system for prefabs before export
    /// </summary>
    public static class AssetValidator
    {
        public static ValidationResult ValidatePrefab(GameObject prefab)
        {
            var result = new ValidationResult();
            
            if (prefab == null)
            {
                result.AddError("Prefab is null");
                return result;
            }
            
            string prefabPath = AssetDatabase.GetAssetPath(prefab);
            
            // Basic checks
            ValidateBasicRequirements(prefab, prefabPath, result);
            
            // Component checks
            ValidateComponents(prefab, result);
            
            // Mesh checks
            ValidateMeshes(prefab, result);
            
            // Material and texture checks
            ValidateMaterials(prefab, result);
            
            // ScenarioBridge checks
            ValidateScenarioBridge(prefab, result);
            
            return result;
        }

        private static void ValidateBasicRequirements(GameObject prefab, string prefabPath, ValidationResult result)
        {
            // Check if it's a prefab
            if (!prefabPath.EndsWith(".prefab"))
            {
                result.AddError("Selected object is not a prefab asset.");
            }
            
            // Check for prefab variants
            #if UNITY_2018_3_OR_NEWER
            var prefabInstance = PrefabUtility.GetCorrespondingObjectFromSource(prefab);
            if (prefabInstance != null && PrefabUtility.IsPartOfVariantPrefab(prefab))
            {
                result.AddError("Prefab variants are not supported for export. Please export the base prefab instead, or unpack the variant first.");
            }
            #endif
            
            // Check for nested prefabs
            var nestedPrefabs = prefab.GetComponentsInChildren<Transform>(true)
                .Where(t => PrefabUtility.IsAnyPrefabInstanceRoot(t.gameObject) && t != prefab.transform)
                .ToArray();
            
            if (nestedPrefabs.Length > 0)
            {
                result.AddWarning($"Prefab contains {nestedPrefabs.Length} nested prefab(s). These will be included in the export but may cause issues if the nested prefabs are not also exported.");
            }
            
            // Check prefab name
            if (string.IsNullOrWhiteSpace(prefab.name))
            {
                result.AddError("Prefab has no name.");
            }
            
            // Check for special characters in name
            if (prefab.name.Any(c => !char.IsLetterOrDigit(c) && c != '_' && c != '-' && c != ' '))
            {
                result.AddWarning("Prefab name contains special characters. Consider using only letters, numbers, underscores, and spaces.");
            }
        }

        private static void ValidateComponents(GameObject prefab, ValidationResult result)
        {
            var allComponents = prefab.GetComponentsInChildren<Component>(true);
            
            // Check for missing scripts
            int missingScripts = allComponents.Count(c => c == null);
            if (missingScripts > 0)
            {
                result.AddError($"Prefab has {missingScripts} missing script reference(s). These must be fixed before export.");
            }
            
            // Check for non-SDK MonoBehaviours
            var monoBehaviours = prefab.GetComponentsInChildren<MonoBehaviour>(true);
            foreach (var mb in monoBehaviours)
            {
                if (mb == null) continue;
                
                var type = mb.GetType();
                string ns = type.Namespace ?? "";
                
                // Allow SDK components
                if (ns.StartsWith("CreatorSDK")) continue;
                if (type == typeof(ScenarioBridge)) continue;
                
                // Allow Unity built-ins
                if (ns.StartsWith("UnityEngine")) continue;
                if (ns.StartsWith("TMPro")) continue;
                
                result.AddWarning($"Custom script '{type.Name}' detected. It will NOT execute at runtime. Use Lua scripts via ScenarioBridge instead.");
            }
            
            // Check for risky components
            var rigidbodies = prefab.GetComponentsInChildren<Rigidbody>(true);
            if (rigidbodies.Length > 0)
            {
                result.AddInfo($"Prefab contains {rigidbodies.Length} Rigidbody component(s). Physics will be active at runtime.");
            }
            
            var colliders = prefab.GetComponentsInChildren<Collider>(true);
            if (colliders.Length == 0)
            {
                result.AddWarning("Prefab has no Colliders. It won't be interactable or detect triggers.");
            }
            
            // Check for Audio sources
            var audioSources = prefab.GetComponentsInChildren<AudioSource>(true);
            if (audioSources.Length > 0)
            {
                result.AddInfo($"Prefab contains {audioSources.Length} AudioSource component(s).");
            }
            
            // Check for Particle Systems
            var particles = prefab.GetComponentsInChildren<ParticleSystem>(true);
            if (particles.Length > 0)
            {
                result.AddInfo($"Prefab contains {particles.Length} ParticleSystem component(s).");
            }
            
            // Check for Animator
            var animators = prefab.GetComponentsInChildren<Animator>(true);
            if (animators.Length > 0)
            {
                foreach (var animator in animators)
                {
                    if (animator.runtimeAnimatorController == null)
                    {
                        result.AddWarning($"Animator on '{animator.gameObject.name}' has no controller assigned.");
                    }
                }
            }
        }

        private static void ValidateMeshes(GameObject prefab, ValidationResult result)
        {
            var meshFilters = prefab.GetComponentsInChildren<MeshFilter>(true);
            var skinnedMeshes = prefab.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            
            int totalVertices = 0;
            int totalTriangles = 0;
            
            foreach (var mf in meshFilters)
            {
                if (mf.sharedMesh != null)
                {
                    totalVertices += mf.sharedMesh.vertexCount;
                    totalTriangles += mf.sharedMesh.triangles.Length / 3;
                }
            }
            
            foreach (var smr in skinnedMeshes)
            {
                if (smr.sharedMesh != null)
                {
                    totalVertices += smr.sharedMesh.vertexCount;
                    totalTriangles += smr.sharedMesh.triangles.Length / 3;
                }
            }
            
            // Polygon limits
            if (totalVertices > 100000)
            {
                result.AddError($"Mesh is too complex: {totalVertices:N0} vertices (max: 100,000). Please reduce polygon count.");
            }
            else if (totalVertices > 50000)
            {
                result.AddWarning($"High polygon count: {totalVertices:N0} vertices. Consider optimizing for mobile devices.");
            }
            else if (totalVertices > 10000)
            {
                result.AddInfo($"Mesh complexity: {totalVertices:N0} vertices, {totalTriangles:N0} triangles.");
            }
            
            // Check for missing meshes
            foreach (var mf in meshFilters)
            {
                if (mf.sharedMesh == null)
                {
                    result.AddWarning($"MeshFilter on '{mf.gameObject.name}' has no mesh assigned.");
                }
            }
        }

        private static void ValidateMaterials(GameObject prefab, ValidationResult result)
        {
            var renderers = prefab.GetComponentsInChildren<Renderer>(true);
            var checkedTextures = new HashSet<Texture>();
            
            foreach (var renderer in renderers)
            {
                if (renderer.sharedMaterials == null) continue;
                
                foreach (var mat in renderer.sharedMaterials)
                {
                    if (mat == null)
                    {
                        result.AddWarning($"Renderer on '{renderer.gameObject.name}' has a missing material.");
                        continue;
                    }
                    
                    // Check shader compatibility
                    if (mat.shader != null)
                    {
                        string shaderName = mat.shader.name;
                        
                        // Check for potentially problematic shaders
                        if (shaderName.Contains("Legacy") || shaderName.Contains("Diffuse"))
                        {
                            result.AddInfo($"Material '{mat.name}' uses legacy shader '{shaderName}'. Consider using URP/Standard shaders.");
                        }
                    }
                    
                    // Check textures
                    CheckMaterialTextures(mat, checkedTextures, result);
                }
            }
        }

        private static void CheckMaterialTextures(Material mat, HashSet<Texture> checkedTextures, ValidationResult result)
        {
            // Get all texture properties (Unity 6+ API)
            var shader = mat.shader;
            int propertyCount = shader.GetPropertyCount();
            
            for (int i = 0; i < propertyCount; i++)
            {
                if (shader.GetPropertyType(i) == UnityEngine.Rendering.ShaderPropertyType.Texture)
                {
                    string propName = shader.GetPropertyName(i);
                    Texture tex = mat.GetTexture(propName);
                    
                    if (tex != null && !checkedTextures.Contains(tex))
                    {
                        checkedTextures.Add(tex);
                        
                        // Check texture size
                        if (tex.width > 4096 || tex.height > 4096)
                        {
                            result.AddError($"Texture '{tex.name}' is {tex.width}x{tex.height} (max: 4096x4096). Please reduce size.");
                        }
                        else if (tex.width > 2048 || tex.height > 2048)
                        {
                            result.AddWarning($"Texture '{tex.name}' is {tex.width}x{tex.height}. Consider reducing for mobile.");
                        }
                        
                        // Check for non-power-of-2
                        if (!IsPowerOfTwo(tex.width) || !IsPowerOfTwo(tex.height))
                        {
                            result.AddInfo($"Texture '{tex.name}' ({tex.width}x{tex.height}) is not power-of-2. This may affect performance on some platforms.");
                        }
                    }
                }
            }
        }

        private static void ValidateScenarioBridge(GameObject prefab, ValidationResult result)
        {
            var bridges = prefab.GetComponentsInChildren<ScenarioBridge>(true);
            
            if (bridges.Length == 0)
            {
                result.AddInfo("No ScenarioBridge component found. Object will have no runtime behavior. Add one if you want Lua scripting.");
                return;
            }
            
            if (bridges.Length > 1)
            {
                result.AddInfo($"✓ Prefab contains {bridges.Length} ScenarioBridge components. All scripts will be exported and will run at runtime.");
            }
            
            // Validate all bridges for Lua scripts
            foreach (var bridge in bridges)
            {
                ValidateSingleScenarioBridge(bridge, result);
            }
            
            // Check interaction settings on the root bridge (or any interactable bridge)
            var rootBridge = bridges[0];
            if (rootBridge.isInteractable)
            {
                var colliders = prefab.GetComponentsInChildren<Collider>(true);
                if (colliders.Length == 0)
                {
                    result.AddWarning("Object is marked as interactable but has no Colliders. Users won't be able to click it.");
                }
            }
        }

        private static void ValidateSingleScenarioBridge(ScenarioBridge bridge, ValidationResult result)
        {
            // Check Lua script
            if (bridge.luaScript == null)
            {
                result.AddWarning($"ScenarioBridge on '{bridge.gameObject.name}' has no Lua script assigned. Object will have no custom behavior.");
            }
            else
            {
                // Validate Lua script content
                string luaContent = bridge.luaScript.text;
                
                if (string.IsNullOrWhiteSpace(luaContent))
                {
                    result.AddWarning($"Lua script on '{bridge.gameObject.name}' is empty.");
                }
                else
                {
                    // Check for common functions
                    if (!luaContent.Contains("function"))
                    {
                        result.AddWarning($"Lua script on '{bridge.gameObject.name}' contains no functions. It won't do anything.");
                    }
                    
                    // Check for prohibited APIs using Regex (Word Boundaries)
                    if (Regex.IsMatch(luaContent, @"\b(os|io)\."))
                    {
                        result.AddError($"Lua script on '{bridge.gameObject.name}' contains forbidden API calls (os.* or io.*). These will be blocked at runtime.");
                    }

                    // Check for prohibited file operations
                    if (Regex.IsMatch(luaContent, @"\b(require|dofile)\s*\("))
                    {
                        result.AddError($"Lua script on '{bridge.gameObject.name}' contains forbidden file operations (require/dofile). External modules are not supported.");
                    }
                }
            }
            
            // Check exposed properties
            if (bridge.exposedProperties != null && bridge.exposedProperties.Count > 0)
            {
                foreach (var prop in bridge.exposedProperties)
                {
                    if (string.IsNullOrWhiteSpace(prop.propertyName))
                    {
                        result.AddWarning($"An exposed property on '{bridge.gameObject.name}' has no name defined.");
                    }
                    
                    if (string.IsNullOrWhiteSpace(prop.displayName))
                    {
                        result.AddInfo($"Property '{prop.propertyName}' on '{bridge.gameObject.name}' has no display name. It will show the property name in UI.");
                    }
                }
                
                result.AddInfo($"Object '{bridge.gameObject.name}' has {bridge.exposedProperties.Count} exposed properties for runtime configuration.");
            }
        }

        private static bool IsPowerOfTwo(int value)
        {
            return value > 0 && (value & (value - 1)) == 0;
        }

        private static Bounds CalculateBounds(GameObject obj)
        {
            var renderers = obj.GetComponentsInChildren<Renderer>(true);
            if (renderers.Length == 0) return new Bounds(obj.transform.position, Vector3.zero);

            Bounds bounds = renderers[0].bounds;
            for (int i = 1; i < renderers.Length; i++)
            {
                bounds.Encapsulate(renderers[i].bounds);
            }
            return bounds;
        }

    #region Validation Result Classes

    public class ValidationResult
    {
        public List<ValidationMessage> Messages { get; } = new List<ValidationMessage>();
        
        public bool HasErrors => Messages.Any(m => m.Severity == ValidationSeverity.Error);
        public bool HasWarnings => Messages.Any(m => m.Severity == ValidationSeverity.Warning);
        public bool IsValid => !HasErrors;
        
        public int ErrorCount => Messages.Count(m => m.Severity == ValidationSeverity.Error);
        public int WarningCount => Messages.Count(m => m.Severity == ValidationSeverity.Warning);
        public int InfoCount => Messages.Count(m => m.Severity == ValidationSeverity.Info);

        public void AddError(string message)
        {
            Messages.Add(new ValidationMessage(ValidationSeverity.Error, message));
        }

        public void AddWarning(string message)
        {
            Messages.Add(new ValidationMessage(ValidationSeverity.Warning, message));
        }

        public void AddInfo(string message)
        {
            Messages.Add(new ValidationMessage(ValidationSeverity.Info, message));
        }

        public string GetSummary()
        {
            if (Messages.Count == 0)
                return "Validation passed with no issues.";
            
            return $"Validation complete: {ErrorCount} error(s), {WarningCount} warning(s), {InfoCount} info message(s).";
        }

        public string GetFullReport()
        {
            var sb = new System.Text.StringBuilder();
            
            sb.AppendLine(GetSummary());
            sb.AppendLine();
            
            foreach (var msg in Messages.OrderByDescending(m => m.Severity))
            {
                string prefix = msg.Severity switch
                {
                    ValidationSeverity.Error => "[ERROR]",
                    ValidationSeverity.Warning => "[WARN]",
                    ValidationSeverity.Info => "[INFO]",
                    _ => ""
                };
                
                sb.AppendLine($"{prefix} {msg.Message}");
            }
            
            return sb.ToString();
        }
    }
}

public class ValidationMessage
    {
        public ValidationSeverity Severity { get; }
        public string Message { get; }

        public ValidationMessage(ValidationSeverity severity, string message)
        {
            Severity = severity;
            Message = message;
        }
    }

    public enum ValidationSeverity
    {
        Info = 0,
        Warning = 1,
        Error = 2
    }

    #endregion
}
