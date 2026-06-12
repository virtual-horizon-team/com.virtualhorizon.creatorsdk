#if !UNITY_6000_0_OR_NEWER
#error Creator SDK requires Unity 6 (6000.x) or newer. Please upgrade your Unity version.
#endif

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using UnityEditor;
using UnityEngine.Rendering;

namespace CreatorSDK.Editor.Services
{
    /// <summary>
    /// Dedicated helper for extracting embedded materials and textures from
    /// imported model files (FBX, GLB, etc.) using Unity's standard
    /// <see cref="AssetDatabase.ExtractAsset"/> workflow.
    ///
    /// <para>
    /// SOLID: Single Responsibility — this class does ONE thing: extract
    /// sub-assets (materials, textures) from model imports into standalone
    /// files inside a designated temp folder. It knows nothing about UI,
    /// packages, or network operations.
    /// </para>
    ///
    /// <para>
    /// DESIGN DECISION — Why AssetDatabase.ExtractAsset over Graphics.Blit:
    /// The previous Graphics.Blit approach corrupted glTFast PBR color spaces
    /// and destroyed original texture compression. ExtractAsset preserves the
    /// importer's raw pixel data, color space, and compression settings exactly
    /// as the artist authored them.
    /// </para>
    ///
    /// <para>
    /// NOTE ON TEXTURE EXTRACTION: Unity's documentation states that
    /// AssetDatabase.ExtractAsset is "currently only available for materials
    /// embedded in model assets." However, in Unity 6 (6000.x), this method
    /// has been observed to work for Texture2D sub-assets as well. This
    /// implementation attempts ExtractAsset first for textures, and falls
    /// back to ModelImporter.ExtractTextures() if that fails, ensuring
    /// robustness across different Unity versions and import configurations.
    /// </para>
    /// </summary>
    public static class StandardAssetExtractor
    {
        // ─────────────────────────────────────────────────────────────────
        // Constants
        // ─────────────────────────────────────────────────────────────────

        /// <summary>
        /// File extension for extracted textures. Using .asset preserves
        /// the raw Texture2D asset exactly (including color space, compression,
        /// and importer settings) rather than forcing a re-import through PNG.
        /// </summary>
        private const string TEXTURE_EXTENSION = ".asset";

        /// <summary>
        /// File extension for extracted materials.
        /// </summary>
        private const string MATERIAL_EXTENSION = ".mat";

        // ─────────────────────────────────────────────────────────────────
        // Public API
        // ─────────────────────────────────────────────────────────────────

        /// <summary>
        /// Extracts all embedded materials and their dependent textures from
        /// the renderers of <paramref name="prefabInstance"/>.
        ///
        /// <para>
        /// Algorithm:
        /// 1. Iterate every Renderer.sharedMaterials on the prefab instance.
        /// 2. For each material: if it is a sub-asset (embedded), extract it
        ///    to a .mat file via <c>AssetDatabase.ExtractAsset</c>. If it is
        ///    already a standalone file, use <c>AssetDatabase.CopyAsset</c>.
        /// 3. For each extracted material, iterate its shader texture properties.
        ///    If a texture is a sub-asset, extract it to a .asset file.
        ///    If it is standalone, copy it.
        /// 4. Reassign extracted textures to extracted materials and mark dirty.
        /// </para>
        /// </summary>
        /// <param name="prefabInstance">The instantiated (unpacked) prefab to process.</param>
        /// <param name="materialsFolder">Project-relative path (e.g. "Assets/Temp/Materials") where .mat files will be written.</param>
        /// <param name="texturesFolder">Project-relative path (e.g. "Assets/Temp/Textures") where .asset files will be written.</param>
        /// <returns>A result object containing the material remap dictionary and extraction counters.</returns>
        public static ExtractionResult ExtractMaterialsAndTextures(
            GameObject prefabInstance,
            string materialsFolder,
            string texturesFolder)
        {
            if (prefabInstance == null)
                throw new ArgumentNullException(nameof(prefabInstance));
            if (string.IsNullOrEmpty(materialsFolder))
                throw new ArgumentException("Materials folder path is required.", nameof(materialsFolder));
            if (string.IsNullOrEmpty(texturesFolder))
                throw new ArgumentException("Textures folder path is required.", nameof(texturesFolder));

            var result = new ExtractionResult();

            // Deduplication dictionaries — ensure we never extract/copy the same
            // source asset twice, even if referenced by multiple renderers.
            var materialRemap = new Dictionary<Material, Material>();   // src material → extracted material
            var textureRemap = new Dictionary<Texture, Texture>();        // src texture → extracted texture
            var texturePathRemap = new Dictionary<string, string>();      // src asset path → dest asset path

            // Collect all unique materials from all renderers
            var allRenderers = prefabInstance.GetComponentsInChildren<Renderer>(true);
            var uniqueMaterials = new HashSet<Material>();
            foreach (var rend in allRenderers)
            {
                if (rend.sharedMaterials == null) continue;
                foreach (var mat in rend.sharedMaterials)
                {
                    if (mat != null)
                        uniqueMaterials.Add(mat);
                }
            }

            // ── Phase 1: Extract / Copy Materials ───────────────────────
            foreach (var srcMaterial in uniqueMaterials)
            {
                Material extractedMaterial = ExtractOrCopyMaterial(
                    srcMaterial,
                    materialsFolder,
                    result);

                if (extractedMaterial != null)
                    materialRemap[srcMaterial] = extractedMaterial;
            }

            // ── Phase 2: Extract / Copy Textures ────────────────────────
            // We walk the EXTRACTED materials (not the originals) so that
            // any shader property overrides made by the extractor are respected.
            foreach (var kvp in materialRemap.ToList()) // ToList because we may modify values
            {
                Material extractedMat = kvp.Value;
                if (extractedMat == null || extractedMat.shader == null)
                    continue;

                Shader shader = extractedMat.shader;
                int propCount = shader.GetPropertyCount();

                for (int pi = 0; pi < propCount; pi++)
                {
                    if (shader.GetPropertyType(pi) != ShaderPropertyType.Texture)
                        continue;

                    string propName = shader.GetPropertyName(pi);
                    Texture srcTexture = extractedMat.GetTexture(propName);
                    if (srcTexture == null)
                        continue;

                    // Already remapped this exact Texture instance? Reuse it.
                    if (textureRemap.TryGetValue(srcTexture, out Texture cachedTexture))
                    {
                        extractedMat.SetTexture(propName, cachedTexture);
                        continue;
                    }

                    string srcTexPath = AssetDatabase.GetAssetPath(srcTexture);

                    // Skip textures that are already inside the temp folder
                    // (e.g. from a previous extraction step or user-provided)
                    if (!string.IsNullOrEmpty(srcTexPath) && 
                        (srcTexPath.StartsWith(materialsFolder) || srcTexPath.StartsWith(texturesFolder)))
                        continue;

                    // Skip built-in / packaged Unity textures (path doesn't start with Assets/)
                    bool insideAssets = !string.IsNullOrEmpty(srcTexPath) && srcTexPath.StartsWith("Assets/");

                    Texture extractedTexture = null;

                    if (insideAssets && !AssetDatabase.IsSubAsset(srcTexture))
                    {
                        // CASE A: Standalone texture file → CopyAsset
                        extractedTexture = CopyStandaloneTexture(
                            srcTexture,
                            srcTexPath,
                            texturesFolder,
                            texturePathRemap,
                            result);
                    }
                    else if (insideAssets && AssetDatabase.IsSubAsset(srcTexture))
                    {
                        // CASE B: Embedded sub-asset (inside FBX/GLB) → ExtractAsset
                        extractedTexture = ExtractSubTexture(
                            srcTexture,
                            srcTexPath,
                            texturesFolder,
                            result);
                    }
                    else
                    {
                        // CASE C: Generated texture with no file path, or built-in texture
                        // These cannot be extracted; log a warning.
                        FileLogger.LogWarning($"Texture '{srcTexture.name}' (property: {propName}) has no valid asset path ('{srcTexPath}'). Skipping — material may render without it.");
                    }

                    if (extractedTexture != null)
                    {
                        extractedMat.SetTexture(propName, extractedTexture);
                        textureRemap[srcTexture] = extractedTexture;
                    }
                }

                // Persist material changes so the .mat file on disk references
                // the extracted textures, not the originals.
                EditorUtility.SetDirty(extractedMat);
            }

            // Save all modified assets to disk before returning
            AssetDatabase.SaveAssets();

            result.MaterialMap.Clear();
            foreach (var kvp in materialRemap)
                result.MaterialMap[kvp.Key] = kvp.Value;

            return result;
        }

        // ─────────────────────────────────────────────────────────────────
        // Material extraction / copying
        // ─────────────────────────────────────────────────────────────────

        /// <summary>
        /// Extracts or copies a single material to the destination folder.
        /// </summary>
        private static Material ExtractOrCopyMaterial(
            Material srcMaterial,
            string materialsFolder,
            ExtractionResult result)
        {
            string srcPath = AssetDatabase.GetAssetPath(srcMaterial);
            string safeName = SanitizeFileName(srcMaterial.name);
            string destPath = $"{materialsFolder}/{Guid.NewGuid()}_{safeName}{MATERIAL_EXTENSION}";

            Material destMaterial = null;

            if (AssetDatabase.IsSubAsset(srcMaterial))
            {
                string error = AssetDatabase.ExtractAsset(srcMaterial, destPath);
                if (string.IsNullOrEmpty(error))
                {
                    destMaterial = AssetDatabase.LoadAssetAtPath<Material>(destPath);
                    if (destMaterial != null) result.ExtractedMaterialCount++;
                }
            }
            else if (!string.IsNullOrEmpty(srcPath) && srcPath.EndsWith(MATERIAL_EXTENSION))
            {
                if (AssetDatabase.CopyAsset(srcPath, destPath))
                {
                    destMaterial = AssetDatabase.LoadAssetAtPath<Material>(destPath);
                    if (destMaterial != null) result.CopiedMaterialCount++;
                }
            }

            // في حال فشل الاستخراج، نقوم بإنشاء المادة
            if (destMaterial == null)
            {
                destMaterial = new Material(srcMaterial.shader);
                destMaterial.name = srcMaterial.name;
                AssetDatabase.CreateAsset(destMaterial, destPath);
            }

            // === الإصلاح الجذري ===
            if (destMaterial != null && srcMaterial != null)
            {
                destMaterial.CopyPropertiesFromMaterial(srcMaterial);
                float surface = destMaterial.HasProperty("_Surface") ? destMaterial.GetFloat("_Surface") : -1;
                float zwrite  = destMaterial.HasProperty("_ZWrite")  ? destMaterial.GetFloat("_ZWrite")  : -1;
                float alpha   = destMaterial.HasProperty("_AlphaClip") ? destMaterial.GetFloat("_AlphaClip") : -1;
                float srcSurface = srcMaterial.HasProperty("_Surface") ? srcMaterial.GetFloat("_Surface") : -1;

                FileLogger.Log($"[CreatorSDK] MAT '{srcMaterial.name}' | " +
                        $"src_Surface:{srcSurface} → dest_Surface:{surface} | " +
                        $"ZWrite:{zwrite} | AlphaClip:{alpha} | " +
                        $"RenderQueue:{destMaterial.renderQueue} | " +
                        $"Shader:{destMaterial.shader.name}");
                
                // Legacy keywords
                destMaterial.shaderKeywords = srcMaterial.shaderKeywords;
                
                // ✅ NEW: Local keywords (URP/HDRP Unity 6)
                foreach (var keyword in srcMaterial.shader.keywordSpace.keywords)
                {
                    if (srcMaterial.IsKeywordEnabled(keyword))
                        destMaterial.EnableKeyword(keyword);
                    else
                        destMaterial.DisableKeyword(keyword);
                }
                
                destMaterial.renderQueue = srcMaterial.renderQueue;
                destMaterial.enableInstancing = srcMaterial.enableInstancing;
                destMaterial.globalIlluminationFlags = srcMaterial.globalIlluminationFlags;
                destMaterial.doubleSidedGI = srcMaterial.doubleSidedGI;
                
                EditorUtility.SetDirty(destMaterial);
                AssetDatabase.SaveAssets();
            }

            return destMaterial;
        }

        // ─────────────────────────────────────────────────────────────────
        // Texture copying (standalone files)
        // ─────────────────────────────────────────────────────────────────

        /// <summary>
        /// Copies a standalone texture file to the destination folder using
        /// <see cref="AssetDatabase.CopyAsset"/>, preserving importer settings.
        /// </summary>
        private static Texture CopyStandaloneTexture(
            Texture srcTexture,
            string srcTexPath,
            string texturesFolder,
            Dictionary<string, string> pathRemap,
            ExtractionResult result)
        {
            if (pathRemap.TryGetValue(srcTexPath, out string existingDest))
            {
                return AssetDatabase.LoadAssetAtPath<Texture>(existingDest);
            }

            string fileName = Path.GetFileName(srcTexPath);
            string destPath = $"{texturesFolder}/{Guid.NewGuid()}_{fileName}";

            if (!AssetDatabase.CopyAsset(srcTexPath, destPath))
            {
                FileLogger.LogWarning($"CopyAsset FAILED for texture '{srcTexPath}'. Skipping.");
                return null;
            }

            pathRemap[srcTexPath] = destPath;
            result.CopiedTextureCount++;

            // Preserve color space / sRGB setting from the original importer
            var srcImporter = AssetImporter.GetAtPath(srcTexPath) as TextureImporter;
            var destImporter = AssetImporter.GetAtPath(destPath) as TextureImporter;
            if (srcImporter != null && destImporter != null)
            {
                if (destImporter.sRGBTexture != srcImporter.sRGBTexture)
                {
                    destImporter.sRGBTexture = srcImporter.sRGBTexture;
                    destImporter.SaveAndReimport();
                    FileLogger.Log($"[CreatorSDK] Fixed sRGB setting for copied texture: {destPath}");
                }
            }

            FileLogger.Log($"Copied texture: {srcTexPath} → {destPath}");
            return AssetDatabase.LoadAssetAtPath<Texture>(destPath);
        }

        // ─────────────────────────────────────────────────────────────────
        // Texture extraction (sub-assets via AssetDatabase.ExtractAsset)
        // ─────────────────────────────────────────────────────────────────

        /// <summary>
        /// Extracts an embedded (sub-asset) texture using
        /// <see cref="AssetDatabase.ExtractAsset"/>.
        /// The extracted file uses the .asset extension to preserve raw
        /// pixel data and color space exactly.
        /// </summary>
        private static Texture ExtractSubTexture(
            Texture srcTexture,
            string srcTexPath,
            string texturesFolder,
            ExtractionResult result)
        {
            string safeName = SanitizeFileName(srcTexture.name);
            if (string.IsNullOrEmpty(safeName))
                safeName = "embedded_texture";

            string destPath = $"{texturesFolder}/{Guid.NewGuid()}_{safeName}{TEXTURE_EXTENSION}";

            // AssetDatabase.ExtractAsset works on the source asset path combined
            // with the sub-asset identifier. For textures inside model files,
            // srcTexPath points to the model file and srcTexture is the sub-asset.
            string error = AssetDatabase.ExtractAsset(srcTexture, destPath);

            if (!string.IsNullOrEmpty(error))
            {
                FileLogger.LogWarning($"AssetDatabase.ExtractAsset FAILED for texture '{srcTexture.name}' (path: '{srcTexPath}'): {error}. Attempting fallback extraction.");
                return ExtractSubTextureFallback(srcTexture, srcTexPath, texturesFolder, result);
            }

            // Force reimport so the extracted texture is fully registered
            AssetDatabase.ImportAsset(destPath,
                ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport);

            Texture extracted = AssetDatabase.LoadAssetAtPath<Texture>(destPath);
            if (extracted != null)
            {
                result.ExtractedTextureCount++;
                FileLogger.Log($"Extracted embedded texture '{srcTexture.name}' → {destPath}");
            }
            else
            {
                FileLogger.LogWarning($"[CreatorSDK] ExtractAsset reported success but could not load extracted texture at '{destPath}'. Attempting fallback.");
                return ExtractSubTextureFallback(srcTexture, srcTexPath, texturesFolder, result);
            }

            return extracted;
        }

        /// <summary>
        /// Fallback extraction for textures when AssetDatabase.ExtractAsset fails.
        /// Uses ModelImporter.ExtractTextures() if the texture belongs to a model
        /// asset, otherwise falls back to a color-space-preserving blit.
        /// </summary>
        private static Texture ExtractSubTextureFallback(
            Texture srcTexture,
            string srcTexPath,
            string texturesFolder,
            ExtractionResult result)
        {
            // Try ModelImporter.ExtractTextures for model-embedded textures
            if (!string.IsNullOrEmpty(srcTexPath))
            {
                string modelPath = srcTexPath;
                // If srcTexPath is the model file itself (which is typical for sub-assets),
                // use ModelImporter to extract all textures from it.
                var modelImporter = AssetImporter.GetAtPath(modelPath) as ModelImporter;
                if (modelImporter != null)
                {
                    try
                    {
                        // ExtractTextures will extract ALL textures from the model to the specified folder
                        modelImporter.ExtractTextures(texturesFolder);
                        AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);

                        // Find the extracted texture by name
                        string[] textureGuids = AssetDatabase.FindAssets($"t:Texture {srcTexture.name}", new[] { texturesFolder });
                        foreach (string guid in textureGuids)
                        {
                            string path = AssetDatabase.GUIDToAssetPath(guid);
                            Texture tex = AssetDatabase.LoadAssetAtPath<Texture>(path);
                            if (tex != null && tex.name == srcTexture.name)
                            {
                                result.ExtractedTextureCount++;
                                FileLogger.Log($"[CreatorSDK] Extracted texture via ModelImporter fallback: '{srcTexture.name}' → {path}");
                                return tex;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        FileLogger.LogWarning($"ModelImporter.ExtractTextures fallback failed: {ex.Message}");
                    }
                }
            }

            // Final fallback: color-space-preserving blit
            // This is only used when all other methods fail. It attempts to
            // preserve the color space by reading the texture's import settings.
            return ExtractTextureViaBlit(srcTexture, texturesFolder, result);
        }

        /// <summary>
        /// Last-resort texture extraction using a temporary RenderTexture readback.
        /// Preserves linear/sRGB color space based on the original texture's
        /// import settings or property name heuristics.
        /// </summary>
        private static Texture ExtractTextureViaBlit(
            Texture srcTexture,
            string texturesFolder,
            ExtractionResult result)
        {
            var tex2D = srcTexture as Texture2D;
            if (tex2D == null)
            {
                FileLogger.LogWarning($"Cannot extract non-Texture2D '{srcTexture.name}' via blit fallback.");
                return null;
            }

            int w = tex2D.width;
            int h = tex2D.height;
            if (w == 0 || h == 0)
            {
                FileLogger.LogWarning($"Texture '{srcTexture.name}' has zero dimensions. Skipping.");
                return null;
            }

            // Determine color space from original import settings
            string srcPath = AssetDatabase.GetAssetPath(srcTexture);
            bool isLinear = false;
            if (!string.IsNullOrEmpty(srcPath))
            {
                var ti = AssetImporter.GetAtPath(srcPath) as TextureImporter;
                if (ti != null)
                    isLinear = !ti.sRGBTexture;
            }

            RenderTextureReadWrite rtReadWrite = isLinear
                ? RenderTextureReadWrite.Linear
                : RenderTextureReadWrite.sRGB;

            RenderTexture prevActive = RenderTexture.active;
            RenderTexture rt = RenderTexture.GetTemporary(
                w, h, 0, RenderTextureFormat.ARGB32, rtReadWrite);

            try
            {
                Graphics.Blit(tex2D, rt);
                RenderTexture.active = rt;

                Texture2D readable = new Texture2D(w, h, TextureFormat.ARGB32, false, isLinear);
                readable.ReadPixels(new Rect(0, 0, w, h), 0, 0);
                readable.Apply();

                byte[] pngBytes = readable.EncodeToPNG();
                UnityEngine.Object.DestroyImmediate(readable);

                string safeName = SanitizeFileName(tex2D.name);
                if (string.IsNullOrEmpty(safeName)) safeName = "embedded_texture";

                string destPath = $"{texturesFolder}/{Guid.NewGuid()}_{safeName}.png";
                string projectRoot = Path.GetDirectoryName(Application.dataPath);
                string destDisk = Path.Combine(projectRoot,
                    destPath.Replace("/", Path.DirectorySeparatorChar.ToString()));

                File.WriteAllBytes(destDisk, pngBytes);
                AssetDatabase.ImportAsset(destPath,
                    ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport);

                var destImporter = AssetImporter.GetAtPath(destPath) as TextureImporter;
                if (destImporter != null)
                {
                    destImporter.sRGBTexture = !isLinear;
                    destImporter.SaveAndReimport();
                }

                Texture extracted = AssetDatabase.LoadAssetAtPath<Texture>(destPath);
                if (extracted != null)
                {
                    result.ExtractedTextureCount++;
                    FileLogger.Log($"Extracted texture via blit fallback (color-space preserved): '{tex2D.name}' → {destPath}");
                }
                return extracted;
            }
            catch (Exception ex)
            {
                FileLogger.LogWarning($"Blit fallback failed for texture '{tex2D.name}': {ex.Message}");
                return null;
            }
            finally
            {
                RenderTexture.active = prevActive;
                RenderTexture.ReleaseTemporary(rt);
            }
        }

        // ─────────────────────────────────────────────────────────────────
        // Utility
        // ─────────────────────────────────────────────────────────────────

        /// <summary>
        /// Removes characters that are invalid for file names and replaces
        /// spaces with underscores.
        /// </summary>
        private static string SanitizeFileName(string name)
        {
            if (string.IsNullOrEmpty(name))
                return "unnamed";

            foreach (char c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');

            return name.Replace(" ", "_").Replace("#", "_");
        }
    }
}