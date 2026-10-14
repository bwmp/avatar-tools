using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;
using VRC.SDK3.Avatars.Components;

namespace AvatarTools.Editor
{
    public class AvatarBackup : EditorWindow
    {
        public const string BackupRoot = "Assets/AvatarTools/Backups";
        const string StampFormat = "yyyy-MM-dd_HHmmss";
        const string NoteFile = "backup.txt";
        const string NoteHeader = "Note:";

        public class BackupEntry
        {
            public string Folder;
            public string PrefabPath;
            public DateTime Time;
            public string Note;
            public long Bytes;
        }

        public enum RestoreMode { AddAlongside, Replace }

        VRCAvatarDescriptor avatar;
        string avatarFolderName;
        string note = "";
        RestoreMode restoreMode;
        bool detachAssets = true;
        List<BackupEntry> entries;
        Vector2 scroll;

        [MenuItem(AvatarToolsUtil.MenuRoot + "Workflow/Avatar Backup Snapshot")]
        static void Open()
        {
            GetWindow<AvatarBackup>("Avatar Backup");
        }

        [MenuItem(AvatarToolsUtil.MenuRoot + "Workflow/Quick Backup Selected Avatar %#&b")]
        static void QuickBackup()
        {
            var selected = AvatarToolsUtil.SelectedAvatar();
            var folder = Backup(selected, "");
            Debug.Log($"[Avatar Tools] Backed up {selected.gameObject.name} to {folder}");
            EditorGUIUtility.PingObject(AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(folder));
            foreach (var window in Resources.FindObjectsOfTypeAll<AvatarBackup>()) window.Reload();
        }

        [MenuItem(AvatarToolsUtil.MenuRoot + "Workflow/Quick Backup Selected Avatar %#&b", true)]
        static bool QuickBackupValidate()
        {
            var selected = AvatarToolsUtil.SelectedAvatar();
            return selected != null && !EditorUtility.IsPersistent(selected);
        }

        void OnEnable()
        {
            SetAvatar(AvatarToolsUtil.SelectedAvatar());
        }

        void SetAvatar(VRCAvatarDescriptor value)
        {
            avatar = value != null && EditorUtility.IsPersistent(value) ? null : value;
            if (avatar != null) avatarFolderName = AvatarToolsUtil.SafeName(avatar.gameObject.name);
            Reload();
        }

        void Reload()
        {
            entries = string.IsNullOrEmpty(avatarFolderName) ? new List<BackupEntry>() : ListBackups(avatarFolderName);
            Repaint();
        }

        public static string AvatarBackupFolder(string avatarName)
        {
            return BackupRoot + "/" + AvatarToolsUtil.SafeName(avatarName);
        }

        public static string Backup(VRCAvatarDescriptor avatar, string note)
        {
            var now = DateTime.Now;
            var parent = AvatarToolsUtil.EnsureFolder(AvatarBackupFolder(avatar.gameObject.name));
            var target = parent + "/" + now.ToString(StampFormat, CultureInfo.InvariantCulture);
            for (var i = 2; AssetDatabase.IsValidFolder(target); i++)
                target = parent + "/" + now.ToString(StampFormat, CultureInfo.InvariantCulture) + "_" + i;

            var tempName = "__AvatarBackup_" + Guid.NewGuid().ToString("N");
            var copy = AvatarVariantMaker.CreateVariant(avatar, tempName, true, false);
            try
            {
                var tempFolder = AvatarToolsUtil.GeneratedRoot + "/" + AvatarToolsUtil.SafeName(tempName);
                var moveError = AssetDatabase.MoveAsset(tempFolder, target);
                if (!string.IsNullOrEmpty(moveError))
                    throw new InvalidOperationException($"Could not move {tempFolder} to {target}: {moveError}");

                copy.gameObject.name = avatar.gameObject.name;
                var prefabPath = target + "/" + AvatarToolsUtil.SafeName(avatar.gameObject.name) + ".prefab";
                PrefabUtility.SaveAsPrefabAsset(copy.gameObject, prefabPath, out var saved);
                if (!saved) throw new InvalidOperationException("Could not save backup prefab " + prefabPath);
            }
            finally
            {
                Undo.ClearUndo(copy.gameObject);
                DestroyImmediate(copy.gameObject);
            }

            var text = new StringBuilder()
                .AppendLine("Avatar: " + avatar.gameObject.name)
                .AppendLine("Created: " + now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture))
                .AppendLine("Blueprint ID: " + BlueprintIdManager.GetBlueprintId(avatar))
                .AppendLine(NoteHeader)
                .Append(note ?? "");
            var notePath = target + "/" + NoteFile;
            File.WriteAllText(notePath, text.ToString());
            AssetDatabase.ImportAsset(notePath);
            AssetDatabase.SaveAssets();
            return target;
        }

        public static List<string> BackedUpAvatars()
        {
            return AssetDatabase.IsValidFolder(BackupRoot)
                ? AssetDatabase.GetSubFolders(BackupRoot).Select(Path.GetFileName).OrderBy(n => n).ToList()
                : new List<string>();
        }

        public static List<BackupEntry> ListBackups(string avatarFolderName)
        {
            var parent = BackupRoot + "/" + avatarFolderName;
            if (!AssetDatabase.IsValidFolder(parent)) return new List<BackupEntry>();
            return AssetDatabase.GetSubFolders(parent).Select(ReadEntry).OrderByDescending(e => e.Time).ToList();
        }

        static BackupEntry ReadEntry(string folder)
        {
            var directory = new DirectoryInfo(folder);
            var name = directory.Name;
            var stamp = name.Length > StampFormat.Length ? name.Substring(0, StampFormat.Length) : name;
            if (!DateTime.TryParseExact(stamp, StampFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var time))
                time = directory.CreationTime;

            var noteText = "";
            var notePath = Path.Combine(folder, NoteFile);
            if (File.Exists(notePath))
            {
                var lines = File.ReadAllLines(notePath);
                var start = Array.IndexOf(lines, NoteHeader);
                if (start >= 0) noteText = string.Join("\n", lines.Skip(start + 1)).Trim();
            }
            return new BackupEntry
            {
                Folder = folder,
                PrefabPath = AssetDatabase.FindAssets("t:Prefab", new[] { folder }).Select(AssetDatabase.GUIDToAssetPath)
                    .FirstOrDefault(p => Path.GetDirectoryName(p).Replace('\\', '/') == folder),
                Time = time,
                Note = noteText,
                Bytes = directory.Exists ? directory.EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length) : 0
            };
        }

        public static GameObject Restore(BackupEntry entry, VRCAvatarDescriptor current, RestoreMode mode, bool detachAssets)
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(entry.PrefabPath);
            Undo.IncrementCurrentGroup();
            var group = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("Restore Avatar Backup");

            var instance = current != null
                ? (GameObject)PrefabUtility.InstantiatePrefab(prefab, current.gameObject.scene)
                : (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            PrefabUtility.UnpackPrefabInstance(instance, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
            Undo.RegisterCreatedObjectUndo(instance, "Restore Avatar Backup");

            var restoredName = prefab.name + " (Backup " + entry.Time.ToString("yyyy-MM-dd HHmmss", CultureInfo.InvariantCulture) + ")";
            instance.name = restoredName;
            if (current != null)
            {
                instance.transform.SetParent(current.transform.parent, false);
                instance.transform.SetPositionAndRotation(current.transform.position, current.transform.rotation);
                if (mode == RestoreMode.AddAlongside) instance.transform.position += current.transform.right;
                instance.transform.SetSiblingIndex(current.transform.GetSiblingIndex() + 1);
            }

            if (detachAssets)
            {
                var descriptor = instance.GetComponent<VRCAvatarDescriptor>();
                var detached = AvatarVariantMaker.CreateVariant(descriptor, restoredName, true, false).gameObject;
                Undo.DestroyObjectImmediate(instance);
                instance = detached;
            }

            if (current != null && mode == RestoreMode.Replace)
            {
                Undo.RecordObject(instance, "Restore Avatar Backup");
                instance.name = current.gameObject.name;
                Undo.RecordObject(current.gameObject, "Restore Avatar Backup");
                current.gameObject.SetActive(false);
            }
            Undo.CollapseUndoOperations(group);
            return instance;
        }

        public static void Delete(BackupEntry entry)
        {
            if (!AssetDatabase.DeleteAsset(entry.Folder))
                throw new InvalidOperationException("Could not delete " + entry.Folder);
        }

        static string FormatSize(long bytes)
        {
            if (bytes >= 1024 * 1024) return (bytes / (1024f * 1024f)).ToString("0.0") + " MB";
            if (bytes >= 1024) return (bytes / 1024f).ToString("0.0") + " KB";
            return bytes + " B";
        }

        void OnGUI()
        {
            var picked = AvatarToolsUtil.AvatarField(avatar);
            if (picked != avatar) SetAvatar(picked);

            var known = BackedUpAvatars();
            if (!string.IsNullOrEmpty(avatarFolderName) && !known.Contains(avatarFolderName)) known.Insert(0, avatarFolderName);
            if (known.Count > 0)
            {
                var index = Mathf.Max(0, known.IndexOf(avatarFolderName));
                var chosen = EditorGUILayout.Popup("Backups For", index, known.ToArray());
                if (known[chosen] != avatarFolderName)
                {
                    avatarFolderName = known[chosen];
                    Reload();
                }
            }

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("New Backup", EditorStyles.boldLabel);
            if (avatar == null)
                EditorGUILayout.HelpBox("Pick an avatar in the scene to back it up.", MessageType.Info);
            else
            {
                EditorGUILayout.LabelField("Note");
                note = EditorGUILayout.TextArea(note, GUILayout.MinHeight(36));
                EditorGUILayout.HelpBox("Saves an unpacked prefab plus copies of the controllers, clips, menus and parameters into " +
                                        AvatarBackupFolder(avatar.gameObject.name) + "/<timestamp>. Meshes, materials and textures are referenced, not copied.",
                    MessageType.None);
                if (GUILayout.Button("Backup Now", GUILayout.Height(28)))
                {
                    var folder = Backup(avatar, note);
                    note = "";
                    avatarFolderName = AvatarToolsUtil.SafeName(avatar.gameObject.name);
                    Reload();
                    ShowNotification(new GUIContent("Backed up to " + Path.GetFileName(folder)));
                    GUIUtility.ExitGUI();
                }
            }

            EditorGUILayout.Space();
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField("Backups", EditorStyles.boldLabel);
                if (GUILayout.Button("Refresh", GUILayout.Width(70))) Reload();
            }
            if (entries == null || entries.Count == 0)
            {
                EditorGUILayout.HelpBox("No backups yet.", MessageType.Info);
                return;
            }

            restoreMode = (RestoreMode)EditorGUILayout.EnumPopup(new GUIContent("Restore Mode",
                "Add Alongside places the backup next to the current avatar. Replace places it at the same spot and disables the current avatar (it is not deleted)."), restoreMode);
            detachAssets = EditorGUILayout.ToggleLeft(new GUIContent("Copy assets out of the backup on restore",
                "Restored avatar points at fresh copies in " + AvatarToolsUtil.GeneratedRoot + " so later edits do not change the backup."), detachAssets);
            if (avatar == null && restoreMode == RestoreMode.Replace)
                EditorGUILayout.HelpBox("No current avatar picked: the backup will be added at its saved position.", MessageType.Info);

            scroll = EditorGUILayout.BeginScrollView(scroll);
            foreach (var entry in entries)
            {
                using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        EditorGUILayout.LabelField(entry.Time.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture), EditorStyles.boldLabel, GUILayout.Width(140));
                        EditorGUILayout.LabelField(FormatSize(entry.Bytes), GUILayout.Width(70));
                        GUILayout.FlexibleSpace();
                        using (new EditorGUI.DisabledScope(entry.PrefabPath == null))
                        {
                            if (GUILayout.Button("Restore", GUILayout.Width(65)))
                            {
                                var restored = Restore(entry, avatar, restoreMode, detachAssets);
                                Selection.activeGameObject = restored;
                                EditorGUIUtility.PingObject(restored);
                                GUIUtility.ExitGUI();
                            }
                        }
                        if (GUILayout.Button("Ping", GUILayout.Width(45)))
                            EditorGUIUtility.PingObject(AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(entry.PrefabPath ?? entry.Folder));
                        if (GUILayout.Button("Delete", GUILayout.Width(55)) &&
                            EditorUtility.DisplayDialog("Delete Backup", $"Delete the backup folder {entry.Folder}? This cannot be undone.", "Delete", "Cancel"))
                        {
                            Delete(entry);
                            Reload();
                            GUIUtility.ExitGUI();
                        }
                    }
                    if (entry.PrefabPath == null) EditorGUILayout.LabelField("Prefab missing from this backup folder.", EditorStyles.miniLabel);
                    if (!string.IsNullOrEmpty(entry.Note)) EditorGUILayout.LabelField(entry.Note, EditorStyles.wordWrappedMiniLabel);
                }
            }
            EditorGUILayout.EndScrollView();
        }
    }
}
