using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
using VRC.Core;
using VRC.SDK3.Avatars.Components;

namespace AvatarTools.Editor
{
    public class BlueprintIdManager : EditorWindow
    {
        static readonly Regex BlueprintIdPattern = new Regex(
            "^avtr_[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$");

        List<VRCAvatarDescriptor> avatars = new List<VRCAvatarDescriptor>();
        readonly Dictionary<VRCAvatarDescriptor, string> pendingIds = new Dictionary<VRCAvatarDescriptor, string>();
        Vector2 scroll;

        [MenuItem(AvatarToolsUtil.MenuRoot + "Setup/Blueprint ID Manager")]
        static void Open()
        {
            GetWindow<BlueprintIdManager>("Blueprint IDs");
        }

        void OnEnable() => Refresh();
        void OnFocus() => Refresh();
        void OnHierarchyChange()
        {
            Refresh();
            Repaint();
        }

        void Refresh()
        {
            avatars = FindAvatars();
        }

        public static List<VRCAvatarDescriptor> FindAvatars()
        {
            var result = new List<VRCAvatarDescriptor>();
            for (var i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (!scene.isLoaded) continue;
                foreach (var root in scene.GetRootGameObjects())
                    result.AddRange(root.GetComponentsInChildren<VRCAvatarDescriptor>(true));
            }
            return result;
        }

        public static string GetBlueprintId(VRCAvatarDescriptor avatar)
        {
            var pipeline = avatar.GetComponent<PipelineManager>();
            return pipeline != null ? pipeline.blueprintId ?? "" : "";
        }

        public static bool IsValidBlueprintId(string id)
        {
            return id != null && BlueprintIdPattern.IsMatch(id.Trim());
        }

        public static void SetBlueprintId(VRCAvatarDescriptor avatar, string id)
        {
            var pipeline = avatar.GetComponent<PipelineManager>();
            if (pipeline == null) pipeline = Undo.AddComponent<PipelineManager>(avatar.gameObject);
            Undo.RecordObject(pipeline, string.IsNullOrEmpty(id) ? "Clear Blueprint ID" : "Set Blueprint ID");
            pipeline.blueprintId = id ?? "";
            EditorUtility.SetDirty(pipeline);
        }

        void OnGUI()
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField($"{avatars.Count} avatars in open scenes", EditorStyles.boldLabel);
                if (GUILayout.Button("Refresh", GUILayout.Width(80))) Refresh();
            }
            avatars.RemoveAll(a => a == null);
            if (avatars.Count == 0)
            {
                EditorGUILayout.HelpBox("No VRC Avatar Descriptors in the open scenes.", MessageType.Info);
                return;
            }

            var duplicates = avatars.Select(GetBlueprintId).Where(id => id.Length > 0)
                .GroupBy(id => id).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet();
            if (duplicates.Count > 0)
                EditorGUILayout.HelpBox("Some avatars share a blueprint ID. Uploading one will overwrite the other.", MessageType.Warning);

            scroll = EditorGUILayout.BeginScrollView(scroll);
            foreach (var avatar in avatars.ToList()) DrawRow(avatar, duplicates);
            EditorGUILayout.EndScrollView();
        }

        void DrawRow(VRCAvatarDescriptor avatar, HashSet<string> duplicates)
        {
            var id = GetBlueprintId(avatar);
            EditorGUILayout.Space();
            using (new EditorGUILayout.HorizontalScope())
            {
                var name = avatar.gameObject.name + (avatar.gameObject.activeInHierarchy ? "" : "  (inactive)");
                EditorGUILayout.LabelField(name, EditorStyles.boldLabel);
                if (GUILayout.Button("Select", EditorStyles.miniButton, GUILayout.Width(60)))
                {
                    Selection.activeGameObject = avatar.gameObject;
                    EditorGUIUtility.PingObject(avatar.gameObject);
                }
            }
            using (new EditorGUI.IndentLevelScope())
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (id.Length > 0)
                        EditorGUILayout.SelectableLabel(id, GUILayout.Height(EditorGUIUtility.singleLineHeight));
                    else
                        EditorGUILayout.LabelField("No blueprint ID (uploads as a new avatar)");
                    using (new EditorGUI.DisabledScope(id.Length == 0))
                    {
                        if (GUILayout.Button("Copy ID", EditorStyles.miniButton, GUILayout.Width(60)))
                            EditorGUIUtility.systemCopyBuffer = id;
                        if (GUILayout.Button("Clear ID", EditorStyles.miniButton, GUILayout.Width(60)) &&
                            EditorUtility.DisplayDialog("Clear Blueprint ID",
                                $"Detach \"{avatar.gameObject.name}\" from {id}? The next upload will create a new avatar.", "Clear", "Cancel"))
                            SetBlueprintId(avatar, "");
                    }
                }
                if (duplicates.Contains(id))
                    EditorGUILayout.LabelField("Shared with another avatar in the scene", EditorStyles.miniLabel);

                pendingIds.TryGetValue(avatar, out var pending);
                using (new EditorGUILayout.HorizontalScope())
                {
                    pending = EditorGUILayout.TextField("New ID", pending ?? "");
                    pendingIds[avatar] = pending;
                    using (new EditorGUI.DisabledScope(!IsValidBlueprintId(pending)))
                    {
                        if (GUILayout.Button("Set ID", EditorStyles.miniButton, GUILayout.Width(60)))
                        {
                            SetBlueprintId(avatar, pending.Trim());
                            pendingIds.Remove(avatar);
                            pending = "";
                            GUI.FocusControl(null);
                        }
                    }
                }
                if (!string.IsNullOrWhiteSpace(pending) && !IsValidBlueprintId(pending))
                    EditorGUILayout.HelpBox("Expected avtr_ followed by a GUID, e.g. avtr_xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx.", MessageType.Error);
            }
        }
    }
}
