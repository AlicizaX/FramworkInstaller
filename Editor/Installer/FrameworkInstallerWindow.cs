using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.PackageManager;
using UnityEditor.PackageManager.Requests;
using UnityEngine;

namespace AlicizaX.Installer.Editor
{
    public sealed class FrameworkInstallerWindow : EditorWindow
    {
        private const string MenuPath = "AlicizaX/Installer";
        private const string InstallerPath = "Packages/com.alicizax.unity.installer/Editor/Installer";
        private const string TemplatesPath = InstallerPath + "/Templates~";
        private const string NormalTemplateName = "NormalTemplate";
        private const string HybridTemplateName = "HybridTemplate";
        private const string NormalTemplatePath = TemplatesPath + "/" + NormalTemplateName;
        private const string HybridTemplatePath = TemplatesPath + "/" + HybridTemplateName;

        private const string CorePackageName = "com.alicizax.unity.framework";
        private const string UrpPackageName = "com.unity.render-pipelines.universal";
        private const string HybridClrPackageName = "com.code-philosophy.hybridclr";
        private const string EnableLogSymbol = "ENABLE_LOG";
        private const string EnableHybridClrSymbol = "ENABLE_HYBRIDCLR";

        private const string RequiredRegistryName = "AlicizaX";
        private const string RequiredRegistryUrl = "https://package.openupm.com";
        private const string RequiredRegistryScopeCysharp = "com.cysharp";
        private const string RequiredRegistryScopeAlicizaX = "com.alicizax.unity";
        private const string RequiredRegistryScopeTuyooGame = "com.tuyoogame";

        private const string ManifestPath = "Packages/manifest.json";
        private const string InstallStatePath = "ProjectSettings/AlicizaXFrameworkInstaller.json";

        private static readonly string[] RequiredRegistryScopes =
        {
            RequiredRegistryScopeCysharp,
            RequiredRegistryScopeAlicizaX,
            RequiredRegistryScopeTuyooGame
        };

        private static readonly string[] RuntimeAssetMarkers =
        {
            "Assets/Scripts/Startup",
            "Assets/Bundles",
            "Assets/YooAsset"
        };

        private static readonly string[] HybridAssetMarkers =
        {
            "Assets/Scripts/Hotfix",
            "Assets/HybridCLRGenerate",
            "Assets/Bundles/DLL"
        };

        private InstallCheckResult _checkResult;
        private TemplateType _selectedTemplate;
        private Vector2 _scrollPosition;
        private Request _registryResolveRequest;
        private AddRequest _installCoreRequest;
        private string _startupMessage;
        private string _startupError;
        private bool _registryReady;

        private enum TemplateType
        {
            Normal,
            Hybrid
        }

        private enum ProjectInstallState
        {
            NotInstalled,
            Custom,
            NormalTemplate,
            HybridTemplate
        }

        [MenuItem(MenuPath, false, -3000)]
        private static void OpenWindow()
        {
            FrameworkInstallerWindow window = GetWindow<FrameworkInstallerWindow>();
            window.titleContent = new GUIContent("AlicizaX Installer", EditorGUIUtility.IconContent("Package Manager").image);
            window.minSize = new Vector2(560f, 460f);
            window.Show();
        }

        private void OnEnable()
        {
            EnsureRegistryBeforeDisplay();
        }

        private void OnDisable()
        {
            EditorApplication.update -= MonitorRegistryResolve;
            EditorApplication.update -= MonitorCoreInstall;
        }

        private void OnGUI()
        {
            if (!_registryReady)
            {
                DrawStartupPanel();
                return;
            }

            EnsureCheckResult();

            _scrollPosition = EditorGUILayout.BeginScrollView(_scrollPosition);
            DrawHeader();
            DrawEnvironmentPanel();
            DrawCorePanel();
            DrawTemplatePanel();
            DrawActionPanel();
            EditorGUILayout.EndScrollView();
        }

        private void EnsureRegistryBeforeDisplay()
        {
            _registryReady = false;
            _startupError = string.Empty;
            _startupMessage = "Checking OpenUPM scoped registry...";

            if (!EnsureRequiredScopedRegistry(out string error, out bool changed))
            {
                _startupError = error;
                Repaint();
                return;
            }

            if (changed)
            {
                _startupMessage = "OpenUPM scoped registry was updated. Waiting for Package Manager to resolve...";
                Client.Resolve();
                _registryResolveRequest = Client.List();
                EditorApplication.update -= MonitorRegistryResolve;
                EditorApplication.update += MonitorRegistryResolve;
                Repaint();
                return;
            }

            _registryReady = true;
            RunInstallCheck();
        }

        private void MonitorRegistryResolve()
        {
            if (_registryResolveRequest == null || !_registryResolveRequest.IsCompleted)
            {
                return;
            }

            EditorApplication.update -= MonitorRegistryResolve;
            if (_registryResolveRequest.Status == StatusCode.Failure)
            {
                _startupError = "Package Manager resolve failed: " + _registryResolveRequest.Error.message;
                _registryResolveRequest = null;
                Repaint();
                return;
            }

            _registryResolveRequest = null;
            _registryReady = true;
            AssetDatabase.Refresh();
            RunInstallCheck();
        }

        private void DrawStartupPanel()
        {
            EditorGUILayout.Space(8f);
            using (new InstallerGui.BoxGroupScope("AlicizaX Installer", 26f))
            {
                if (!string.IsNullOrEmpty(_startupError))
                {
                    InstallerGui.HelpBox(_startupError, MessageType.Error);
                    if (GUILayout.Button("Retry", InstallerGui.InlineButton, GUILayout.Width(120f)))
                    {
                        EnsureRegistryBeforeDisplay();
                    }

                    return;
                }

                InstallerGui.HelpBox(_startupMessage, MessageType.Info);
            }
        }

        private void DrawHeader()
        {
            using (new InstallerGui.BoxGroupScope("AlicizaX Framework Installer", 26f))
            {
                EditorGUILayout.LabelField("Install Core first, then import a project template.", InstallerGui.MutedLabel);
            }
        }

        private void DrawEnvironmentPanel()
        {
            using (new InstallerGui.BoxGroupScope("Environment", 24f))
            {
                DrawStatusRow("OpenUPM registry", _checkResult.HasRequiredScopedRegistry, _checkResult.RequiredScopedRegistryText);
                DrawStatusRow("Unity 2022.3 or newer", _checkResult.UnityVersionSupported, _checkResult.UnityVersion);
                DrawStatusRow("Core package", _checkResult.HasCorePackage, _checkResult.CorePackageText);
                DrawStatusRow("URP package", _checkResult.HasUrp, _checkResult.UrpVersionText, MessageType.Warning);
                DrawStatusRow("HybridCLR package", _checkResult.HasHybridClr, _checkResult.HybridClrVersionText, MessageType.Warning);
                DrawStatusRow("Installer state", _checkResult.ProjectState != ProjectInstallState.NotInstalled, _checkResult.StateText, MessageType.Warning);
                DrawStatusRow("State source", true, _checkResult.StateSource);
                DrawStatusRow("Normal template folder", _checkResult.HasNormalTemplate, NormalTemplatePath);
                DrawStatusRow("Hybrid template folder", _checkResult.HasHybridTemplate, HybridTemplatePath, MessageType.Warning);
            }
        }

        private void DrawCorePanel()
        {
            using (new InstallerGui.BoxGroupScope("Core", 24f))
            {
                if (_checkResult.HasCorePackage)
                {
                    InstallerGui.HelpBox("AlicizaX Framework is installed. Template installation is available.", MessageType.Info);
                    return;
                }

                InstallerGui.HelpBox("Install Core before importing templates. This installs " + CorePackageName + " and lets Unity resolve its dependencies.", MessageType.Warning);
                using (new EditorGUI.DisabledScope(_installCoreRequest != null))
                {
                    string label = _installCoreRequest == null ? "Install Core" : "Installing Core...";
                    if (GUILayout.Button(label, InstallerGui.InlineButton, GUILayout.Width(160f)))
                    {
                        InstallCorePackage();
                    }
                }
            }
        }

        private void DrawTemplatePanel()
        {
            using (new InstallerGui.BoxGroupScope("Template", 24f))
            {
                if (!_checkResult.HasCorePackage)
                {
                    InstallerGui.HelpBox("Install Core first. Template options are locked until " + CorePackageName + " is installed.", MessageType.Info);
                    return;
                }

                if (_checkResult.ProjectState == ProjectInstallState.Custom)
                {
                    InstallerGui.HelpBox("Project is marked as custom/no template required. Template import is disabled by persisted state.", MessageType.Info);
                    return;
                }

                if (_checkResult.ProjectState == ProjectInstallState.HybridTemplate)
                {
                    InstallerGui.HelpBox("Hybrid template is already initialized. Installer is locked to avoid overwriting project files.", MessageType.Info);
                    return;
                }

                if (_checkResult.ProjectState == ProjectInstallState.NormalTemplate)
                {
                    InstallerGui.HelpBox("Normal template is initialized. You can upgrade to Hybrid template after HybridCLR is installed.", MessageType.Info);
                    using (new EditorGUI.DisabledScope(!_checkResult.HasHybridClr || !_checkResult.HasHybridTemplate))
                    {
                        _selectedTemplate = TemplateType.Hybrid;
                        DrawTemplateChoice("Hybrid Template", true, "Upgrade current project to hot update template.");
                    }

                    return;
                }

                DrawTemplateChoice("Normal Template", _selectedTemplate == TemplateType.Normal, "Standalone framework template. Adds ENABLE_LOG.");

                using (new EditorGUI.DisabledScope(!_checkResult.HasHybridClr))
                {
                    bool selected = _selectedTemplate == TemplateType.Hybrid;
                    bool nextSelected = DrawTemplateChoice("Hybrid Template", selected, "Hot update framework template. Adds ENABLE_LOG and ENABLE_HYBRIDCLR.");
                    if (nextSelected && !selected)
                    {
                        _selectedTemplate = TemplateType.Hybrid;
                    }
                }

                if (!_checkResult.HasHybridClr)
                {
                    InstallerGui.HelpBox("Hybrid template requires HybridCLR package.", MessageType.Warning);
                }
            }
        }

        private void DrawActionPanel()
        {
            using (new InstallerGui.BoxGroupScope("Actions", 24f))
            {
                bool canInstall = CanInstallSelectedTemplate(out string blockReason);
                if (!string.IsNullOrEmpty(blockReason))
                {
                    InstallerGui.HelpBox(blockReason, MessageType.Warning);
                }

                EditorGUILayout.BeginHorizontal();
                if (GUILayout.Button("Refresh Check", InstallerGui.InlineButton, GUILayout.Width(130f)))
                {
                    RunInstallCheck();
                }

                if (_checkResult.ProjectState == ProjectInstallState.NotInstalled)
                {
                    using (new EditorGUI.DisabledScope(!_checkResult.HasCorePackage))
                    {
                        if (GUILayout.Button("Use Custom", InstallerGui.InlineButton, GUILayout.Width(120f)))
                        {
                            SaveInstallState(ProjectInstallState.Custom);
                            RunInstallCheck();
                        }
                    }
                }

                GUILayout.FlexibleSpace();

                using (new EditorGUI.DisabledScope(!canInstall))
                {
                    string label = _checkResult.ProjectState == ProjectInstallState.NormalTemplate ? "Upgrade Template" : "Install Template";
                    if (GUILayout.Button(label, InstallerGui.InlineButton, GUILayout.Width(180f)))
                    {
                        InstallSelectedTemplate();
                    }
                }

                EditorGUILayout.EndHorizontal();
            }
        }

        private bool DrawTemplateChoice(string title, bool selected, string description)
        {
            EditorGUILayout.BeginVertical(InstallerGui.EntryBody);
            EditorGUILayout.BeginHorizontal();

            bool nextSelected = GUILayout.Toggle(selected, GUIContent.none, GUILayout.Width(18f));
            EditorGUILayout.LabelField(title, InstallerGui.RowLabel);

            EditorGUILayout.EndHorizontal();
            EditorGUILayout.LabelField(description, InstallerGui.MutedMiniLabel);
            EditorGUILayout.EndVertical();

            if (nextSelected)
            {
                _selectedTemplate = title.StartsWith("Hybrid", StringComparison.Ordinal) ? TemplateType.Hybrid : TemplateType.Normal;
            }

            return nextSelected;
        }

        private void DrawStatusRow(string label, bool success, string message, MessageType failedType = MessageType.Error)
        {
            EditorGUILayout.BeginHorizontal(InstallerGui.FieldRow);
            GUIContent icon = success ? InstallerGui.GreenLight : InstallerGui.RedLight;
            GUILayout.Label(icon, GUILayout.Width(22f), GUILayout.Height(18f));
            EditorGUILayout.LabelField(label, InstallerGui.FieldLabel, GUILayout.Width(160f));
            GUIStyle valueStyle = success ? InstallerGui.RowLabel : failedType == MessageType.Warning ? InstallerGui.WarningLabel : InstallerGui.WarningLabel;
            EditorGUILayout.LabelField(message, valueStyle);
            EditorGUILayout.EndHorizontal();
        }

        private void EnsureCheckResult()
        {
            if (_checkResult == null)
            {
                RunInstallCheck();
            }
        }

        private void RunInstallCheck()
        {
            _checkResult = InstallCheckResult.Create();

            if (_checkResult.ProjectState == ProjectInstallState.NotInstalled)
            {
                _selectedTemplate = _checkResult.HasHybridClr ? TemplateType.Hybrid : TemplateType.Normal;
            }
            else if (_checkResult.ProjectState == ProjectInstallState.NormalTemplate)
            {
                _selectedTemplate = TemplateType.Hybrid;
            }
            else
            {
                _selectedTemplate = TemplateType.Normal;
            }

            Repaint();
        }

        private bool CanInstallSelectedTemplate(out string blockReason)
        {
            blockReason = string.Empty;

            if (!_checkResult.HasRequiredScopedRegistry)
            {
                blockReason = "OpenUPM scoped registry is still being configured.";
                return false;
            }

            if (!_checkResult.UnityVersionSupported)
            {
                blockReason = "Unity version must be 2022.3.x or newer.";
                return false;
            }

            if (!_checkResult.HasCorePackage)
            {
                blockReason = "Install Core before importing templates.";
                return false;
            }

            if (!_checkResult.HasUrp)
            {
                blockReason = "URP package is required before installing AlicizaX framework templates.";
                return false;
            }

            if (_checkResult.ProjectState == ProjectInstallState.Custom)
            {
                blockReason = "Project is marked as custom/no template required.";
                return false;
            }

            if (_checkResult.ProjectState == ProjectInstallState.HybridTemplate)
            {
                blockReason = "Hybrid template is already initialized.";
                return false;
            }

            if (_checkResult.ProjectState == ProjectInstallState.NormalTemplate && _selectedTemplate != TemplateType.Hybrid)
            {
                blockReason = "Normal template is already initialized and cannot be overwritten.";
                return false;
            }

            if (_selectedTemplate == TemplateType.Hybrid && !_checkResult.HasHybridClr)
            {
                blockReason = "Hybrid template requires HybridCLR package.";
                return false;
            }

            string templatePath = GetSelectedTemplatePath();
            if (!Directory.Exists(templatePath))
            {
                blockReason = "Template folder is missing: " + templatePath;
                return false;
            }

            return true;
        }

        private void InstallCorePackage()
        {
            if (!EnsureRequiredScopedRegistry(out string registryError, out bool changed))
            {
                EditorUtility.DisplayDialog("AlicizaX Installer", registryError, "OK");
                EnsureRegistryBeforeDisplay();
                return;
            }

            if (changed)
            {
                EnsureRegistryBeforeDisplay();
                return;
            }

            if (_installCoreRequest != null)
            {
                return;
            }

            _installCoreRequest = Client.Add(CorePackageName);
            EditorApplication.update -= MonitorCoreInstall;
            EditorApplication.update += MonitorCoreInstall;
            Repaint();
        }

        private void MonitorCoreInstall()
        {
            if (_installCoreRequest == null || !_installCoreRequest.IsCompleted)
            {
                return;
            }

            EditorApplication.update -= MonitorCoreInstall;
            AddRequest request = _installCoreRequest;
            _installCoreRequest = null;

            if (request.Status == StatusCode.Failure)
            {
                EditorUtility.DisplayDialog("AlicizaX Installer", "Failed to install Core: " + request.Error.message, "OK");
                RunInstallCheck();
                return;
            }

            AssetDatabase.Refresh();
            RunInstallCheck();
            EditorUtility.DisplayDialog("AlicizaX Installer", "Core package installed. You can now import a template.", "OK");
        }

        private void InstallSelectedTemplate()
        {
            if (!CanInstallSelectedTemplate(out string blockReason))
            {
                EditorUtility.DisplayDialog("AlicizaX Installer", blockReason, "OK");
                RunInstallCheck();
                return;
            }

            string templatePath = GetSelectedTemplatePath();
            string prompt = _checkResult.ProjectState == ProjectInstallState.NormalTemplate
                ? "Upgrade the current project to the Hybrid template?"
                : "Install the selected template into the current project?";

            if (!EditorUtility.DisplayDialog("AlicizaX Installer", prompt + "\n\n" + templatePath, "Install", "Cancel"))
            {
                return;
            }

            if (_selectedTemplate == TemplateType.Hybrid && !ConfirmHybridPlayerSettings())
            {
                return;
            }

            CopyTemplateDirectory(templatePath);
            ApplyPlayerSettings();
            ApplyScriptingDefineSymbols(_selectedTemplate);
            SaveInstallState(_selectedTemplate == TemplateType.Hybrid ? ProjectInstallState.HybridTemplate : ProjectInstallState.NormalTemplate);
            AssetDatabase.Refresh();
            RunInstallCheck();

            EditorUtility.DisplayDialog("AlicizaX Installer", "Template installation complete.", "OK");
        }

        private static bool EnsureRequiredScopedRegistry(out string error, out bool changed)
        {
            error = string.Empty;
            changed = false;

            if (HasScopedRegistry(RequiredRegistryUrl, RequiredRegistryScopes))
            {
                return true;
            }

            try
            {
                if (!File.Exists(ManifestPath))
                {
                    error = "Package manifest is missing: " + ManifestPath;
                    return false;
                }

                string manifest = File.ReadAllText(ManifestPath);
                string nextManifest = AddOrUpdateScopedRegistry(manifest, RequiredRegistryName, RequiredRegistryUrl, RequiredRegistryScopes);

                if (string.Equals(manifest, nextManifest, StringComparison.Ordinal))
                {
                    error = "Failed to update OpenUPM scoped registry in " + ManifestPath + ".";
                    return false;
                }

                File.WriteAllText(ManifestPath, nextManifest);
                changed = true;
                Debug.Log("AlicizaX installer updated OpenUPM scoped registry in " + ManifestPath + ".");
                return true;
            }
            catch (Exception ex)
            {
                error = "Failed to update Package Manager scoped registry: " + ex.Message;
                return false;
            }
        }

        private static string AddOrUpdateScopedRegistry(string manifest, string registryName, string registryUrl, params string[] requiredScopes)
        {
            Match registryMatch = Regex.Match(
                manifest,
                "\\{\\s*\"name\"\\s*:\\s*\"[^\"]*\"\\s*,\\s*\"url\"\\s*:\\s*\"" + Regex.Escape(registryUrl) + "\"\\s*,\\s*\"scopes\"\\s*:\\s*\\[(?<scopes>[\\s\\S]*?)\\]\\s*\\}",
                RegexOptions.Singleline);

            if (registryMatch.Success)
            {
                string scopeBlock = registryMatch.Groups["scopes"].Value;
                string registryJson = BuildScopedRegistryJson(
                    registryName,
                    registryUrl,
                    MergeScopes(scopeBlock, requiredScopes),
                    GetRegistryIndent(manifest, registryMatch.Index));

                return manifest.Substring(0, registryMatch.Index) +
                       registryJson +
                       manifest.Substring(registryMatch.Index + registryMatch.Length);
            }

            string newRegistryJson = BuildScopedRegistryJson(registryName, registryUrl, requiredScopes, "    ");
            Match scopedRegistriesMatch = Regex.Match(manifest, "\"scopedRegistries\"\\s*:\\s*\\[(?<content>[\\s\\S]*?)\\]\\s*(?=\\n\\s*\\})", RegexOptions.Singleline);
            if (scopedRegistriesMatch.Success)
            {
                string content = scopedRegistriesMatch.Groups["content"].Value;
                string nextContent = string.IsNullOrWhiteSpace(content)
                    ? "\n" + newRegistryJson + "\n  "
                    : content.TrimEnd() + ",\n" + newRegistryJson + "\n  ";

                return manifest.Substring(0, scopedRegistriesMatch.Groups["content"].Index) +
                       nextContent +
                       manifest.Substring(scopedRegistriesMatch.Groups["content"].Index + scopedRegistriesMatch.Groups["content"].Length);
            }

            int insertIndex = manifest.LastIndexOf('}');
            if (insertIndex < 0)
            {
                return manifest;
            }

            string prefix = manifest.Substring(0, insertIndex).TrimEnd();
            string suffix = manifest.Substring(insertIndex);
            string separator = prefix.EndsWith("{", StringComparison.Ordinal) ? "\n" : ",\n";
            return prefix + separator + "  \"scopedRegistries\": [\n" + newRegistryJson + "\n  ]\n" + suffix;
        }

        private static string[] MergeScopes(string scopeBlock, string[] requiredScopes)
        {
            string[] existingScopes = Regex.Matches(scopeBlock, "\"(?<scope>[^\"]+)\"")
                .Cast<Match>()
                .Select(match => match.Groups["scope"].Value)
                .ToArray();

            string[] mergedScopes = new string[existingScopes.Length + requiredScopes.Length];
            int count = 0;

            foreach (string scope in existingScopes)
            {
                if (ContainsScope(mergedScopes, count, scope))
                {
                    continue;
                }

                mergedScopes[count++] = scope;
            }

            foreach (string scope in requiredScopes)
            {
                if (ContainsScope(mergedScopes, count, scope))
                {
                    continue;
                }

                mergedScopes[count++] = scope;
            }

            Array.Resize(ref mergedScopes, count);
            return mergedScopes;
        }

        private static bool ContainsScope(string[] scopes, int count, string scope)
        {
            for (int i = 0; i < count; i++)
            {
                if (string.Equals(scopes[i], scope, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        private static string GetRegistryIndent(string manifest, int registryIndex)
        {
            int lineStart = manifest.LastIndexOf('\n', Math.Max(0, registryIndex - 1));
            if (lineStart < 0)
            {
                return "    ";
            }

            int indentStart = lineStart + 1;
            int indentLength = 0;
            while (indentStart + indentLength < manifest.Length && char.IsWhiteSpace(manifest[indentStart + indentLength]))
            {
                indentLength++;
            }

            return indentLength > 0 ? manifest.Substring(indentStart, indentLength) : "    ";
        }

        private static string BuildScopedRegistryJson(string registryName, string registryUrl, string[] scopes, string indent)
        {
            string scopeIndent = indent + "    ";
            string json = indent + "{\n" +
                          indent + "  \"name\": \"" + registryName + "\",\n" +
                          indent + "  \"url\": \"" + registryUrl + "\",\n" +
                          indent + "  \"scopes\": [";

            for (int i = 0; i < scopes.Length; i++)
            {
                json += "\n" + scopeIndent + "\"" + scopes[i] + "\"";
                if (i < scopes.Length - 1)
                {
                    json += ",";
                }
            }

            return json + "\n" + indent + "  ]\n" + indent + "}";
        }

        private string GetSelectedTemplatePath()
        {
            return _selectedTemplate == TemplateType.Hybrid ? HybridTemplatePath : NormalTemplatePath;
        }

        private static void CopyTemplateDirectory(string templatePath)
        {
            string sourceRoot = Path.GetFullPath(templatePath);
            string targetRoot = Application.dataPath;

            foreach (string sourceDirectory in Directory.GetDirectories(sourceRoot, "*", SearchOption.AllDirectories))
            {
                string relativeDirectory = GetRelativePath(sourceRoot, sourceDirectory);
                Directory.CreateDirectory(Path.Combine(targetRoot, relativeDirectory));
            }

            foreach (string sourceFile in Directory.GetFiles(sourceRoot, "*", SearchOption.AllDirectories))
            {
                if (IsTemplatePlaceholder(sourceFile))
                {
                    continue;
                }

                string relativeFile = GetRelativePath(sourceRoot, sourceFile);
                string targetFile = Path.Combine(targetRoot, relativeFile);
                string targetDirectory = Path.GetDirectoryName(targetFile);
                if (!string.IsNullOrEmpty(targetDirectory))
                {
                    Directory.CreateDirectory(targetDirectory);
                }

                if (File.Exists(targetFile))
                {
                    Debug.LogWarning("AlicizaX installer skipped existing file: " + relativeFile);
                    continue;
                }

                File.Copy(sourceFile, targetFile);
            }
        }

        private static bool IsTemplatePlaceholder(string filePath)
        {
            string fileName = Path.GetFileName(filePath);
            if (string.Equals(fileName, ".keep", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(fileName, ".gitkeep", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            string fileNameWithoutMeta = fileName;
            if (fileName.EndsWith(".meta", StringComparison.OrdinalIgnoreCase))
            {
                fileNameWithoutMeta = Path.GetFileNameWithoutExtension(fileName);
            }

            return string.Equals(fileNameWithoutMeta, ".keep", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(fileNameWithoutMeta, ".gitkeep", StringComparison.OrdinalIgnoreCase);
        }

        private static string GetRelativePath(string rootPath, string path)
        {
            Uri rootUri = new Uri(AppendDirectorySeparatorChar(rootPath));
            Uri pathUri = new Uri(path);
            return Uri.UnescapeDataString(rootUri.MakeRelativeUri(pathUri).ToString()).Replace('/', Path.DirectorySeparatorChar);
        }

        private static string AppendDirectorySeparatorChar(string path)
        {
            return path.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal)
                ? path
                : path + Path.DirectorySeparatorChar;
        }

        private static void ApplyScriptingDefineSymbols(TemplateType templateType)
        {
            ScriptingDefineSymbolUtility.AddScriptingDefineSymbol(EnableLogSymbol);

            if (templateType == TemplateType.Hybrid)
            {
                ScriptingDefineSymbolUtility.AddScriptingDefineSymbol(EnableHybridClrSymbol);
            }
        }

        private static void ApplyPlayerSettings()
        {
            if (!PlayerSettings.allowUnsafeCode)
            {
                PlayerSettings.allowUnsafeCode = true;
            }
        }

        private static bool ConfirmHybridPlayerSettings()
        {
            int option = EditorUtility.DisplayDialogComplex(
                "AlicizaX Installer",
                "Installing the Hybrid template requires these Player Settings:\n\n" +
                "1. Scripting Backend: IL2CPP.\n" +
                "2. Api Compatibility Level: .NET Framework.\n" +
                "3. Incremental GC: disabled.\n\n" +
                "Apply these settings for the current platform?",
                "Apply",
                "Manual",
                "Cancel");

            if (option == 2)
            {
                return false;
            }

            if (option == 0)
            {
                ApplyHybridPlayerSettings();
            }

            return true;
        }

        private static void ApplyHybridPlayerSettings()
        {
            BuildTargetGroup buildTargetGroup = EditorUserBuildSettings.selectedBuildTargetGroup;
            PlayerSettings.SetScriptingBackend(buildTargetGroup, ScriptingImplementation.IL2CPP);
            PlayerSettings.SetApiCompatibilityLevel(buildTargetGroup, ApiCompatibilityLevel.NET_4_6);
            PlayerSettings.gcIncremental = false;
        }

        private static void SaveInstallState(ProjectInstallState state)
        {
            string json = JsonUtility.ToJson(new InstallStateData
            {
                installerState = state.ToString(),
                template = ToTemplateText(state),
                unityVersion = Application.unityVersion,
                projectPath = Path.GetFullPath("."),
                updatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
            }, true);

            File.WriteAllText(InstallStatePath, json);
        }

        private static ProjectInstallState ReadInstallState(out string source)
        {
            if (File.Exists(InstallStatePath))
            {
                try
                {
                    InstallStateData data = JsonUtility.FromJson<InstallStateData>(File.ReadAllText(InstallStatePath));
                    if (TryParseState(data.installerState, out ProjectInstallState fileState))
                    {
                        ProjectInstallState validatedState = ValidatePersistedInstallState(fileState);
                        source = validatedState == fileState ? InstallStatePath : InstallStatePath + " (missing template assets)";
                        return validatedState;
                    }

                    if (!string.IsNullOrEmpty(data.template) && TryParseLegacyTemplate(data.template, out fileState))
                    {
                        ProjectInstallState validatedState = ValidatePersistedInstallState(fileState);
                        source = validatedState == fileState ? InstallStatePath + " (legacy)" : InstallStatePath + " (legacy, missing template assets)";
                        return validatedState;
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogWarning("Failed to read AlicizaX installer state: " + ex.Message);
                }
            }

            bool hasLogSymbol = ScriptingDefineSymbolUtility.HasScriptingDefineSymbol(EditorUserBuildSettings.selectedBuildTargetGroup, EnableLogSymbol);
            bool hasHybridSymbol = ScriptingDefineSymbolUtility.HasScriptingDefineSymbol(EditorUserBuildSettings.selectedBuildTargetGroup, EnableHybridClrSymbol);

            if (hasHybridSymbol || HasHybridAssetMarkers())
            {
                source = "Compatibility fallback";
                return ProjectInstallState.HybridTemplate;
            }

            if (hasLogSymbol || HasRuntimeAssetMarkers())
            {
                source = "Compatibility fallback";
                return ProjectInstallState.NormalTemplate;
            }

            source = "Default";
            return ProjectInstallState.NotInstalled;
        }

        private static ProjectInstallState ValidatePersistedInstallState(ProjectInstallState state)
        {
            if (state == ProjectInstallState.NormalTemplate && !HasRuntimeAssetMarkers())
            {
                return ProjectInstallState.NotInstalled;
            }

            if (state == ProjectInstallState.HybridTemplate)
            {
                if (HasHybridAssetMarkers())
                {
                    return ProjectInstallState.HybridTemplate;
                }

                return HasRuntimeAssetMarkers() ? ProjectInstallState.NormalTemplate : ProjectInstallState.NotInstalled;
            }

            return state;
        }

        private static bool TryParseState(string value, out ProjectInstallState state)
        {
            if (string.IsNullOrEmpty(value))
            {
                state = ProjectInstallState.NotInstalled;
                return false;
            }

            if (Enum.TryParse(value, true, out state))
            {
                return true;
            }

            state = ProjectInstallState.NotInstalled;
            return false;
        }

        private static bool TryParseLegacyTemplate(string value, out ProjectInstallState state)
        {
            if (string.Equals(value, "Normal", StringComparison.OrdinalIgnoreCase))
            {
                state = ProjectInstallState.NormalTemplate;
                return true;
            }

            if (string.Equals(value, "Hybrid", StringComparison.OrdinalIgnoreCase))
            {
                state = ProjectInstallState.HybridTemplate;
                return true;
            }

            return TryParseState(value, out state);
        }

        private static string ToTemplateText(ProjectInstallState state)
        {
            if (state == ProjectInstallState.NormalTemplate)
            {
                return "Normal";
            }

            if (state == ProjectInstallState.HybridTemplate)
            {
                return "Hybrid";
            }

            if (state == ProjectInstallState.Custom)
            {
                return "Custom";
            }

            return string.Empty;
        }

        private static bool HasRuntimeAssetMarkers()
        {
            foreach (string marker in RuntimeAssetMarkers)
            {
                if (AssetDatabase.IsValidFolder(marker) || File.Exists(marker))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool HasHybridAssetMarkers()
        {
            foreach (string marker in HybridAssetMarkers)
            {
                if (AssetDatabase.IsValidFolder(marker) || File.Exists(marker))
                {
                    return true;
                }
            }

            return false;
        }

        private static string FindManifestDependencyVersion(string packageName)
        {
            if (string.IsNullOrEmpty(packageName) || !File.Exists(ManifestPath))
            {
                return string.Empty;
            }

            string manifest = File.ReadAllText(ManifestPath);
            Match match = Regex.Match(manifest, "\"" + Regex.Escape(packageName) + "\"\\s*:\\s*\"([^\"]+)\"");
            return match.Success ? match.Groups[1].Value : string.Empty;
        }

        private static bool HasScopedRegistry(string url, params string[] scopes)
        {
            if (string.IsNullOrEmpty(url) || scopes == null || scopes.Length == 0 || !File.Exists(ManifestPath))
            {
                return false;
            }

            try
            {
                ManifestData manifestData = JsonUtility.FromJson<ManifestData>(File.ReadAllText(ManifestPath));
                if (manifestData?.scopedRegistries == null)
                {
                    return false;
                }

                foreach (ScopedRegistryData registry in manifestData.scopedRegistries)
                {
                    if (registry == null ||
                        !string.Equals(NormalizeUrl(registry.url), NormalizeUrl(url), StringComparison.OrdinalIgnoreCase) ||
                        registry.scopes == null)
                    {
                        continue;
                    }

                    bool containsAllScopes = true;
                    foreach (string scope in scopes)
                    {
                        if (!Array.Exists(registry.scopes, registryScope => string.Equals(registryScope, scope, StringComparison.Ordinal)))
                        {
                            containsAllScopes = false;
                            break;
                        }
                    }

                    if (containsAllScopes)
                    {
                        return true;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning("Failed to read Package Manager scoped registries: " + ex.Message);
            }

            return false;
        }

        private static string NormalizeUrl(string url)
        {
            return string.IsNullOrEmpty(url) ? string.Empty : url.Trim().TrimEnd('/');
        }

        private sealed class InstallCheckResult
        {
            public ProjectInstallState ProjectState;
            public string StateSource;
            public bool UnityVersionSupported;
            public string UnityVersion;
            public bool HasRequiredScopedRegistry;
            public string RequiredScopedRegistryText;
            public bool HasCorePackage;
            public string CorePackageText;
            public bool HasUrp;
            public string UrpVersionText;
            public bool HasHybridClr;
            public string HybridClrVersionText;
            public bool HasNormalTemplate;
            public bool HasHybridTemplate;

            public string StateText
            {
                get
                {
                    switch (ProjectState)
                    {
                        case ProjectInstallState.Custom:
                            return "Custom / no template required";
                        case ProjectInstallState.NormalTemplate:
                            return "Normal template";
                        case ProjectInstallState.HybridTemplate:
                            return "Hybrid template";
                        default:
                            return "Not initialized";
                    }
                }
            }

            public static InstallCheckResult Create()
            {
                string corePackageVersion = FindCorePackageVersion();
                string urpVersion = FindManifestDependencyVersion(UrpPackageName);
                string hybridClrVersion = FindManifestDependencyVersion(HybridClrPackageName);
                bool hasRequiredScopedRegistry = HasScopedRegistry(RequiredRegistryUrl, RequiredRegistryScopes);
                ProjectInstallState projectState = ReadInstallState(out string stateSource);

                return new InstallCheckResult
                {
                    ProjectState = projectState,
                    StateSource = stateSource,
                    UnityVersionSupported = IsUnityVersionSupported(Application.unityVersion),
                    UnityVersion = Application.unityVersion,
                    HasRequiredScopedRegistry = hasRequiredScopedRegistry,
                    RequiredScopedRegistryText = hasRequiredScopedRegistry
                        ? RequiredRegistryUrl + " (" + string.Join(", ", RequiredRegistryScopes) + ")"
                        : "Missing " + RequiredRegistryUrl + " scopes: " + string.Join(", ", RequiredRegistryScopes),
                    HasCorePackage = !string.IsNullOrEmpty(corePackageVersion),
                    CorePackageText = string.IsNullOrEmpty(corePackageVersion) ? "Not installed" : corePackageVersion,
                    HasUrp = !string.IsNullOrEmpty(urpVersion),
                    UrpVersionText = string.IsNullOrEmpty(urpVersion) ? "Not installed" : urpVersion,
                    HasHybridClr = !string.IsNullOrEmpty(hybridClrVersion),
                    HybridClrVersionText = string.IsNullOrEmpty(hybridClrVersion) ? "Not installed" : hybridClrVersion,
                    HasNormalTemplate = Directory.Exists(NormalTemplatePath),
                    HasHybridTemplate = Directory.Exists(HybridTemplatePath)
                };
            }

            private static string FindCorePackageVersion()
            {
                string manifestVersion = FindManifestDependencyVersion(CorePackageName);
                if (!string.IsNullOrEmpty(manifestVersion))
                {
                    return manifestVersion;
                }

                string embeddedPackageJson = "Packages/" + CorePackageName + "/package.json";
                if (File.Exists(embeddedPackageJson))
                {
                    string packageJson = File.ReadAllText(embeddedPackageJson);
                    Match match = Regex.Match(packageJson, "\"version\"\\s*:\\s*\"([^\"]+)\"");
                    return match.Success ? "Embedded " + match.Groups[1].Value : "Embedded package";
                }

                return string.Empty;
            }

            private static bool IsUnityVersionSupported(string version)
            {
                if (string.IsNullOrEmpty(version))
                {
                    return false;
                }

                string[] parts = version.Split('.');
                if (parts.Length < 2)
                {
                    return false;
                }

                if (!int.TryParse(parts[0], out int major) || !int.TryParse(parts[1], out int minor))
                {
                    return false;
                }

                return major > 2022 || major == 2022 && minor >= 3;
            }
        }

        [Serializable]
        private sealed class InstallStateData
        {
            public string installerState;
            public string template;
            public string unityVersion;
            public string projectPath;
            public string updatedAt;
        }

        [Serializable]
        private sealed class ManifestData
        {
            public ScopedRegistryData[] scopedRegistries;
        }

        [Serializable]
        private sealed class ScopedRegistryData
        {
            public string name;
            public string url;
            public string[] scopes;
        }
    }

    internal static class ScriptingDefineSymbolUtility
    {
        private static readonly BuildTargetGroup[] BuildTargetGroups =
        {
            BuildTargetGroup.Standalone,
            BuildTargetGroup.iOS,
            BuildTargetGroup.Android,
            BuildTargetGroup.WSA,
            BuildTargetGroup.WebGL
        };

        public static bool HasScriptingDefineSymbol(BuildTargetGroup buildTargetGroup, string scriptingDefineSymbol)
        {
            if (string.IsNullOrEmpty(scriptingDefineSymbol))
            {
                return false;
            }

            string[] scriptingDefineSymbols = GetScriptingDefineSymbols(buildTargetGroup);
            foreach (string symbol in scriptingDefineSymbols)
            {
                if (symbol == scriptingDefineSymbol)
                {
                    return true;
                }
            }

            return false;
        }

        public static void AddScriptingDefineSymbol(string scriptingDefineSymbol)
        {
            if (string.IsNullOrEmpty(scriptingDefineSymbol))
            {
                return;
            }

            foreach (BuildTargetGroup buildTargetGroup in BuildTargetGroups)
            {
                AddScriptingDefineSymbol(buildTargetGroup, scriptingDefineSymbol);
            }
        }

        private static void AddScriptingDefineSymbol(BuildTargetGroup buildTargetGroup, string scriptingDefineSymbol)
        {
            if (HasScriptingDefineSymbol(buildTargetGroup, scriptingDefineSymbol))
            {
                return;
            }

            string[] currentSymbols = GetScriptingDefineSymbols(buildTargetGroup);
            string[] nextSymbols = new string[currentSymbols.Length + 1];
            Array.Copy(currentSymbols, nextSymbols, currentSymbols.Length);
            nextSymbols[nextSymbols.Length - 1] = scriptingDefineSymbol;
            SetScriptingDefineSymbols(buildTargetGroup, nextSymbols);
        }

        private static string[] GetScriptingDefineSymbols(BuildTargetGroup buildTargetGroup)
        {
#if UNITY_6000_0_OR_NEWER
            return PlayerSettings.GetScriptingDefineSymbols(NamedBuildTarget.FromBuildTargetGroup(buildTargetGroup)).Split(';');
#else
            return PlayerSettings.GetScriptingDefineSymbolsForGroup(buildTargetGroup).Split(';');
#endif
        }

        private static void SetScriptingDefineSymbols(BuildTargetGroup buildTargetGroup, string[] scriptingDefineSymbols)
        {
#if UNITY_6000_0_OR_NEWER
            PlayerSettings.SetScriptingDefineSymbols(NamedBuildTarget.FromBuildTargetGroup(buildTargetGroup), string.Join(";", scriptingDefineSymbols));
#else
            PlayerSettings.SetScriptingDefineSymbolsForGroup(buildTargetGroup, string.Join(";", scriptingDefineSymbols));
#endif
        }
    }

    internal static class InstallerGui
    {
        private const float DefaultControlHeight = 20f;
        private static GUIStyle _inlineButton;
        private static GUIStyle _entryBody;
        private static GUIStyle _fieldRow;
        private static GUIStyle _rowLabel;
        private static GUIStyle _mutedLabel;
        private static GUIStyle _fieldLabel;
        private static GUIStyle _mutedMiniLabel;
        private static GUIStyle _warningLabel;

        public static GUIContent GreenLight => EditorGUIUtility.TrIconContent("greenLight");
        public static GUIContent RedLight => EditorGUIUtility.TrIconContent("redLight");

        public static GUIStyle InlineButton
        {
            get
            {
                if (_inlineButton == null)
                {
                    _inlineButton = new GUIStyle(EditorStyles.toolbarButton)
                    {
                        alignment = TextAnchor.MiddleCenter,
                        fixedHeight = DefaultControlHeight,
                        padding = new RectOffset(6, 6, 1, 1)
                    };
                }

                return _inlineButton;
            }
        }

        public static GUIStyle EntryBody
        {
            get
            {
                if (_entryBody == null)
                {
                    _entryBody = new GUIStyle(EditorStyles.helpBox)
                    {
                        padding = new RectOffset(8, 8, 6, 8),
                        margin = new RectOffset(0, 0, 0, 4)
                    };
                }

                return _entryBody;
            }
        }

        public static GUIStyle FieldRow
        {
            get
            {
                if (_fieldRow == null)
                {
                    _fieldRow = new GUIStyle(EditorStyles.helpBox)
                    {
                        padding = new RectOffset(5, 5, 3, 3),
                        margin = new RectOffset(0, 0, 1, 1)
                    };
                }

                return _fieldRow;
            }
        }

        public static GUIStyle RowLabel => _rowLabel ?? (_rowLabel = new GUIStyle(EditorStyles.label) { alignment = TextAnchor.MiddleLeft, clipping = TextClipping.Clip });
        public static GUIStyle MutedLabel => _mutedLabel ?? (_mutedLabel = new GUIStyle(EditorStyles.label) { normal = { textColor = Color.gray }, alignment = TextAnchor.MiddleLeft, clipping = TextClipping.Clip });
        public static GUIStyle FieldLabel => _fieldLabel ?? (_fieldLabel = new GUIStyle(EditorStyles.miniLabel) { normal = { textColor = Color.gray }, alignment = TextAnchor.MiddleLeft, clipping = TextClipping.Clip });
        public static GUIStyle MutedMiniLabel => _mutedMiniLabel ?? (_mutedMiniLabel = new GUIStyle(EditorStyles.miniLabel) { normal = { textColor = Color.gray }, alignment = TextAnchor.MiddleLeft, clipping = TextClipping.Clip });
        public static GUIStyle WarningLabel => _warningLabel ?? (_warningLabel = new GUIStyle(EditorStyles.label) { normal = { textColor = new Color(1f, 0.66f, 0.24f, 1f) }, alignment = TextAnchor.MiddleLeft, clipping = TextClipping.Clip });

        public static void HelpBox(string message, MessageType messageType)
        {
            EditorGUILayout.HelpBox(message, messageType);
        }

        public sealed class BoxGroupScope : GUI.Scope
        {
            public BoxGroupScope(string title, float height = 22f)
            {
                EditorGUILayout.BeginVertical(GUI.skin.box);
                Rect headerRect = GUILayoutUtility.GetRect(1, height);
                EditorGUI.DrawRect(headerRect, new Color(0.1f, 0.1f, 0.1f, 0.4f));
                headerRect.x += EditorGUIUtility.standardVerticalSpacing;
                EditorGUI.LabelField(headerRect, title, EditorStyles.boldLabel);
                EditorGUILayout.Space(EditorGUIUtility.standardVerticalSpacing);
            }

            protected override void CloseScope()
            {
                EditorGUILayout.EndVertical();
            }
        }
    }
}
