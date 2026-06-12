#if !UNITY_6000_0_OR_NEWER
#error Creator SDK requires Unity 6 (6000.x) or newer. Please upgrade your Unity version.
#endif

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;
using UnityEditor;
using UnityEngine.Rendering;
using CreatorSDK.Editor.Services;

namespace CreatorSDK.Editor
{
    /// <summary>
    /// Manages the core export workflow: temporary folder creation, prefab
    /// instantiation, mesh cloning, material/texture extraction, Lua script
    /// collection, metadata JSON generation, and .unitypackage creation.
    ///
    /// <para>
    /// SOLID: Single Responsibility — this class owns EVERYTHING related to
    /// the export pipeline. It does NOT render UI, handle user input, or
    /// perform network operations. It receives an <see cref="UploadItem"/>
    /// and produces a .unitypackage file on disk.
    /// </para>
    /// </summary>
    public static class AssetExporter
    {
        // ─────────────────────────────────────────────────────────────────
        // Constants
        // ─────────────────────────────────────────────────────────────────

        private const string EXPORT_FOLDER = "ExportedPackages";
        private const string MATERIALS_SUBFOLDER = "Materials";
        private const string MESHES_SUBFOLDER = "Meshes";
        private const string TEXTURES_SUBFOLDER = "Textures";

        // ─────────────────────────────────────────────────────────────────
        // Public API
        // ─────────────────────────────────────────────────────────────────

        /// <summary>
        /// Exports a single <see cref="UploadItem"/> to a .unitypackage file.
        /// This is the main entry point for the export pipeline.
        /// </summary>
        /// <param name="item">The item to export. Must have a valid prefab.</param>
        /// <param name="includeScripts">Whether to include Lua scripts in the export.</param>
        /// <param name="creatorId">The creator identifier written into metadata.</param>
        /// <param name="validateBeforeExport">If true, runs validation before export.</param>
        /// <returns>An <see cref="ExportResult"/> describing success or failure.</returns>
        public static ExportResult ExportItem(
            UploadItem item,
            bool includeScripts,
            string creatorId,
            bool validateBeforeExport)
        {
            if (!Application.unityVersion.StartsWith("6000.3.13f"))
            {
                string errorMsg = $"Incompatible Unity Version: You are using Unity version {Application.unityVersion}. This SDK version only supports Unity version 6000.3.13f.";
                EditorUtility.DisplayDialog("Incompatible Unity Version", errorMsg, "OK");
                return ExportResult.Failed(errorMsg);
            }

            if (item?.prefab == null)
            {
                FileLogger.LogError("No prefab assigned to upload item.");
                return ExportResult.Failed("No prefab assigned to upload item.");
            }

            // Initialize logger
            string logFilePath = Path.Combine(Application.dataPath, $"../{EXPORT_FOLDER}/export_log_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
            FileLogger.Initialize(logFilePath);

            FileLogger.Log($"Starting export for item: '{item.displayName}' (GUID: {item.generatedGuid})");
            FileLogger.Log($"Include scripts: {includeScripts}, Creator ID: {creatorId}, Validate: {validateBeforeExport}");

            item.status = UploadStatus.Exporting;
            item.statusMessage = "";

            string assetGuid = Guid.NewGuid().ToString();
            item.generatedGuid = assetGuid;

            FileLogger.Log($"Generated new GUID: {assetGuid}");

            // Build temp folder paths (project-relative and absolute)
            string tempFolder = $"Assets/_TempExport_{assetGuid}";
            string meshesFolder = $"{tempFolder}/{MESHES_SUBFOLDER}";
            string materialsFolder = $"{tempFolder}/{MATERIALS_SUBFOLDER}";
            string texturesFolder = $"{tempFolder}/{TEXTURES_SUBFOLDER}";

            string projectRoot = Path.GetDirectoryName(Application.dataPath);
            string tempFolderDisk = ToAbsolutePath(tempFolder, projectRoot);
            string meshesFolderDisk = ToAbsolutePath(meshesFolder, projectRoot);
            string materialsFolderDisk = ToAbsolutePath(materialsFolder, projectRoot);
            string texturesFolderDisk = ToAbsolutePath(texturesFolder, projectRoot);

            FileLogger.Log($"Temp folder: {tempFolder}");
            FileLogger.Log($"Project root: {projectRoot}");

            try
            {
                // ── 1. Create temp folder structure ─────────────────────
                FileLogger.Log("Step 1: Creating temp folder structure");
                Directory.CreateDirectory(meshesFolderDisk);
                Directory.CreateDirectory(materialsFolderDisk);
                Directory.CreateDirectory(texturesFolderDisk);
                AssetDatabase.Refresh(ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport);
                FileLogger.Log("Temp folders created and refreshed");

                string newPrefabPath = $"{tempFolder}/{assetGuid}.prefab";
                FileLogger.Log($"New prefab path: {newPrefabPath}");

                // ── 2. Instantiate and unpack prefab ────────────────────
                FileLogger.Log("Step 2: Instantiating and unpacking prefab");
                GameObject tempObj = PrefabUtility.InstantiatePrefab(item.prefab) as GameObject;
                if (tempObj == null)
                {
                    FileLogger.LogError("Failed to instantiate prefab");
                    throw new Exception("Failed to instantiate prefab");
                }
                PrefabUtility.UnpackPrefabInstance(tempObj, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
                FileLogger.Log("Prefab instantiated and unpacked");

                // ── 3. Clone meshes (handles non-readable GLB/FBX) ────────
                FileLogger.Log("Step 3: Cloning meshes");
                var meshCloneMap = CloneAllMeshes(tempObj, meshesFolder);
                FileLogger.Log($"Cloned {meshCloneMap.Count} unique meshes");

                // ── 4. Extract materials and textures ───────────────────
                FileLogger.Log("Step 4: Extracting materials and textures");
                // Delegate to StandardAssetExtractor (SRP: this class orchestrates,
                // the extractor performs the actual extraction).
                ExtractionResult extraction = StandardAssetExtractor.ExtractMaterialsAndTextures(
                    tempObj,
                    materialsFolder,
                    texturesFolder);

                FileLogger.Log($"Extraction completed - Copied mats: {extraction.CopiedMaterialCount}, Extracted mats: {extraction.ExtractedMaterialCount}, Copied tex: {extraction.CopiedTextureCount}, Extracted tex: {extraction.ExtractedTextureCount}");

                // Reassign extracted materials to renderers
                ApplyExtractedMaterials(tempObj, extraction.MaterialMap);
                FileLogger.Log("Extracted materials reassigned to renderers");

                // ── 5. Save prefab with cloned meshes and extracted materials ─
                FileLogger.Log("Step 5: Saving prefab with cloned meshes and extracted materials");
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh(ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport);

                PrefabUtility.SaveAsPrefabAsset(tempObj, newPrefabPath);
                GameObject.DestroyImmediate(tempObj);

                AssetDatabase.ImportAsset(newPrefabPath,
                    ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport);
                FileLogger.Log($"Prefab saved as: {newPrefabPath}");

                // ── 6. Collect Lua scripts ────────────────────────────────
                FileLogger.Log("Step 6: Collecting Lua scripts");
                var scriptInfos = new List<ScriptInfo>();
                var assetsToExport = new List<string> { newPrefabPath };
                var luaCopiedSrc = new Dictionary<string, string>();

                if (includeScripts)
                {
                    CollectLuaScripts(
                        item.prefab,
                        tempFolder,
                        assetGuid,
                        projectRoot,
                        luaCopiedSrc,
                        scriptInfos,
                        assetsToExport);
                    FileLogger.Log($"Collected {scriptInfos.Count} Lua scripts");
                }
                else
                {
                    FileLogger.Log("Script inclusion disabled");
                }

                // ── 7. Write metadata JSON ────────────────────────────────
                FileLogger.Log("Step 7: Writing metadata JSON");
                CreatorRenderPipeline pipeline = DetectRenderPipeline();
                string metaPath = WriteMetadata(
                    tempFolder,
                    assetGuid,
                    item,
                    creatorId,
                    pipeline,
                    scriptInfos,
                    projectRoot);
                assetsToExport.Add(metaPath);
                FileLogger.Log($"Metadata written to: {metaPath}, Pipeline: {pipeline}");

                // ── 8. Collect dependencies (shaders, etc.) ─────────────
                FileLogger.Log("Step 8: Collecting dependencies");
                CollectDependencies(
                    newPrefabPath,
                    tempFolder,
                    assetsToExport);
                FileLogger.Log($"Total assets to export: {assetsToExport.Count}");

                // ── 9. Inject shader name markers ─────────────────────────
                FileLogger.Log("Step 9: Injecting shader name markers");
                foreach (var kvp in extraction.MaterialMap)
                {
                    Material finalMat = kvp.Value;
                    string matPath = AssetDatabase.GetAssetPath(finalMat);
                    if (!string.IsNullOrEmpty(matPath))
                        WriteShaderNameToMatFile(matPath, kvp.Key.shader?.name, projectRoot);
                }

                string fixerScriptPath = InjectShaderFixer(tempFolder, assetGuid, projectRoot);
                if (!string.IsNullOrEmpty(fixerScriptPath))
                {
                    assetsToExport.Add(fixerScriptPath);
                    FileLogger.Log($"Shader fixer injected: {fixerScriptPath}");
                }

                // ── 10. Final sanity check & package export ────────────────
                FileLogger.Log("Step 10: Final sanity check and package export");
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh(ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport);

                int meshCount = AssetDatabase.FindAssets("t:Mesh", new[] { meshesFolder }).Length;
                int matCount = AssetDatabase.FindAssets("t:Material", new[] { materialsFolder }).Length;
                int textureCount = AssetDatabase.FindAssets("t:Texture", new[] { texturesFolder }).Length;

                FileLogger.Log($"Export check — pipeline:{pipeline} meshes:{meshCount} materials:{matCount} textures:{textureCount} (copiedMats:{extraction.CopiedMaterialCount}, extractedMats:{extraction.ExtractedMaterialCount}, copiedTex:{extraction.CopiedTextureCount}, extractedTex:{extraction.ExtractedTextureCount})");

                if (matCount > 0 && textureCount == 0)
                {
                    FileLogger.LogWarning($"'{item.displayName}' has materials but NO textures were copied/extracted. Imported material may render flat (white).");
                }

                string exportFolder = Path.Combine(Application.dataPath, $"../{EXPORT_FOLDER}");
                Directory.CreateDirectory(exportFolder);
                string pkgPath = Path.Combine(exportFolder, $"{assetGuid}.unitypackage");

                FileLogger.Log($"Exporting package to: {pkgPath}");
                AssetDatabase.ExportPackage(
                    assetsToExport.Distinct().ToArray(),
                    pkgPath,
                    ExportPackageOptions.Recurse | ExportPackageOptions.IncludeDependencies);

                item.lastExportedPackagePath = pkgPath;
                item.status = UploadStatus.Done;
                item.statusMessage =
                    $"Exported!\nGUID: {assetGuid}\nPipeline: {pipeline}\n" +
                    $"Meshes: {meshCount} Materials: {matCount} Textures: {textureCount}\n" +
                    $"  • copiedMats:{extraction.CopiedMaterialCount} extractedMats:{extraction.ExtractedMaterialCount}\n" +
                    $"  • copiedTex:{extraction.CopiedTextureCount} extractedTex:{extraction.ExtractedTextureCount}";

                FileLogger.Log($"Package exported successfully: {pkgPath}");
                FileLogger.Log($"Export completed for '{item.displayName}' - Log file: {FileLogger.GetLogFilePath()}");

                return ExportResult.Success(pkgPath, item.statusMessage);
            }
            catch (Exception ex)
            {
                item.status = UploadStatus.Error;
                item.statusMessage = $"Export failed: {ex.Message}";
                FileLogger.LogException(ex, $"Export error for '{item.displayName}'");
                return ExportResult.Failed(ex.Message);
            }
            finally
            {
                // ── Cleanup ─────────────────────────────────────────────
                if (AssetDatabase.IsValidFolder(tempFolder))
                {
                    AssetDatabase.DeleteAsset(tempFolder);
                    FileLogger.Log($"Cleaned up temp folder: {tempFolder}");
                }
                AssetDatabase.Refresh();
                FileLogger.Log($"Export process completed for '{item.displayName}'");
            }
        }

        /// <summary>
        /// Exports all valid items in the provided list.
        /// </summary>
        public static void ExportAllPackages(
            List<UploadItem> uploadItems,
            bool includeScripts,
            string creatorId,
            bool validateBeforeExport)
        {
            if (!Application.unityVersion.StartsWith("6000.3.13f"))
            {
                string errorMsg = $"Incompatible Unity Version: You are using Unity version {Application.unityVersion}. This SDK version only supports Unity version 6000.3.13f.";
                EditorUtility.DisplayDialog("Incompatible Unity Version", errorMsg, "OK");
                return;
            }

            var targets = uploadItems
                .Where(i => i.prefab != null && i.status != UploadStatus.Invalid)
                .ToList();

            if (targets.Count == 0)
            {
                FileLogger.LogWarning("No valid items found for export - all items either have no prefab or are marked as invalid");
                EditorUtility.DisplayDialog("No Valid Items",
                    "Please add and validate prefabs first.", "OK");
                return;
            }

            FileLogger.Log($"Starting batch export of {targets.Count} items");

            string folder = Path.Combine(Application.dataPath, $"../{EXPORT_FOLDER}");
            Directory.CreateDirectory(folder);

            int ok = 0;
            int failed = 0;

            foreach (var item in targets)
            {
                FileLogger.Log($"Exporting item {ok + failed + 1}/{targets.Count}: '{item.displayName}'");
                var result = ExportItem(item, includeScripts, creatorId, validateBeforeExport);
                if (result.IsSuccess)
                {
                    ok++;
                    FileLogger.Log($"✓ Successfully exported: '{item.displayName}'");
                }
                else
                {
                    failed++;
                    FileLogger.LogError($"✗ Failed to export: '{item.displayName}' - {result.Message}");
                }
            }

            string summary = $"Batch export completed: {ok} successful, {failed} failed";
            FileLogger.Log(summary);

            EditorUtility.DisplayDialog("Export Complete",
                $"{summary}\nLocation: {folder}", "OK");
            EditorUtility.RevealInFinder(folder);

            // Open log file for review
            FileLogger.OpenLogFile();
        }

        // ─────────────────────────────────────────────────────────────────
        // Mesh cloning
        // ─────────────────────────────────────────────────────────────────

        /// <summary>
        /// Clones all meshes on the prefab instance, replacing sharedMesh
        /// references with cloned copies stored as .asset files.
        /// Handles non-readable GLB/FBX meshes via SafeCloneMesh.
        /// </summary>
        private static Dictionary<Mesh, Mesh> CloneAllMeshes(GameObject prefabInstance, string meshesFolder)
        {
            var meshClones = new Dictionary<Mesh, Mesh>();

            // MeshFilters
            foreach (var mf in prefabInstance.GetComponentsInChildren<MeshFilter>(true))
            {
                if (mf.sharedMesh == null) continue;
                if (!meshClones.TryGetValue(mf.sharedMesh, out Mesh cloned))
                {
                    cloned = SafeCloneMesh(mf.sharedMesh);
                    if (cloned == null) continue;
                    cloned.name = SanitizeFileName(mf.sharedMesh.name);
                    string meshAssetPath = $"{meshesFolder}/{Guid.NewGuid()}_{cloned.name}.asset";
                    AssetDatabase.CreateAsset(cloned, meshAssetPath);
                    meshClones[mf.sharedMesh] = cloned;
                }
                mf.sharedMesh = cloned;
            }

            // SkinnedMeshRenderers
            foreach (var smr in prefabInstance.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (smr.sharedMesh == null) continue;
                if (!meshClones.TryGetValue(smr.sharedMesh, out Mesh cloned))
                {
                    cloned = SafeCloneMesh(smr.sharedMesh);
                    if (cloned == null) continue;
                    cloned.name = SanitizeFileName(smr.sharedMesh.name);
                    string meshAssetPath = $"{meshesFolder}/{Guid.NewGuid()}_{cloned.name}.asset";
                    AssetDatabase.CreateAsset(cloned, meshAssetPath);
                    meshClones[smr.sharedMesh] = cloned;
                }
                smr.sharedMesh = cloned;
            }

            return meshClones;
        }

        // ─────────────────────────────────────────────────────────────────
        // Material application
        // ─────────────────────────────────────────────────────────────────

        /// <summary>
        /// Replaces renderer materials with their extracted counterparts.
        /// </summary>
        private static void ApplyExtractedMaterials(
            GameObject prefabInstance,
            Dictionary<Material, Material> materialMap)
        {
            foreach (var rend in prefabInstance.GetComponentsInChildren<Renderer>(true))
            {
                var mats = rend.sharedMaterials;
                for (int i = 0; i < mats.Length; i++)
                {
                    if (mats[i] != null && materialMap.TryGetValue(mats[i], out var extracted))
                        mats[i] = extracted;
                }
                rend.sharedMaterials = mats;
            }
        }

        // ─────────────────────────────────────────────────────────────────
        // Lua script collection
        // ─────────────────────────────────────────────────────────────────

        /// <summary>
        /// Collects Lua scripts from ScenarioBridge components, copies them
        /// to the temp folder, and builds ScriptInfo descriptors.
        /// </summary>
        private static void CollectLuaScripts(
            GameObject originalPrefab,
            string tempFolder,
            string assetGuid,
            string projectRoot,
            Dictionary<string, string> luaCopiedSrc,
            List<ScriptInfo> scriptInfos,
            List<string> assetsToExport)
        {
            var bridges = originalPrefab.GetComponentsInChildren<ScenarioBridge>(true);
            int sIdx = 0;

            foreach (var bridge in bridges)
            {
                if (bridge?.luaScript == null) continue;

                string safeName = bridge.gameObject.name.Replace(" ", "_").Replace("/", "_");
                string luaSrc = AssetDatabase.GetAssetPath(bridge.luaScript);
                string destFile = $"{assetGuid}_{sIdx}_{safeName}.lua.txt";
                string luaDest = $"{tempFolder}/{destFile}";
                bool ok = false;

                if (!string.IsNullOrEmpty(luaSrc) && File.Exists(luaSrc))
                {
                    string srcExt = Path.GetExtension(luaSrc).ToLowerInvariant();
                    if (srcExt == ".lua" || srcExt == ".txt" || luaSrc.EndsWith(".lua.txt"))
                    {
                        if (luaCopiedSrc.TryGetValue(luaSrc, out var existingDest))
                        {
                            luaDest = existingDest;
                            destFile = Path.GetFileName(existingDest);
                            ok = true;
                        }
                        else
                        {
                            ok = AssetDatabase.CopyAsset(luaSrc, luaDest);
                            if (!ok)
                            {
                                // Fallback: raw file copy when extensions differ
                                try
                                {
                                    string destDisk = ToAbsolutePath(luaDest, projectRoot);
                                    File.Copy(luaSrc, destDisk, overwrite: true);
                                    AssetDatabase.ImportAsset(luaDest,
                                        ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport);
                                    ok = true;
                                }
                                catch (Exception copyEx)
                                {
                                    FileLogger.LogWarning($"Lua copy fallback failed for {luaSrc}: {copyEx.Message}");
                                }
                            }
                            if (ok) luaCopiedSrc[luaSrc] = luaDest;
                        }
                    }
                    else
                    {
                        FileLogger.LogWarning($"Lua source has unsupported extension '{srcExt}' for {luaSrc}.");
                    }
                }
                else
                {
                    // In-memory TextAsset — write its text content directly
                    try
                    {
                        string destDisk = ToAbsolutePath(luaDest, projectRoot);
                        File.WriteAllText(destDisk, bridge.luaScript.text ?? string.Empty);
                        AssetDatabase.ImportAsset(luaDest,
                            ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport);
                        ok = true;
                        FileLogger.Log($"Wrote in-memory Lua text → {luaDest}");
                    }
                    catch (Exception writeEx)
                    {
                        FileLogger.LogWarning($"Could not persist in-memory Lua for '{bridge.gameObject.name}': {writeEx.Message}");
                    }
                }

                if (!ok)
                {
                    FileLogger.LogWarning($"Skipping Lua for '{bridge.gameObject.name}' — could not export.");
                    continue;
                }

                if (!assetsToExport.Contains(luaDest))
                    assetsToExport.Add(luaDest);

                scriptInfos.Add(new ScriptInfo
                {
                    objectPath = GetHierarchyPath(bridge.transform, originalPrefab.transform),
                    objectName = bridge.gameObject.name,
                    scriptFileName = destFile
                });
                sIdx++;
            }
        }

        // ─────────────────────────────────────────────────────────────────
        // Metadata
        // ─────────────────────────────────────────────────────────────────

        /// <summary>
        /// Writes the AssetMetadata JSON file to the temp folder.
        /// </summary>
        private static string WriteMetadata(
            string tempFolder,
            string assetGuid,
            UploadItem item,
            string creatorId,
            CreatorRenderPipeline pipeline,
            List<ScriptInfo> scriptInfos,
            string projectRoot)
        {
            var meta = new AssetMetadata
            {
                assetId = assetGuid,
                displayName = item.displayName,
                description = item.description,
                category = item.category,
                tags = string.IsNullOrEmpty(item.tags) ? new string[0] : item.tags.Split(','),
                creatorId = creatorId,
                createdAt = DateTime.UtcNow.ToString("o"),
                originalName = item.prefab.name,
                renderPipeline = pipeline.ToString(),
                luaScripts = scriptInfos,
                totalScriptCount = scriptInfos.Count
            };

            string metaPath = $"{tempFolder}/{assetGuid}_metadata.json";
            string metaDiskPath = ToAbsolutePath(metaPath, projectRoot);
            File.WriteAllText(metaDiskPath, JsonUtility.ToJson(meta, true));
            AssetDatabase.ImportAsset(metaPath,
                ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport);

            return metaPath;
        }

        // ─────────────────────────────────────────────────────────────────
        // Dependency collection
        // ─────────────────────────────────────────────────────────────────

        /// <summary>
        /// Collects additional dependencies (custom shaders, shader graphs)
        /// that live outside the temp folder but must be included in the package.
        /// </summary>
        private static void CollectDependencies(
            string newPrefabPath,
            string tempFolder,
            List<string> assetsToExport)
        {
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh(ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport);

            var extraShaderPaths = new HashSet<string>();
            var prefabObj = AssetDatabase.LoadAssetAtPath<GameObject>(newPrefabPath);
            if (prefabObj != null)
            {
                var deps = EditorUtility.CollectDependencies(new UnityEngine.Object[] { prefabObj });
                foreach (var obj in deps)
                {
                    string p = AssetDatabase.GetAssetPath(obj);
                    if (string.IsNullOrEmpty(p)) continue;

                    if (p.StartsWith(tempFolder))
                    {
                        assetsToExport.Add(p);
                        continue;
                    }

                    if (p.StartsWith("Assets/") &&
                        (p.EndsWith(".shader") || p.EndsWith(".shadergraph")))
                    {
                        extraShaderPaths.Add(p);
                    }
                }
            }

            foreach (var sp in extraShaderPaths)
                assetsToExport.Add(sp);
        }

        // ─────────────────────────────────────────────────────────────────
        // Shader name marker injection
        // ─────────────────────────────────────────────────────────────────

        /// <summary>
        /// Writes a comment line into the .mat YAML file containing the
        /// original shader name. This allows the receiving project to
        /// re-map shaders if the render pipeline differs.
        /// </summary>
        private static void WriteShaderNameToMatFile(string matAssetPath, string shaderName, string projectRoot)
        {
            if (string.IsNullOrEmpty(matAssetPath) || string.IsNullOrEmpty(shaderName))
                return;

            try
            {
                string matDiskPath = ToAbsolutePath(matAssetPath, projectRoot);
                if (!File.Exists(matDiskPath))
                    return;

                var utf8NoBom = new System.Text.UTF8Encoding(false);
                string content = File.ReadAllText(matDiskPath, utf8NoBom);
                if (content.Contains("#SDK_ShaderName:"))
                    return;

                int newlineIndex = content.IndexOf('\n');
                if (newlineIndex < 0)
                    return;

                string markerLine = $"#SDK_ShaderName: {shaderName}\n";
                content = content.Insert(newlineIndex + 1, markerLine);

                File.WriteAllText(matDiskPath, content, utf8NoBom);
                AssetDatabase.ImportAsset(matAssetPath, ImportAssetOptions.ForceSynchronousImport);
            }
            catch (Exception ex)
            {
                FileLogger.LogWarning($"Could not write shader name marker to '{matAssetPath}': {ex.Message}");
            }
        }

        /// <summary>
        /// Injects a shader fixer script into the temp folder to handle
        /// shader remapping on the receiving project.
        /// </summary>
        private static string InjectShaderFixer(string tempFolder, string assetGuid, string projectRoot)
        {
            if (string.IsNullOrEmpty(tempFolder) || string.IsNullOrEmpty(assetGuid))
                return null;

            try
            {
                string templateAssetPath = SDKPathUtility.GetPath("Editor/ShaderFixerTemplate.txt");
                string templateDiskPath = ToAbsolutePath(templateAssetPath, projectRoot);
                if (!File.Exists(templateDiskPath))
                {
                    FileLogger.LogWarning($"Shader fixer template not found at '{templateAssetPath}'.");
                    return null;
                }

                string shortId = assetGuid.Replace("-", string.Empty).Substring(0, 8);
                string scriptAssetPath = $"{tempFolder}/CreatorSDKShaderFixer_{shortId}.cs";
                string scriptDiskPath = ToAbsolutePath(scriptAssetPath, projectRoot);

                var utf8NoBom = new System.Text.UTF8Encoding(false);
                string templateContent = File.ReadAllText(templateDiskPath, utf8NoBom);
                string scriptContent = templateContent.Replace("GUID_PLACEHOLDER", shortId);

                File.WriteAllText(scriptDiskPath, scriptContent, utf8NoBom);
                AssetDatabase.ImportAsset(scriptAssetPath,
                    ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport);

                return scriptAssetPath;
            }
            catch (Exception ex)
            {
                FileLogger.LogWarning($"Could not inject shader fixer for '{assetGuid}': {ex.Message}");
                return null;
            }
        }

        // ─────────────────────────────────────────────────────────────────
        // Render pipeline detection
        // ─────────────────────────────────────────────────────────────────

        /// <summary>
        /// Detects the active render pipeline for metadata tracking.
        /// </summary>
        private static CreatorRenderPipeline DetectRenderPipeline()
        {
            var rp = GraphicsSettings.defaultRenderPipeline;
            if (rp == null) return CreatorRenderPipeline.BuiltIn;
            string t = rp.GetType().FullName ?? "";
            if (t.Contains("HighDefinition")) return CreatorRenderPipeline.HDRP;
            if (t.Contains("Universal")) return CreatorRenderPipeline.URP;
            return CreatorRenderPipeline.BuiltIn;
        }

        // ─────────────────────────────────────────────────────────────────
        // Mesh cloning utilities
        // ─────────────────────────────────────────────────────────────────

        /// <summary>
        /// Clones a Mesh in a way that works for both readable and
        /// non-readable source meshes (GLB/FBX sub-assets often have
        /// Read/Write disabled).
        /// </summary>
        private static Mesh SafeCloneMesh(Mesh src)
        {
            if (src == null) return null;

            // Path A: cheap clone via Instantiate
            try
            {
                Mesh m = UnityEngine.Object.Instantiate(src);
                if (m != null && m.vertexCount > 0)
                {
                    m.indexFormat = src.indexFormat;
                    m.RecalculateBounds();
                    return m;
                }
                if (m != null) UnityEngine.Object.DestroyImmediate(m);
            }
            catch (Exception ex)
            {
                FileLogger.LogWarning($"Mesh Instantiate failed for '{src.name}': {ex.Message}. Trying manual clone.");
            }

            // Path B: manual rebuild via public APIs (requires readable mesh)
            if (!src.isReadable)
            {
                FileLogger.LogWarning($"Mesh '{src.name}' is NOT readable. Enable 'Read/Write' on the model importer to allow exporting.");
                return null;
            }

            try
            {
                Mesh dst = new Mesh();
                dst.indexFormat = src.indexFormat;

                var verts = new List<Vector3>(); src.GetVertices(verts); dst.SetVertices(verts);
                var norms = new List<Vector3>(); src.GetNormals(norms); if (norms.Count > 0) dst.SetNormals(norms);
                var tans = new List<Vector4>(); src.GetTangents(tans); if (tans.Count > 0) dst.SetTangents(tans);
                var cols = new List<Color>(); src.GetColors(cols); if (cols.Count > 0) dst.SetColors(cols);

                for (int ch = 0; ch < 8; ch++)
                {
                    var uvs = new List<Vector2>();
                    src.GetUVs(ch, uvs);
                    if (uvs.Count > 0) dst.SetUVs(ch, uvs);
                }

                dst.subMeshCount = src.subMeshCount;
                for (int sm = 0; sm < src.subMeshCount; sm++)
                {
                    var tris = src.GetIndices(sm);
                    var topo = src.GetTopology(sm);
                    dst.SetIndices(tris, topo, sm);
                }

                if (src.bindposes != null && src.bindposes.Length > 0)
                    dst.bindposes = src.bindposes;
                if (src.boneWeights != null && src.boneWeights.Length > 0)
                    dst.boneWeights = src.boneWeights;


                dst.RecalculateBounds();

                return dst;
            }
            catch (Exception ex)
            {
                FileLogger.LogError($"Manual mesh clone failed for '{src.name}': {ex.Message}");
                return null;
            }
        }

        // ─────────────────────────────────────────────────────────────────
        // Path utilities
        // ─────────────────────────────────────────────────────────────────

        /// <summary>
        /// Converts a project-relative path (using forward slashes) to an
        /// absolute disk path.
        /// </summary>
        private static string ToAbsolutePath(string projectRelativePath, string projectRoot)
        {
            return Path.Combine(projectRoot, projectRelativePath.Replace("/", Path.DirectorySeparatorChar.ToString()));
        }

        /// <summary>
        /// Returns the hierarchy path of <paramref name="child"/> RELATIVE to
        /// <paramref name="root"/>, NOT including the root segment itself.
        /// </summary>
        private static string GetHierarchyPath(Transform child, Transform root)
        {
            if (child == null || root == null) return "";
            if (child == root) return "";

            var parts = new List<string>();
            Transform cur = child;
            while (cur != null && cur != root)
            {
                parts.Insert(0, cur.name);
                cur = cur.parent;
            }
            if (cur != root) return child.name;

            return string.Join("/", parts);
        }

        private static string SanitizeFileName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "unnamed";
            foreach (char c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');
            return name.Replace(" ", "_").Replace("#", "_");
        }
    }

    // ─────────────────────────────────────────────────────────────────────
    // ExportResult DTO
    // ─────────────────────────────────────────────────────────────────────

    public readonly struct ExportResult
    {
        public bool IsSuccess { get; }
        public string PackagePath { get; }
        public string Message { get; }

        private ExportResult(bool success, string path, string message)
        {
            IsSuccess = success;
            PackagePath = path;
            Message = message;
        }

        public static ExportResult Success(string path, string message) =>
            new ExportResult(true, path, message);

        public static ExportResult Failed(string message) =>
            new ExportResult(false, null, message);
    }
}