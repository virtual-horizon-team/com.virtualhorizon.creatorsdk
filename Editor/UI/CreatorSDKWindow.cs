#if !UNITY_6000_0_OR_NEWER
#error Creator SDK requires Unity 6 (6000.x) or newer. Please upgrade your Unity version.
#endif

using UnityEngine;
using UnityEngine.UIElements;
using UnityEditor;
using UnityEditor.UIElements;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CreatorSDK.Editor.Services;
using CreatorSDK.Editor;

namespace CreatorSDK.Editor.UI
{
    /// <summary>
    /// Main Creator SDK Window — UI Toolkit shell with IMGUI assets list.
    /// Supports Export-only (no login) and Export+Upload (login required).
    ///
    /// <para>
    /// SOLID: Single Responsibility — this class handles ONLY UI Toolkit/IMGUI
    /// rendering, user interactions, and window state management. All export logic
    /// is delegated to <see cref="AssetExporter"/>, all data structures live in
    /// <see cref="SDKModels"/>, and all asset extraction is handled by
    /// <see cref="StandardAssetExtractor"/>.
    /// </para>
    /// </summary>
    public class CreatorSDKWindow : EditorWindow
    {
        #region Constants
        
        private readonly string[] CATEGORIES =
        {
            "InteractiveAsset", "EnvironmentAsset", "InteractiveAssetGroup"
        };

        #endregion

        #region State

        private List<UploadItem> uploadItems = new List<UploadItem>();
        private string creatorId = "";
        private bool includeScripts = true;
        private bool validateBeforeExport = true;

        // IMGUI scroll
        private Vector2 assetsScrollPos;

        // Async operation in progress
        private bool isBusy = false;
        private CancellationTokenSource uploadCancellation;

        #endregion

        #region UI References (UI Toolkit)

        private Label authStripBadge;
        private IMGUIContainer assetsImgui;

        // Account tab
        private VisualElement loginPanel;
        private VisualElement loggedInPanel;
        private TextField usernameField;
        private TextField passwordField;
        private Label loginMessage;
        private Label userNameLabel;

        // Settings tab
        private TextField creatorIdField;
        private Toggle includeScriptsToggle;
        private Toggle validateToggle;

        // New Tab Navigation State
        private enum TabType { Assets, Account, Settings, Help }
        private TabType activeTab = TabType.Assets;

        // Tab containers
        private VisualElement contentArea;
        private IMGUIContainer assetsTabContent;
        private VisualElement accountTabContent;
        private VisualElement settingsTabContent;
        private VisualElement helpTabContent;

        // Sidebar Menu Buttons
        private Button assetsMenuBtn;
        private Button accountMenuBtn;
        private Button settingsMenuBtn;
        private Button helpMenuBtn;
        private Button sidebarLogoutBtn;

        // Output Console Logs
        private List<string> consoleLogs = new List<string>();
        private Vector2 consoleScrollPos;

        #endregion

        // ── Menu ──────────────────────────────────────────────────────────

        [MenuItem("Creator SDK/Asset Uploader %#u")]
        public static void ShowWindow()
        {
            var w = GetWindow<CreatorSDKWindow>("Creator SDK");
            w.minSize = new Vector2(850, 600);
        }

        // ── Unity callbacks ───────────────────────────────────────────────

        private void OnEnable()
        {
            creatorId = EditorPrefs.GetString("CreatorSDK_CreatorId", "");
            includeScripts = EditorPrefs.GetBool("CreatorSDK_IncludeScripts", true);
            validateBeforeExport = EditorPrefs.GetBool("CreatorSDK_ValidateBefore", true);

            // Handle domain reload - cancel any ongoing operations
            AssemblyReloadEvents.beforeAssemblyReload += OnBeforeAssemblyReload;
            AssemblyReloadEvents.afterAssemblyReload += OnAfterAssemblyReload;
        }

        private void OnDisable()
        {
            // Clean up cancellation token
            uploadCancellation?.Cancel();
            uploadCancellation?.Dispose();
            uploadCancellation = null;

            AssemblyReloadEvents.beforeAssemblyReload -= OnBeforeAssemblyReload;
            AssemblyReloadEvents.afterAssemblyReload -= OnAfterAssemblyReload;
        }

        private void OnBeforeAssemblyReload()
        {
            // Cancel any ongoing upload operations before domain reload
            uploadCancellation?.Cancel();
            isBusy = false;
            // Clear any pending progress bars
            EditorUtility.ClearProgressBar();
        }

        private void OnAfterAssemblyReload()
        {
            // Reset state after domain reload
            isBusy = false;
            Repaint();
        }

        public void CreateGUI()
        {
            string ussPath = SDKPathUtility.GetPath("Editor/UI/CreatorSDKWindow.uss");
            var uss = AssetDatabase.LoadAssetAtPath<StyleSheet>(ussPath);
            if (uss != null) rootVisualElement.styleSheets.Add(uss);

            rootVisualElement.AddToClassList("sdk-root");

            // Build Left Sidebar
            var sidebar = BuildSidebar();
            rootVisualElement.Add(sidebar);

            // Build Right Panel
            var rightPanel = new VisualElement();
            rightPanel.AddToClassList("sdk-right-panel");
            rootVisualElement.Add(rightPanel);

            // Build Right Header
            var rightHeader = BuildRightHeader();
            rightPanel.Add(rightHeader);

            // Build Content Area
            contentArea = new VisualElement();
            contentArea.AddToClassList("sdk-content-area");
            rightPanel.Add(contentArea);

            // Build Tab Contents
            assetsTabContent = new IMGUIContainer(DrawAssetsTabIMGUI) { style = { flexGrow = 1 } };
            accountTabContent = BuildAccountTab();
            settingsTabContent = BuildSettingsTab();
            helpTabContent = BuildHelpTab();

            contentArea.Add(assetsTabContent);
            contentArea.Add(accountTabContent);
            contentArea.Add(settingsTabContent);
            contentArea.Add(helpTabContent);

            // Set Default Tab
            SwitchTab(TabType.Assets);

            UpdateAuthUI();
        }

        private VisualElement BuildSidebar()
        {
            var sidebar = new VisualElement();
            sidebar.AddToClassList("sdk-sidebar");

            // Logo Section
            var logoSection = new VisualElement();
            logoSection.AddToClassList("sidebar-logo-section");

            var logoIcon = new Label("❖"); // Red cube icon representation
            logoIcon.AddToClassList("logo-icon");
            logoSection.Add(logoIcon);

            var logoTextContainer = new VisualElement();
            logoTextContainer.AddToClassList("logo-text-container");
            logoTextContainer.Add(new Label("Virtual Horizon").WithClass("logo-title"));
            logoTextContainer.Add(new Label("Creator SDK v1.0").WithClass("logo-subtitle"));
            logoSection.Add(logoTextContainer);

            sidebar.Add(logoSection);

            // New Asset Button
            var newAssetBtn = new Button(() => {
                uploadItems.Clear();
                Repaint();
                LogToConsole("Cleared all assets from the window.");
            }) { text = "+ New Asset" };
            newAssetBtn.AddToClassList("btn-new-asset");
            sidebar.Add(newAssetBtn);

            // Tab Navigation Menu
            var menuContainer = new VisualElement();
            menuContainer.AddToClassList("sidebar-menu");

            assetsMenuBtn = new Button(() => SwitchTab(TabType.Assets)) { text = "  Assets" };
            assetsMenuBtn.AddToClassList("menu-btn");
            menuContainer.Add(assetsMenuBtn);

            accountMenuBtn = new Button(() => SwitchTab(TabType.Account)) { text = "  Account" };
            accountMenuBtn.AddToClassList("menu-btn");
            menuContainer.Add(accountMenuBtn);

            settingsMenuBtn = new Button(() => SwitchTab(TabType.Settings)) { text = "  Settings" };
            settingsMenuBtn.AddToClassList("menu-btn");
            menuContainer.Add(settingsMenuBtn);

            helpMenuBtn = new Button(() => SwitchTab(TabType.Help)) { text = "  Help" };
            helpMenuBtn.AddToClassList("menu-btn");
            menuContainer.Add(helpMenuBtn);

            sidebar.Add(menuContainer);

            // Spacer
            var spacer = new VisualElement();
            spacer.style.flexGrow = 1;
            sidebar.Add(spacer);

            // Logout Button at the bottom
            sidebarLogoutBtn = new Button(async () => await OnLogoutClicked()) { text = "  Logout" };
            sidebarLogoutBtn.AddToClassList("menu-btn");
            sidebarLogoutBtn.AddToClassList("logout-btn");
            sidebar.Add(sidebarLogoutBtn);

            return sidebar;
        }

        private VisualElement BuildRightHeader()
        {
            var header = new VisualElement();
            header.AddToClassList("right-header");

            var titleContainer = new VisualElement();
            titleContainer.AddToClassList("right-header-title-container");
            titleContainer.Add(new Label("Virtual Horizon SDK").WithClass("right-header-title"));
            
            var versionBadge = new Label("v1.0.42-STABLE").WithClass("version-badge");
            titleContainer.Add(versionBadge);
            header.Add(titleContainer);

            var rightSection = new VisualElement();
            rightSection.AddToClassList("right-header-status-container");

            // Status Badge Pill
            authStripBadge = new Label("Offline").WithClass("auth-strip-badge-pill", "badge-offline");
            rightSection.Add(authStripBadge);

            // Icons
            var bellIcon = new Label("🔔").WithClass("header-icon");
            var profileIcon = new Label("👤").WithClass("header-icon");
            rightSection.Add(bellIcon);
            rightSection.Add(profileIcon);

            header.Add(rightSection);
            return header;
        }

        private void SwitchTab(TabType tab)
        {
            activeTab = tab;

            assetsMenuBtn.RemoveFromClassList("menu-btn-active");
            accountMenuBtn.RemoveFromClassList("menu-btn-active");
            settingsMenuBtn.RemoveFromClassList("menu-btn-active");
            helpMenuBtn.RemoveFromClassList("menu-btn-active");

            assetsTabContent.style.display = DisplayStyle.None;
            accountTabContent.style.display = DisplayStyle.None;
            settingsTabContent.style.display = DisplayStyle.None;
            helpTabContent.style.display = DisplayStyle.None;

            switch (tab)
            {
                case TabType.Assets:
                    assetsMenuBtn.AddToClassList("menu-btn-active");
                    assetsTabContent.style.display = DisplayStyle.Flex;
                    break;
                case TabType.Account:
                    accountMenuBtn.AddToClassList("menu-btn-active");
                    accountTabContent.style.display = DisplayStyle.Flex;
                    break;
                case TabType.Settings:
                    settingsMenuBtn.AddToClassList("menu-btn-active");
                    settingsTabContent.style.display = DisplayStyle.Flex;
                    break;
                case TabType.Help:
                    helpMenuBtn.AddToClassList("menu-btn-active");
                    helpTabContent.style.display = DisplayStyle.Flex;
                    break;
            }
        }

        // ── Account Tab ───────────────────────────────────────────────────

        private VisualElement BuildAccountTab()
        {
            var root = new VisualElement();
            root.AddToClassList("account-tab-content");

            loginPanel = new VisualElement();
            loginPanel.AddToClassList("panel-card");
            loginPanel.Add(new Label("Login to Virtual Horizon").WithClass("panel-title"));

            usernameField = new TextField("Username");
            usernameField.AddToClassList("input-field");
            loginPanel.Add(usernameField);

            passwordField = new TextField("Password") { isPasswordField = true };
            passwordField.AddToClassList("input-field");
            loginPanel.Add(passwordField);

            var loginBtn = new Button(async () => await OnLoginClicked()) { text = "Login" };
            loginBtn.AddToClassList("btn-primary");
            loginPanel.Add(loginBtn);

            loginMessage = new Label("").WithClass("message-info");
            loginMessage.style.display = DisplayStyle.None;
            loginPanel.Add(loginMessage);

            root.Add(loginPanel);

            loggedInPanel = new VisualElement();
            loggedInPanel.AddToClassList("panel-card");
            loggedInPanel.Add(new Label("Connected").WithClass("panel-title"));

            var userCard = new VisualElement();
            userCard.AddToClassList("user-card");

            var avatar = new Label("VH").WithClass("user-avatar");
            userCard.Add(avatar);

            var userInfo = new VisualElement();
            userInfo.AddToClassList("user-info");
            userNameLabel = new Label("").WithClass("user-name");
            userInfo.Add(userNameLabel);
            userInfo.Add(new Label("Creator").WithClass("user-role"));
            userCard.Add(userInfo);

            loggedInPanel.Add(userCard);

            var logoutBtn = new Button(async () => await OnLogoutClicked()) { text = "Logout" };
            logoutBtn.AddToClassList("btn-danger");
            logoutBtn.style.marginTop = 12;
            loggedInPanel.Add(logoutBtn);

            root.Add(loggedInPanel);
            return root;
        }

        // ── Settings Tab ──────────────────────────────────────────────────

        private VisualElement BuildSettingsTab()
        {
            var root = new VisualElement();
            root.AddToClassList("settings-content");

            var card = new VisualElement();
            card.AddToClassList("panel-card");
            card.Add(new Label("Creator Settings").WithClass("panel-title"));

            creatorIdField = new TextField("Creator ID") { value = creatorId };
            creatorIdField.AddToClassList("settings-field");
            card.Add(creatorIdField);

            card.Add(new VisualElement().WithClass("section-divider"));
            card.Add(new Label("Export Options").WithClass("panel-title"));

            includeScriptsToggle = new Toggle("Include Lua Scripts") { value = includeScripts };
            includeScriptsToggle.AddToClassList("settings-field");
            card.Add(includeScriptsToggle);

            validateToggle = new Toggle("Validate Before Export") { value = validateBeforeExport };
            validateToggle.AddToClassList("settings-field");
            card.Add(validateToggle);

            var saveBtn = new Button(OnSaveSettings) { text = "Save Settings" };
            saveBtn.AddToClassList("btn-primary");
            card.Add(saveBtn);

            card.Add(new VisualElement().WithClass("section-divider"));

            var folderBtn = new Button(OnOpenExportFolder) { text = "Open Export Folder" };
            folderBtn.AddToClassList("btn-secondary");
            card.Add(folderBtn);

            root.Add(card);
            return root;
        }

        // ── Help Tab ──────────────────────────────────────────────────────

        private VisualElement BuildHelpTab()
        {
            var root = new VisualElement();
            root.AddToClassList("help-content");

            var scroll = new ScrollView();

            void AddBlock(string heading, string body)
            {
                var block = new VisualElement();
                block.AddToClassList("help-block");
                block.Add(new Label(heading).WithClass("help-heading"));
                block.Add(new Label(body) { style = { whiteSpace = WhiteSpace.Normal } });
                scroll.Add(block);
            }

            AddBlock("1. Prepare your prefab",
                "Create your 3D model / prefab in Unity.\n" +
                "Attach a ScenarioBridge component for runtime behavior.\n" +
                "Create a Lua script for custom logic.");

            AddBlock("2. Add to the list",
                "Drag prefabs into the drop area or click '+ Add Prefab'.\n" +
                "Fill in the name, category, and description.");

            AddBlock("3. Set a thumbnail (optional)",
                "Click 'Auto' to auto-capture a preview from Unity, or\n" +
                "'Browse' to pick a PNG/JPG image manually.");

            AddBlock("4. Export",
                "Click 'Export All' to generate .unitypackage files\n" +
                "without uploading.");

            AddBlock("5. Upload",
                "Login in the Account tab, then click 'Export & Upload All'\n" +
                "to export and send packages directly to the server.");

            AddBlock("Lua Script API",
                "self:SetPosition(x, y, z)   self:SetRotation(x, y, z)\n" +
                "self:SetScale(x, y, z)       self:SetColor(r, g, b, a)\n" +
                "self:PlayAnimation(name)     self:Log(message)\n" +
                "self:Rotate(x, y, z)         self:Move(x, y, z)\n\n" +
                "Lifecycle:  OnStart()  OnUpdate(dt)  OnInteract(userId)\n" +
                "            OnTriggerEnter(obj)  OnTriggerExit(obj)\n" +
                "            OnPropertyChanged(name, value)");

            root.Add(scroll);

            var sampleBtn = new Button(CreateSamplePrefab) { text = "Create Sample Prefab with Lua" };
            sampleBtn.AddToClassList("btn-secondary");
            sampleBtn.style.marginTop = 12;
            sampleBtn.style.marginBottom = 12;
            sampleBtn.style.marginLeft = 12;
            sampleBtn.style.marginRight = 12;
            root.Add(sampleBtn);

            return root;
        }

        // ── Auth UI state ─────────────────────────────────────────────────

        private void UpdateAuthUI()
        {
            bool loggedIn = AuthService.IsLoggedIn;

            if (authStripBadge != null)
            {
                if (loggedIn)
                {
                    authStripBadge.text = $"● {AuthService.CurrentUser} | Online";
                    authStripBadge.RemoveFromClassList("badge-offline");
                    authStripBadge.AddToClassList("badge-online");
                }
                else
                {
                    authStripBadge.text = "● Offline";
                    authStripBadge.RemoveFromClassList("badge-online");
                    authStripBadge.AddToClassList("badge-offline");
                }
            }

            if (sidebarLogoutBtn != null)
            {
                sidebarLogoutBtn.style.display = loggedIn ? DisplayStyle.Flex : DisplayStyle.None;
            }

            loginPanel?.SetDisplay(!loggedIn);
            loggedInPanel?.SetDisplay(loggedIn);

            if (loggedIn && userNameLabel != null)
                userNameLabel.text = AuthService.CurrentUser;
        }

        private void SetLoginMessage(string msg, bool isError)
        {
            if (loginMessage == null) return;
            loginMessage.text = msg;
            loginMessage.style.display = string.IsNullOrEmpty(msg) ? DisplayStyle.None : DisplayStyle.Flex;
            loginMessage.RemoveFromClassList("message-success");
            loginMessage.RemoveFromClassList("message-error");
            loginMessage.AddToClassList(isError ? "message-error" : "message-success");
        }

        // ── Auth button handlers ──────────────────────────────────────────

        private async Task OnLoginClicked()
        {
            if (isBusy) return;
            isBusy = true;
            SetLoginMessage("Logging in…", false);

            string user = usernameField?.value ?? "";
            string pass = passwordField?.value ?? "";

            if (string.IsNullOrWhiteSpace(user) || string.IsNullOrWhiteSpace(pass))
            {
                SetLoginMessage("Username and password are required.", true);
                isBusy = false;
                return;
            }

            try
            {
                var (ok, msg) = await AuthService.Login(user, pass);
                SetLoginMessage(msg, !ok);
                UpdateAuthUI();
            }
            catch (Exception ex)
            {
                Debug.LogError($"[CreatorSDK] Login error: {ex.Message}");
                SetLoginMessage($"Login failed: {ex.Message}", true);
            }
            finally
            {
                isBusy = false;
            }
        }

        private async Task OnLogoutClicked()
        {
            if (isBusy) return;
            isBusy = true;

            try
            {
                await AuthService.Logout();
                UpdateAuthUI();
            }
            catch (Exception ex)
            {
                Debug.LogError($"[CreatorSDK] Logout error: {ex.Message}");
            }
            finally
            {
                isBusy = false;
            }
        }

        // ── Settings handlers ─────────────────────────────────────────────

        private void OnSaveSettings()
        {
            creatorId = creatorIdField?.value ?? "";
            includeScripts = includeScriptsToggle?.value ?? true;
            validateBeforeExport = validateToggle?.value ?? true;

            EditorPrefs.SetString("CreatorSDK_CreatorId", creatorId);
            EditorPrefs.SetBool("CreatorSDK_IncludeScripts", includeScripts);
            EditorPrefs.SetBool("CreatorSDK_ValidateBefore", validateBeforeExport);

            EditorUtility.DisplayDialog("Settings Saved", "Your settings have been saved.", "OK");
        }

        private void OnOpenExportFolder()
        {
            string path = Path.Combine(Application.dataPath, "../ExportedPackages");
            if (Directory.Exists(path))
                EditorUtility.RevealInFinder(path);
            else
                EditorUtility.DisplayDialog("Folder Not Found",
                    "No exports yet. Export some packages first.", "OK");
        }

        // ── IMGUI Assets Tab ──────────────────────────────────────────────

        private void DrawAssetsTabIMGUI()
        {
            DrawDragDropArea();
            EditorGUILayout.Space(8);
            DrawItemsList();
            EditorGUILayout.Space(8);
            DrawConsoleView();
            EditorGUILayout.Space(8);
            DrawActionButtons();
        }

        private void DrawDragDropArea()
        {
            Rect dropArea = GUILayoutUtility.GetRect(0, 75, GUILayout.ExpandWidth(true));
            
            Color oldBg = GUI.backgroundColor;
            GUI.backgroundColor = new Color(0.1f, 0.1f, 0.1f);
            
            var style = new GUIStyle(GUI.skin.box)
            {
                alignment = TextAnchor.MiddleCenter,
                richText = true,
                padding = new RectOffset(10, 10, 10, 10),
                normal = { background = Texture2D.whiteTexture }
            };
            
            GUI.Box(dropArea, "", style);
            GUI.backgroundColor = oldBg;

            // Draw a thin dark gray outline
            EditorGUI.DrawRect(new Rect(dropArea.x, dropArea.y, dropArea.width, 1), new Color(0.25f, 0.25f, 0.25f));
            EditorGUI.DrawRect(new Rect(dropArea.x, dropArea.yMax - 1, dropArea.width, 1), new Color(0.25f, 0.25f, 0.25f));
            EditorGUI.DrawRect(new Rect(dropArea.x, dropArea.y, 1, dropArea.height), new Color(0.25f, 0.25f, 0.25f));
            EditorGUI.DrawRect(new Rect(dropArea.xMax - 1, dropArea.y, 1, dropArea.height), new Color(0.25f, 0.25f, 0.25f));

            var labelStyle = new GUIStyle(EditorStyles.label)
            {
                alignment = TextAnchor.MiddleCenter,
                richText = true
            };
            
            string contentText = "<size=20><color=#aaaaaa>☁</color></size>\n<b><size=14><color=#ffffff>Drag & Drop Prefabs Here</color></size></b>\n<size=10><color=#888888>(or click '+ Add Prefab' below)</color></size>";
            GUI.Label(dropArea, contentText, labelStyle);

            Event evt = Event.current;
            if ((evt.type == EventType.DragUpdated || evt.type == EventType.DragPerform)
                && dropArea.Contains(evt.mousePosition))
            {
                DragAndDrop.visualMode = DragAndDropVisualMode.Copy;
                if (evt.type == EventType.DragPerform)
                {
                    DragAndDrop.AcceptDrag();
                    foreach (var obj in DragAndDrop.objectReferences)
                    {
                        if (obj is GameObject go &&
                            AssetDatabase.GetAssetPath(go).EndsWith(".prefab"))
                            AddPrefabToList(go);
                    }
                }
                evt.Use();
            }
        }

        private void DrawItemsList()
        {
            EditorGUILayout.BeginHorizontal();
            var titleStyle = new GUIStyle(EditorStyles.boldLabel) { fontSize = 12, normal = { textColor = new Color(0.9f, 0.9f, 0.9f) } };
            EditorGUILayout.LabelField($"📦 ASSETS ({uploadItems.Count})", titleStyle);

            var buttonStyle = new GUIStyle(GUI.skin.button)
            {
                padding = new RectOffset(10, 10, 4, 4),
                fontSize = 11,
                normal = { textColor = new Color(0.85f, 0.85f, 0.85f) }
            };

            if (GUILayout.Button("+ Add Prefab", buttonStyle, GUILayout.Width(100)))
                uploadItems.Add(new UploadItem());
            EditorGUIUtility.AddCursorRect(GUILayoutUtility.GetLastRect(), MouseCursor.Link);

            if (GUILayout.Button("Clear All", buttonStyle, GUILayout.Width(80)))
            {
                if (EditorUtility.DisplayDialog("Clear All", "Remove all items from the list?", "Yes", "No"))
                    uploadItems.Clear();
            }
            EditorGUIUtility.AddCursorRect(GUILayoutUtility.GetLastRect(), MouseCursor.Link);
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.Space(4);

            assetsScrollPos = EditorGUILayout.BeginScrollView(assetsScrollPos, GUILayout.ExpandHeight(true));

            if (uploadItems.Count == 0)
                EditorGUILayout.HelpBox("No prefabs added. Drag prefabs above or click '+ Add Prefab'.",
                    MessageType.Info);
            else
                for (int i = 0; i < uploadItems.Count; i++)
                    DrawUploadItem(uploadItems[i], i);

            EditorGUILayout.EndScrollView();
        }

        private void DrawUploadItem(UploadItem item, int index)
        {
            var cardStyle = new GUIStyle(GUI.skin.box)
            {
                padding = new RectOffset(12, 12, 10, 10),
                normal = { background = Texture2D.whiteTexture }
            };

            Color oldBg = GUI.backgroundColor;
            GUI.backgroundColor = new Color(0.12f, 0.12f, 0.12f);
            EditorGUILayout.BeginVertical(cardStyle);
            GUI.backgroundColor = oldBg;

            // Row 1: Header (icons + name + status + close)
            EditorGUILayout.BeginHorizontal();
            
            var iconStyle = new GUIStyle(EditorStyles.label) { normal = { textColor = new Color(0.6f, 0.6f, 0.6f) } };
            var titleStyle = new GUIStyle(EditorStyles.boldLabel) { normal = { textColor = Color.white } };
            
            EditorGUILayout.LabelField("↖", iconStyle, GUILayout.Width(14));
            EditorGUILayout.LabelField("❖", iconStyle, GUILayout.Width(14));
            
            string prefabName = item.prefab != null ? item.prefab.name : "None";
            EditorGUILayout.LabelField(prefabName, titleStyle, GUILayout.MinWidth(100));
            
            GUILayout.FlexibleSpace();

            if (item.status == UploadStatus.Done || item.uploadStatus == UploadStatus.Done)
            {
                var doneStyle = new GUIStyle(EditorStyles.label) { normal = { textColor = new Color(0.36f, 0.87f, 0.36f) }, fontStyle = FontStyle.Bold };
                EditorGUILayout.LabelField("✔ Done", doneStyle, GUILayout.Width(50));
            }
            else
            {
                var statusStyle = new GUIStyle(EditorStyles.label) { normal = { textColor = StatusColor(item.status) } };
                EditorGUILayout.LabelField(item.status.ToString(), statusStyle, GUILayout.Width(70));
            }

            var closeStyle = new GUIStyle(GUI.skin.button) { normal = { textColor = new Color(0.6f, 0.6f, 0.6f) }, active = { textColor = Color.white } };
            if (GUILayout.Button("✕", closeStyle, GUILayout.Width(20), GUILayout.Height(18)))
            {
                uploadItems.RemoveAt(index);
                EditorGUILayout.EndHorizontal();
                EditorGUILayout.EndVertical();
                return;
            }
            EditorGUIUtility.AddCursorRect(GUILayoutUtility.GetLastRect(), MouseCursor.Link);

            EditorGUILayout.EndHorizontal();
            EditorGUILayout.Space(6);

            // Row 2: Content (Left column: thumb, Right column: fields)
            EditorGUILayout.BeginHorizontal();

            // LEFT COLUMN
            EditorGUILayout.BeginVertical(GUILayout.Width(160));
            
            Texture2D thumb = GetOrCacheThumb(item);
            Rect thumbRect = GUILayoutUtility.GetRect(150, 150);
            if (thumb != null)
                GUI.DrawTexture(thumbRect, thumb, ScaleMode.ScaleToFit);
            else
                EditorGUI.DrawRect(thumbRect, new Color(0.18f, 0.18f, 0.18f));
                
            // Buttons under thumbnail
            EditorGUILayout.Space(6);
            EditorGUILayout.BeginHorizontal();

            var buttonStyle = new GUIStyle(GUI.skin.button)
            {
                fixedHeight = 24,
                fontSize = 11,
                fontStyle = FontStyle.Bold
            };

            GUIContent designContent = new GUIContent("🪄 Design", "Open the interactive 3D thumbnail designer to rotate, zoom, and capture a custom angle.");
            if (GUILayout.Button(designContent, buttonStyle, GUILayout.Width(73)))
            {
                if (item.prefab != null)
                {
                    CreatorSDKThumbnailEditorWindow.ShowWindow(item.prefab, (path) =>
                    {
                        item.manualThumbnailPath = path;
                        item.thumbnailTexture = LoadTextureFromDisk(path);
                        Repaint();
                    });
                }
                else
                {
                    EditorUtility.DisplayDialog("No Prefab Selected", "Please select a prefab first before designing a thumbnail.", "OK");
                }
            }
            EditorGUIUtility.AddCursorRect(GUILayoutUtility.GetLastRect(), MouseCursor.Link);

            GUILayout.Space(4);

            GUIContent browseContent = new GUIContent("📂 Browse", "Select an existing image from your computer to use as the thumbnail.");
            if (GUILayout.Button(browseContent, buttonStyle, GUILayout.Width(73)))
            {
                string picked = EditorUtility.OpenFilePanel("Select Thumbnail", "", "png,jpg,jpeg");
                if (!string.IsNullOrEmpty(picked))
                {
                    item.manualThumbnailPath = picked;
                    item.thumbnailTexture = LoadTextureFromDisk(picked);
                }
            }
            EditorGUIUtility.AddCursorRect(GUILayoutUtility.GetLastRect(), MouseCursor.Link);

            EditorGUILayout.EndHorizontal();
            
            EditorGUILayout.EndVertical();
            EditorGUILayout.Space(10);

            // RIGHT COLUMN
            EditorGUILayout.BeginVertical();

            // Prefab Object Selector
            item.prefab = (GameObject)EditorGUILayout.ObjectField("Prefab", item.prefab, typeof(GameObject), false);
            if (item.prefab != null && string.IsNullOrEmpty(item.displayName))
                item.displayName = item.prefab.name;

            // Name + Type
            EditorGUILayout.BeginHorizontal();
            
            EditorGUILayout.BeginVertical();
            EditorGUILayout.LabelField("Name", EditorStyles.miniLabel);
            item.displayName = EditorGUILayout.TextField(item.displayName);
            EditorGUILayout.EndVertical();
            
            EditorGUILayout.BeginVertical(GUILayout.Width(140));
            EditorGUILayout.LabelField("Type", EditorStyles.miniLabel);
            item.categoryIndex = EditorGUILayout.Popup(item.categoryIndex, CATEGORIES);
            item.category = CATEGORIES[item.categoryIndex];
            EditorGUILayout.EndVertical();
            
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.Space(4);

            // Description
            EditorGUILayout.LabelField("Description", EditorStyles.miniLabel);
            item.description = EditorGUILayout.TextArea(item.description, GUILayout.Height(32));
            EditorGUILayout.Space(4);

            // Tags
            EditorGUILayout.LabelField("Tags", EditorStyles.miniLabel);
            item.tags = EditorGUILayout.TextField(item.tags);
            EditorGUILayout.Space(6);

            // Info panel card
            DrawCardInfoBox(item);

            EditorGUILayout.EndVertical();
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.EndVertical();
            EditorGUILayout.Space(4);
        }

        private void DrawCardInfoBox(UploadItem item)
        {
            Color oldBg = GUI.backgroundColor;
            GUI.backgroundColor = new Color(0.18f, 0.14f, 0.15f);

            var infoStyle = new GUIStyle(GUI.skin.box)
            {
                padding = new RectOffset(10, 10, 8, 8),
                normal = { background = Texture2D.whiteTexture }
            };

            EditorGUILayout.BeginVertical(infoStyle);
            GUI.backgroundColor = oldBg;

            var titleStyle = new GUIStyle(EditorStyles.boldLabel) { normal = { textColor = new Color(0.95f, 0.5f, 0.55f) } };
            EditorGUILayout.LabelField("☉ INFO", titleStyle);

            int meshCount = 0;
            int matCount = 0;
            if (item.prefab != null)
            {
                var filters = item.prefab.GetComponentsInChildren<MeshFilter>(true);
                meshCount = filters.Length;
                
                var renderers = item.prefab.GetComponentsInChildren<Renderer>(true);
                var mats = new System.Collections.Generic.HashSet<Material>();
                foreach (var r in renderers)
                {
                    foreach (var m in r.sharedMaterials)
                    {
                        if (m != null) mats.Add(m);
                    }
                }
                matCount = mats.Count;
            }

            string statusLine = item.status == UploadStatus.Done || item.uploadStatus == UploadStatus.Done ? "Exported & Uploaded!" : (item.lastExportedPackagePath != "" ? "Exported!" : "Ready for export");
            
            var textStyle = new GUIStyle(EditorStyles.label) { richText = true, wordWrap = true };
            textStyle.normal.textColor = new Color(0.85f, 0.85f, 0.85f);

            EditorGUILayout.LabelField(statusLine, textStyle);
            string guidText = string.IsNullOrEmpty(item.generatedGuid) ? "Pending export" : item.generatedGuid;
            EditorGUILayout.LabelField($"GUID: <color=#ff667f>{guidText}</color>", textStyle);
            EditorGUILayout.LabelField("Pipeline: URP", textStyle);
            
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField($"Meshes: {meshCount}", textStyle, GUILayout.Width(100));
            EditorGUILayout.LabelField($"Materials: {matCount}", textStyle, GUILayout.Width(100));
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.EndVertical();
        }

        private void DrawConsoleView()
        {
            EditorGUILayout.BeginHorizontal();
            var headerStyle = new GUIStyle(EditorStyles.boldLabel) { normal = { textColor = new Color(0.9f, 0.9f, 0.9f) } };
            EditorGUILayout.LabelField("OUTPUT CONSOLE", headerStyle, GUILayout.Width(150));
            GUILayout.FlexibleSpace();

            int completedTasks = uploadItems.Count(i => i.status == UploadStatus.Done || i.uploadStatus == UploadStatus.Done);
            var statusStyle = new GUIStyle(EditorStyles.label) { normal = { textColor = new Color(0.36f, 0.87f, 0.36f) } };
            EditorGUILayout.LabelField($"● {completedTasks} Tasks Completed", statusStyle, GUILayout.Width(150));

            var clearBtnStyle = new GUIStyle(GUI.skin.button) { padding = new RectOffset(6, 6, 2, 2), fontSize = 10 };
            if (GUILayout.Button("Clear Console", clearBtnStyle, GUILayout.Width(90)))
            {
                consoleLogs.Clear();
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space(2);
            consoleScrollPos = EditorGUILayout.BeginScrollView(consoleScrollPos, GUILayout.Height(100));
            
            var boxStyle = new GUIStyle(GUI.skin.box)
            {
                normal = { background = Texture2D.whiteTexture },
                padding = new RectOffset(8, 8, 8, 8)
            };

            Color prevBg = GUI.backgroundColor;
            GUI.backgroundColor = new Color(0.08f, 0.08f, 0.08f);

            var logTextStyle = new GUIStyle(EditorStyles.wordWrappedLabel)
            {
                richText = true
            };
            logTextStyle.normal.textColor = new Color(0.85f, 0.85f, 0.85f);

            string joinedLogs = string.Join("\n", consoleLogs);
            if (string.IsNullOrEmpty(joinedLogs))
            {
                joinedLogs = "<color=#666666>Console idle...</color>";
            }
            EditorGUILayout.TextArea(joinedLogs, logTextStyle, GUILayout.ExpandHeight(true));

            EditorGUILayout.EndScrollView();
            GUI.backgroundColor = prevBg;
        }

        private void DrawActionButtons()
        {
            Rect line = EditorGUILayout.GetControlRect(false, 1);
            EditorGUI.DrawRect(line, new Color(0.2f, 0.2f, 0.2f));
            EditorGUILayout.Space(6);

            int valid = uploadItems.Count(i => i.prefab != null);
            
            var standardBtnStyle = new GUIStyle(GUI.skin.button)
            {
                fontSize = 11,
                fontStyle = FontStyle.Bold,
                normal = { textColor = new Color(0.85f, 0.85f, 0.85f) }
            };

            var redBtnStyle = new GUIStyle(GUI.skin.button)
            {
                fontSize = 11,
                fontStyle = FontStyle.Bold,
                normal = { textColor = Color.white, background = Texture2D.whiteTexture }
            };

            EditorGUILayout.BeginHorizontal();

            // Validate All
            EditorGUI.BeginDisabledGroup(valid == 0 || isBusy);
            if (GUILayout.Button("VALIDATE ALL", standardBtnStyle, GUILayout.Height(30)))
                ValidateAllItems();
            if (valid > 0 && !isBusy)
                EditorGUIUtility.AddCursorRect(GUILayoutUtility.GetLastRect(), MouseCursor.Link);
            EditorGUI.EndDisabledGroup();

            // View Export Log
            if (GUILayout.Button("VIEW EXPORT LOG", standardBtnStyle, GUILayout.Height(30)))
            {
                string logDir = Path.Combine(Application.dataPath, "../ExportedPackages");
                var logFiles = Directory.GetFiles(logDir, "export_log_*.txt")
                    .OrderByDescending(f => File.GetCreationTime(f))
                    .ToArray();
                if (logFiles.Length > 0)
                {
                    UnityEditor.EditorUtility.OpenWithDefaultApp(logFiles[0]);
                }
                else
                {
                    EditorUtility.DisplayDialog("No Log Files", "No export log files found in ExportedPackages folder.", "OK");
                }
            }
            EditorGUIUtility.AddCursorRect(GUILayoutUtility.GetLastRect(), MouseCursor.Link);

            // Export Packages
            EditorGUI.BeginDisabledGroup(valid == 0 || isBusy);
            Color oldBg = GUI.backgroundColor;
            GUI.backgroundColor = new Color(0.7f, 0.05f, 0.15f); // brand red
            if (GUILayout.Button($"EXPORT PACKAGES ({valid})", redBtnStyle, GUILayout.Height(30)))
            {
                LogToConsole($"Starting export for {valid} package(s)...");
                AssetExporter.ExportAllPackages(uploadItems, includeScripts, creatorId, validateBeforeExport);
                LogToConsole("Export complete! Packages saved to ExportedPackages folder.");
                EditorUtility.DisplayDialog("Export Complete", "All valid packages have been exported.", "OK");
            }
            if (valid > 0 && !isBusy)
                EditorGUIUtility.AddCursorRect(GUILayoutUtility.GetLastRect(), MouseCursor.Link);
            GUI.backgroundColor = oldBg;
            EditorGUI.EndDisabledGroup();

            // Upload Last Exported
            bool canUpload = uploadItems.Any(i => i.status == UploadStatus.Done && !string.IsNullOrEmpty(i.lastExportedPackagePath)) && !isBusy && AuthService.IsLoggedIn;
            EditorGUI.BeginDisabledGroup(!canUpload);
            if (GUILayout.Button("UPLOAD LAST EXPORTED", standardBtnStyle, GUILayout.Height(30)))
                _ = UploadLastExported();
            if (canUpload)
                EditorGUIUtility.AddCursorRect(GUILayoutUtility.GetLastRect(), MouseCursor.Link);
            EditorGUI.EndDisabledGroup();

            EditorGUILayout.EndHorizontal();

            if (!AuthService.IsLoggedIn)
            {
                var tipStyle = new GUIStyle(EditorStyles.miniLabel) { alignment = TextAnchor.MiddleCenter, normal = { textColor = new Color(0.6f, 0.6f, 0.6f) } };
                EditorGUILayout.LabelField("Login in the Account tab to enable 'UPLOAD LAST EXPORTED'.", tipStyle);
            }
        }

        private void LogToConsole(string msg, string type = "INFO")
        {
            string time = DateTime.Now.ToString("HH:mm:ss");
            string typeColor = type == "ERROR" ? "#ff5555" : (type == "WARNING" ? "#ffaa00" : "#ff4d6a");
            consoleLogs.Add($"<color=#888888>[{time}]</color> <color={typeColor}>{type}:</color> {msg}");
            Repaint();
        }

        // ── Custom Message Box ────────────────────────────────────────────

        private void DrawMessageCard(string message, MessageType type)
        {
            if (string.IsNullOrEmpty(message)) return;

            Color oldBg = GUI.backgroundColor;
            GUI.backgroundColor = type == MessageType.Error
                ? new Color(1f, 0.4f, 0.4f, 0.8f)
                : new Color(0.4f, 0.8f, 1f, 0.8f);

            GUIStyle cardStyle = new GUIStyle(EditorStyles.helpBox)
            {
                padding = new RectOffset(10, 10, 8, 8),
                margin = new RectOffset(0, 0, 6, 0)
            };

            GUIStyle textStyle = new GUIStyle(EditorStyles.wordWrappedLabel)
            {
                richText = true,
                fontSize = 11
            };

            EditorGUILayout.BeginVertical(cardStyle);

            string iconColor = type == MessageType.Error ? "#ff8888" : "#88ccff";
            string title = type == MessageType.Error ? "<b>✖ ERROR</b>" : "<b>ℹ INFO</b>";
            string displayMsg = message.Replace("{", "\n{").Replace(",", ",\n");

            EditorGUILayout.LabelField(
                $"<color={iconColor}>{title}</color>\n<color=#eeeeee>{displayMsg}</color>",
                textStyle);

            EditorGUILayout.EndVertical();
            GUI.backgroundColor = oldBg;
        }

        // ── Thumbnail helpers ─────────────────────────────────────────────

        private Texture2D GetOrCacheThumb(UploadItem item)
        {
            if (item.thumbnailTexture != null) return item.thumbnailTexture;
            if (item.prefab == null) return null;

            var preview = AssetPreview.GetAssetPreview(item.prefab)
                       ?? AssetPreview.GetMiniThumbnail(item.prefab);

            if (preview != null)
                item.thumbnailTexture = preview;

            return item.thumbnailTexture;
        }

        private Texture2D LoadTextureFromDisk(string path)
        {
            if (!File.Exists(path)) return null;
            byte[] data = File.ReadAllBytes(path);
            var tex = new Texture2D(1, 1);
            tex.LoadImage(data);
            return tex;
        }

        // ── Status helpers ────────────────────────────────────────────────

        private static Color StatusColor(UploadStatus s) => s switch
        {
            UploadStatus.Valid => Color.green,
            UploadStatus.Invalid => Color.red,
            UploadStatus.Error => Color.red,
            UploadStatus.Done => Color.cyan,
            UploadStatus.Exporting => Color.yellow,
            _ => Color.gray
        };

        private static string StatusIcon(UploadStatus s) => s switch
        {
            UploadStatus.Valid => "✓",
            UploadStatus.Invalid => "✗",
            UploadStatus.Error => "!",
            UploadStatus.Done => "↑",
            _ => "○"
        };

        // ── List management ───────────────────────────────────────────────

        private void AddPrefabToList(GameObject prefab)
        {
            if (uploadItems.Any(i => i.prefab == prefab))
            {
                Debug.LogWarning($"[CreatorSDK] {prefab.name} is already in the list.");
                return;
            }

            uploadItems.Add(new UploadItem
            {
                prefab = prefab,
                displayName = prefab.name,
                category = "InteractiveAsset",
                status = UploadStatus.Pending
            });
            Repaint();
        }

        // ── Validation ────────────────────────────────────────────────────

        private void ValidateAllItems()
        {
            foreach (var item in uploadItems)
            {
                if (item.prefab == null) continue;
                ValidateItem(item);
            }
        }

        private void ValidateItem(UploadItem item)
        {
            item.status = UploadStatus.Validating;
            item.statusMessage = "";

            var result = AssetValidator.ValidatePrefab(item.prefab);

            if (string.IsNullOrWhiteSpace(item.displayName))
                result.AddError("Display name is required.");

            if (result.HasErrors)
            {
                item.status = UploadStatus.Invalid;
                item.statusMessage = result.GetFullReport();
            }
            else
            {
                item.status = UploadStatus.Valid;
                item.statusMessage = result.HasWarnings ? result.GetFullReport() : "Validation passed!";
            }
        }

        // ── Export & Upload ───────────────────────────────────────────────

        private async Task UploadLastExported()
        {
            if (!AuthService.IsLoggedIn)
            {
                EditorUtility.DisplayDialog("Login Required",
                    "Please login in the Account tab before uploading.", "OK");
                return;
            }

            // Create cancellation token for this operation
            uploadCancellation = new CancellationTokenSource();
            var token = uploadCancellation.Token;

            try
            {
                isBusy = true;
                Repaint();

                token.ThrowIfCancellationRequested();

                await UploadExportedItems(token);
            }
            catch (OperationCanceledException)
            {
                // Operation was cancelled (likely due to domain reload)
                Debug.Log("[CreatorSDK] Upload operation was cancelled");

                // Reset all items that were in progress
                foreach (var item in uploadItems)
                {
                    if (item.uploadStatus == UploadStatus.Exporting)
                    {
                        item.uploadStatus = UploadStatus.Error;
                        item.uploadMessage = "Upload cancelled due to script reload";
                    }
                }

                isBusy = false;
                Repaint();

                EditorUtility.DisplayDialog("Upload Cancelled",
                    "Upload was cancelled due to Unity script reload. Please try again.", "OK");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[CreatorSDK] Upload error: {ex.Message}");

                // Reset busy state
                isBusy = false;
                Repaint();

                EditorUtility.DisplayDialog("Upload Error",
                    $"An error occurred during upload: {ex.Message}", "OK");
            }
            finally
            {
                // Clean up cancellation token
                uploadCancellation?.Dispose();
                uploadCancellation = null;
            }
        }

        private async Task UploadExportedItems(CancellationToken token)
        {
            // Upload all successfully exported items
            var toUpload = uploadItems
                .Where(i => i.status == UploadStatus.Done &&
                            !string.IsNullOrEmpty(i.lastExportedPackagePath))
                .ToList();

            if (toUpload.Count == 0)
            {
                EditorUtility.DisplayDialog("No Assets to Upload",
                    "No exported packages found. Please export packages first.", "OK");
                return;
            }

            LogToConsole($"Initiating upload for {toUpload.Count} asset(s)...");

            // Prepare thumbnails for all items that need them (on main thread before async upload)
            string tempDir = Path.Combine(Application.dataPath, "../Temp/CreatorSDK_Thumbs");
            Directory.CreateDirectory(tempDir);

            foreach (var item in toUpload)
            {
                if (string.IsNullOrEmpty(item.manualThumbnailPath) || !File.Exists(item.manualThumbnailPath))
                {
                    item.manualThumbnailPath = UploadService.CaptureAutoThumbnail(item.prefab, tempDir);
                }
            }

            foreach (var item in toUpload)
            {
                token.ThrowIfCancellationRequested();

                item.uploadStatus = UploadStatus.Exporting;
                item.uploadMessage = "Uploading…";
                Repaint();

                LogToConsole($"Uploading asset '{item.displayName}' (GUID: {item.generatedGuid})...");
                string thumbPath = item.manualThumbnailPath;

                var (ok, msg) = await UploadService.UploadAssetWithProgressBar(
                    item.lastExportedPackagePath,
                    item.generatedGuid,
                    item.displayName,
                    item.category,
                    item.description,
                    thumbPath);

                token.ThrowIfCancellationRequested();

                item.uploadStatus = ok ? UploadStatus.Done : UploadStatus.Error;
                item.uploadMessage = msg;
                Repaint();

                if (ok)
                {
                    LogToConsole($"Asset '{item.displayName}' uploaded successfully. Initiating bundle compilation...");
                    item.uploadMessage += "\nGenerating bundles…";
                    Repaint();

                    var (LinOk, LinMsg) = await UploadService.GenerateBundleWithProgressBar(item.generatedGuid, "Linux");
                    LogToConsole($"Linux bundle: {(LinOk ? "Success" : "Failed")}");
                    
                    var (winOk, winMsg) = await UploadService.GenerateBundleWithProgressBar(item.generatedGuid, "Windows");
                    LogToConsole($"Windows bundle: {(winOk ? "Success" : "Failed")}");
                    
                    var (andOk, andMsg) = await UploadService.GenerateBundleWithProgressBar(item.generatedGuid, "Android");
                    LogToConsole($"Android bundle: {(andOk ? "Success" : "Failed")}");

                    token.ThrowIfCancellationRequested();

                    item.uploadMessage = msg
                        + $"\n[Linux] {LinMsg}"
                        + $"\n[Windows] {winMsg}"
                        + $"\n[Android] {andMsg}";

                    item.uploadStatus = (LinOk && winOk && andOk) ? UploadStatus.Done : UploadStatus.Error;
                    if (item.uploadStatus == UploadStatus.Done)
                    {
                        LogToConsole($"Bundle compilation complete for '{item.displayName}'!");
                    }
                    else
                    {
                        LogToConsole($"Bundle compilation failed for '{item.displayName}'!", "ERROR");
                    }
                }
                else
                {
                    LogToConsole($"Upload failed for '{item.displayName}': {msg}", "ERROR");
                }
                Repaint();
            }

            isBusy = false;
            Repaint();

            int uploaded = toUpload.Count(i => i.uploadStatus == UploadStatus.Done);
            LogToConsole($"Upload job finished. Successfully processed {uploaded}/{toUpload.Count} assets.");
            EditorUtility.DisplayDialog("Upload Complete",
                $"Uploaded {uploaded}/{toUpload.Count} assets.", "OK");
        }

        // ── CreateSamplePrefab ────────────────────────────────────────────

        private void CreateSamplePrefab()
        {
            string samplePath = SDKPathUtility.GetPath("Samples");
            if (!Directory.Exists(samplePath)) Directory.CreateDirectory(samplePath);

            GameObject cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            cube.name = "SampleInteractable";
            var bridge = cube.AddComponent<ScenarioBridge>();

            string luaContent =
@"-- Sample Interactable Script

function OnStart()
end

function OnInteract(userId)
end
";
            string luaPath = $"{samplePath}/SampleInteractable.lua.txt";
            File.WriteAllText(luaPath, luaContent);
            AssetDatabase.Refresh();

            bridge.luaScript = AssetDatabase.LoadAssetAtPath<TextAsset>(luaPath);
            string prefabPath = $"{samplePath}/SampleInteractable.prefab";
            PrefabUtility.SaveAsPrefabAsset(cube, prefabPath);
            DestroyImmediate(cube);
            AssetDatabase.Refresh();

            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            AddPrefabToList(prefab);

            EditorUtility.DisplayDialog("Sample Created",
                $"Prefab created at:\n{prefabPath}\n\nAdded to the list.", "OK");

            Selection.activeObject = prefab;
            EditorGUIUtility.PingObject(prefab);
        }
    }

    // ── Visual element extensions ─────────────────────────────────────────

    internal static class VEExtensions
    {
        public static T WithClass<T>(this T el, params string[] classes) where T : VisualElement
        {
            foreach (var c in classes) el.AddToClassList(c);
            return el;
        }

        public static void SetDisplay(this VisualElement el, bool visible) =>
            el.style.display = visible ? DisplayStyle.Flex : DisplayStyle.None;
    }
}