using UnityEngine;
using UnityEditor;
using CreatorSDK;

namespace CreatorSDK.Editor
{
    /// <summary>
    /// Custom inspector for ScenarioBridge component
    /// </summary>
    [CustomEditor(typeof(ScenarioBridge))]
    public class ScenarioBridgeEditor : UnityEditor.Editor
    {
        private SerializedProperty luaScriptProp;
        private SerializedProperty exposedPropertiesProp;
        private SerializedProperty isInteractableProp;
        private SerializedProperty interactionRangeProp;
        private SerializedProperty interactionPromptProp;
        private SerializedProperty canRotateProp;
        private SerializedProperty canScaleProp;
        private SerializedProperty minScaleProp;
        private SerializedProperty maxScaleProp;
        private SerializedProperty snapToGroundProp;
        private SerializedProperty isGrabbableProp;
        private SerializedProperty isSnapZoneProp;
        private SerializedProperty customAttachPointProp;
        private SerializedProperty snapTagProp;

        private bool showLuaSection = true;
        private bool showPropertiesSection = true;
        private bool showInteractionSection = true;
        private bool showXRSection = true;
        private bool showPlacementSection = true;
        private bool showLuaPreview = false;

        private GUIStyle headerStyle;
        private GUIStyle boxStyle;

        private void OnEnable()
        {
            luaScriptProp = serializedObject.FindProperty("luaScript");
            exposedPropertiesProp = serializedObject.FindProperty("exposedProperties");
            isInteractableProp = serializedObject.FindProperty("isInteractable");
            interactionRangeProp = serializedObject.FindProperty("interactionRange");
            interactionPromptProp = serializedObject.FindProperty("interactionPrompt");
            canRotateProp = serializedObject.FindProperty("canRotate");
            canScaleProp = serializedObject.FindProperty("canScale");
            minScaleProp = serializedObject.FindProperty("minScale");
            maxScaleProp = serializedObject.FindProperty("maxScale");
            snapToGroundProp = serializedObject.FindProperty("snapToGround");
            isGrabbableProp = serializedObject.FindProperty("isGrabbable");
            isSnapZoneProp = serializedObject.FindProperty("isSnapZone");
            customAttachPointProp = serializedObject.FindProperty("customAttachPoint");
            snapTagProp = serializedObject.FindProperty("snapTag");
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            
            InitStyles();
            
            ScenarioBridge bridge = (ScenarioBridge)target;
            
            // Header
            EditorGUILayout.Space(5);
            EditorGUILayout.LabelField("Scenario Bridge", headerStyle);
            EditorGUILayout.LabelField("Runtime behavior controller for scenario objects", EditorStyles.centeredGreyMiniLabel);
            EditorGUILayout.Space(10);
            
            // Check for Lua Executor
            DrawExecutorStatus(bridge);
            
            // Lua Script Section
            showLuaSection = EditorGUILayout.BeginFoldoutHeaderGroup(showLuaSection, "Lua Script");
            if (showLuaSection)
            {
                EditorGUILayout.BeginVertical(boxStyle);
                
                EditorGUILayout.PropertyField(luaScriptProp, new GUIContent("Script File"));
                
                EditorGUILayout.BeginHorizontal();
                
                if (GUILayout.Button("Create New Script"))
                {
                    CreateNewLuaScript(bridge);
                }
                
                if (bridge.luaScript != null)
                {
                    if (GUILayout.Button("Edit Script"))
                    {
                        AssetDatabase.OpenAsset(bridge.luaScript);
                    }
                }
                
                EditorGUILayout.EndHorizontal();
                
                // Script preview
                if (bridge.luaScript != null)
                {
                    EditorGUILayout.Space(5);
                    showLuaPreview = EditorGUILayout.Foldout(showLuaPreview, "Preview Script");
                    
                    if (showLuaPreview)
                    {
                        EditorGUILayout.BeginVertical(EditorStyles.helpBox);
                        
                        string preview = bridge.luaScript.text;
                        if (preview.Length > 500)
                        {
                            preview = preview.Substring(0, 500) + "\n... (truncated)";
                        }
                        
                        EditorGUILayout.TextArea(preview, EditorStyles.wordWrappedLabel);
                        EditorGUILayout.EndVertical();
                    }
                }
                

                
                EditorGUILayout.EndVertical();
            }
            EditorGUILayout.EndFoldoutHeaderGroup();
            
            EditorGUILayout.Space(5);
            
            // Exposed Properties Section
            showPropertiesSection = EditorGUILayout.BeginFoldoutHeaderGroup(showPropertiesSection, "Exposed Properties");
            if (showPropertiesSection)
            {
                EditorGUILayout.BeginVertical(boxStyle);
                
                EditorGUILayout.HelpBox(
                    "Properties that users can modify at runtime when placing this object in their scenarios.",
                    MessageType.Info);
                
                EditorGUILayout.PropertyField(exposedPropertiesProp, true);
                
                if (GUILayout.Button("Add Common Properties"))
                {
                    AddCommonProperties(bridge);
                }
                
                EditorGUILayout.EndVertical();
            }
            EditorGUILayout.EndFoldoutHeaderGroup();
            
            EditorGUILayout.Space(5);
            
            // Interaction Section
            showInteractionSection = EditorGUILayout.BeginFoldoutHeaderGroup(showInteractionSection, "Interaction Settings");
            if (showInteractionSection)
            {
                EditorGUILayout.BeginVertical(boxStyle);
                
                EditorGUILayout.PropertyField(isInteractableProp, new GUIContent("Is Interactable"));
                
                if (isInteractableProp.boolValue)
                {
                    EditorGUI.indentLevel++;
                    EditorGUILayout.PropertyField(interactionRangeProp, new GUIContent("Interaction Range"));
                    EditorGUILayout.PropertyField(interactionPromptProp, new GUIContent("Prompt Text"));
                    EditorGUI.indentLevel--;
                }
                
                EditorGUILayout.EndVertical();
            }
            EditorGUILayout.EndFoldoutHeaderGroup();
            
            EditorGUILayout.Space(5);
            
            // XR Settings Section
            showXRSection = EditorGUILayout.BeginFoldoutHeaderGroup(showXRSection, "XR Settings");
            if (showXRSection)
            {
                EditorGUILayout.BeginVertical(boxStyle);
                
                EditorGUILayout.PropertyField(isGrabbableProp, new GUIContent("Is Grabbable", "If true, this object can be grabbed in XR"));
                EditorGUILayout.PropertyField(isSnapZoneProp, new GUIContent("Is Snap Zone", "If true, this object acts as a receiver socket for grabbable objects"));
                EditorGUILayout.PropertyField(customAttachPointProp, new GUIContent("Attach Point (Optional)", "Custom transform point for grabbing or snapping"));
                EditorGUILayout.PropertyField(snapTagProp, new GUIContent("Snap Tag (Optional)", "Tag text to match locks to keys"));
                
                EditorGUILayout.EndVertical();
            }
            EditorGUILayout.EndFoldoutHeaderGroup();
            
            EditorGUILayout.Space(5);
            
            // Placement Section
            showPlacementSection = EditorGUILayout.BeginFoldoutHeaderGroup(showPlacementSection, "Placement Rules");
            if (showPlacementSection)
            {
                EditorGUILayout.BeginVertical(boxStyle);
                
                EditorGUILayout.PropertyField(canRotateProp, new GUIContent("Can Rotate"));
                EditorGUILayout.PropertyField(canScaleProp, new GUIContent("Can Scale"));
                
                if (canScaleProp.boolValue)
                {
                    EditorGUI.indentLevel++;
                    EditorGUILayout.PropertyField(minScaleProp, new GUIContent("Min Scale"));
                    EditorGUILayout.PropertyField(maxScaleProp, new GUIContent("Max Scale"));
                    EditorGUI.indentLevel--;
                }
                
                EditorGUILayout.PropertyField(snapToGroundProp, new GUIContent("Snap to Ground"));
                
                EditorGUILayout.EndVertical();
            }
            EditorGUILayout.EndFoldoutHeaderGroup();
            
            serializedObject.ApplyModifiedProperties();
        }

        private void InitStyles()
        {
            if (headerStyle == null)
            {
                headerStyle = new GUIStyle(EditorStyles.boldLabel)
                {
                    fontSize = 16,
                    alignment = TextAnchor.MiddleCenter
                };
            }
            
            if (boxStyle == null)
            {
                boxStyle = new GUIStyle(EditorStyles.helpBox)
                {
                    padding = new RectOffset(10, 10, 10, 10)
                };
            }
        }

        private void CreateNewLuaScript(ScenarioBridge bridge)
        {
            string prefabPath = "";
            
            // Try to get path from prefab
            if (PrefabUtility.IsPartOfPrefabAsset(bridge.gameObject))
            {
                prefabPath = AssetDatabase.GetAssetPath(bridge.gameObject);
            }
            else if (PrefabUtility.IsPartOfPrefabInstance(bridge.gameObject))
            {
                var prefab = PrefabUtility.GetCorrespondingObjectFromSource(bridge.gameObject);
                if (prefab != null)
                {
                    prefabPath = AssetDatabase.GetAssetPath(prefab);
                }
            }
            
            string directory;
            if (!string.IsNullOrEmpty(prefabPath))
            {
                directory = System.IO.Path.GetDirectoryName(prefabPath);
            }
            else
            {
                directory = SDKPathUtility.GetPath("Scripts");
                if (!System.IO.Directory.Exists(directory))
                {
                    System.IO.Directory.CreateDirectory(directory);
                }
            }
            
            string scriptPath = $"{directory}/{bridge.gameObject.name}.lua.txt";
            string template =
$@"-- Lua Script for: {bridge.gameObject.name}

function OnStart()
end

function OnUpdate(deltaTime)
end

function OnInteract(userId)
end
";
            
            System.IO.File.WriteAllText(scriptPath, template);
            AssetDatabase.Refresh();
            
            bridge.luaScript = AssetDatabase.LoadAssetAtPath<TextAsset>(scriptPath);
            EditorUtility.SetDirty(bridge);
            
            AssetDatabase.OpenAsset(bridge.luaScript);
        }



        private void AddCommonProperties(ScenarioBridge bridge)
        {
            // Add common properties if they don't exist
            var props = bridge.exposedProperties;
            
            if (!props.Exists(p => p.propertyName == "color"))
            {
                props.Add(new ExposedProperty
                {
                    propertyName = "color",
                    displayName = "Color",
                    type = PropertyType.Color,
                    defaultValue = "#FFFFFF"
                });
            }
            
            if (!props.Exists(p => p.propertyName == "scale"))
            {
                props.Add(new ExposedProperty
                {
                    propertyName = "scale",
                    displayName = "Scale",
                    type = PropertyType.Float,
                    defaultValue = "1",
                    minValue = "0.1",
                    maxValue = "10"
                });
            }
            
            if (!props.Exists(p => p.propertyName == "visible"))
            {
                props.Add(new ExposedProperty
                {
                    propertyName = "visible",
                    displayName = "Visible",
                    type = PropertyType.Bool,
                    defaultValue = "true"
                });
            }
            
            if (!props.Exists(p => p.propertyName == "rotationSpeed"))
            {
                props.Add(new ExposedProperty
                {
                    propertyName = "rotationSpeed",
                    displayName = "Rotation Speed",
                    type = PropertyType.Float,
                    defaultValue = "0",
                    minValue = "-360",
                    maxValue = "360"
                });
            }
            
            EditorUtility.SetDirty(bridge);
        }

        private void DrawExecutorStatus(ScenarioBridge bridge)
        {
            var executor = bridge.GetComponent<LuaEngine>();
            
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            
            if (executor == null)
            {
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.HelpBox("Add Lua Engine to run scripts in Play Mode", MessageType.Info);
                
                if (GUILayout.Button("Add Lua Engine", GUILayout.Width(110), GUILayout.Height(38)))
                {
                    Undo.AddComponent<LuaEngine>(bridge.gameObject);
                    Debug.Log("[CreatorSDK] Lua Engine added. Scripts will now run in Play Mode!");
                }
                EditorGUILayout.EndHorizontal();
            }
            else
            {
                EditorGUILayout.BeginHorizontal();
                GUI.color = Color.green;
                EditorGUILayout.LabelField("✓ Lua Engine Active", EditorStyles.boldLabel);
                GUI.color = Color.white;
                
                if (GUILayout.Button("Remove", GUILayout.Width(70)))
                {
                    Undo.DestroyObjectImmediate(executor);
                }
                EditorGUILayout.EndHorizontal();
                
                EditorGUILayout.LabelField("Scripts will execute when you enter Play Mode", EditorStyles.miniLabel);
            }
            
            EditorGUILayout.EndVertical();
            EditorGUILayout.Space(5);
        }
    }
}
