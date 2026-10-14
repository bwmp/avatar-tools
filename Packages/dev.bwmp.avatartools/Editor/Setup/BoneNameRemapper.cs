using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;
using VRC.SDK3.Avatars.Components;

namespace AvatarTools.Editor
{
    public class BoneNameRemapper : EditorWindow
    {
        public enum RenameMode
        {
            BlenderToUnderscore,
            UnderscoreToBlender,
            LeftRightWordToBlender,
            BlenderToLeftRightWord,
            StripPrefix,
            AddPrefix,
            FindReplace
        }

        static readonly string[] ModeLabels =
        {
            "X.L / X.R  ->  X_L / X_R",
            "X_L / X_R  ->  X.L / X.R",
            "Left X / Right X  ->  X.L / X.R",
            "X.L / X.R  ->  Left X / Right X",
            "Strip Prefix",
            "Add Prefix",
            "Find / Replace"
        };

        public struct Rename
        {
            public Transform Target;
            public string OldName;
            public string NewName;
            public bool Collides;
        }

        Transform root;
        RenameMode mode;
        string text = "mixamorig:";
        string find = "", replace = "";
        bool useRegex;
        bool updateAnimations = true;
        Vector2 scroll;

        [MenuItem(AvatarToolsUtil.MenuRoot + "Setup/Bone Name Remapper")]
        static void Open()
        {
            GetWindow<BoneNameRemapper>("Bone Remapper");
        }

        void OnEnable()
        {
            if (Selection.activeTransform != null && !EditorUtility.IsPersistent(Selection.activeTransform)) root = Selection.activeTransform;
        }

        void OnGUI()
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                root = (Transform)EditorGUILayout.ObjectField("Root (Armature)", root, typeof(Transform), true);
                if (GUILayout.Button("Use Selected", GUILayout.Width(100)) && Selection.activeTransform != null) root = Selection.activeTransform;
            }
            if (root != null && EditorUtility.IsPersistent(root)) root = null;
            mode = (RenameMode)EditorGUILayout.Popup("Rename", (int)mode, ModeLabels);
            if (mode == RenameMode.StripPrefix || mode == RenameMode.AddPrefix)
                text = EditorGUILayout.TextField("Prefix", text);
            if (mode == RenameMode.FindReplace)
            {
                find = EditorGUILayout.TextField("Find", find);
                replace = EditorGUILayout.TextField("Replace", replace);
                useRegex = EditorGUILayout.Toggle("Regex", useRegex);
            }
            if (root == null)
            {
                EditorGUILayout.HelpBox("Pick the root of the bone hierarchy in the scene.", MessageType.Info);
                return;
            }

            var avatar = AvatarToolsUtil.FindDescriptor(root.gameObject);
            using (new EditorGUI.DisabledScope(avatar == null))
                updateAnimations = EditorGUILayout.ToggleLeft("Update animation paths in the avatar's playable layer clips", updateAnimations && avatar != null);
            var humanAnimator = root.GetComponentInParent<Animator>(true);
            if (humanAnimator != null && humanAnimator.isHuman)
                EditorGUILayout.HelpBox("This hierarchy belongs to a humanoid Animator. Its Avatar maps bones by name, so renaming humanoid bones requires reconfiguring the rig afterwards.", MessageType.Warning);

            List<Rename> renames;
            try
            {
                renames = Preview(root, n => RenameOne(n, mode, text, find, replace, useRegex));
            }
            catch (ArgumentException e)
            {
                EditorGUILayout.HelpBox("Invalid regex: " + e.Message, MessageType.Error);
                return;
            }

            var collisions = renames.Count(r => r.Collides);
            EditorGUILayout.LabelField($"{renames.Count} renames" + (collisions > 0 ? $", {collisions} with sibling name collisions" : ""), EditorStyles.miniLabel);
            scroll = EditorGUILayout.BeginScrollView(scroll);
            var collisionColor = new Color(1f, 0.5f, 0.45f);
            foreach (var rename in renames)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    var previous = GUI.color;
                    if (rename.Collides) GUI.color = collisionColor;
                    EditorGUILayout.LabelField(rename.OldName, GUILayout.MinWidth(100));
                    EditorGUILayout.LabelField("->", GUILayout.Width(20));
                    EditorGUILayout.LabelField(rename.NewName, GUILayout.MinWidth(100));
                    GUI.color = previous;
                    if (GUILayout.Button("Ping", EditorStyles.miniButton, GUILayout.Width(40))) EditorGUIUtility.PingObject(rename.Target);
                }
            }
            EditorGUILayout.EndScrollView();

            using (new EditorGUI.DisabledScope(renames.Count == 0))
            {
                if (!GUILayout.Button($"Rename {renames.Count} Transforms", GUILayout.Height(30))) return;
                if (collisions > 0 && !EditorUtility.DisplayDialog("Name collisions",
                        $"{collisions} renamed transforms will share a name with a sibling, which makes animation paths ambiguous. Rename anyway?", "Rename", "Cancel"))
                    return;
                var result = Apply(renames, updateAnimations ? avatar : null);
                if (result.skippedClips > 0)
                    EditorUtility.DisplayDialog("Bone Name Remapper", $"Updated {result.updatedClips} clips. Skipped {result.skippedClips} read-only clips (inside models or packages).", "OK");
            }
        }

        public static string RenameOne(string name, RenameMode mode, string prefix, string find, string replace, bool regex)
        {
            switch (mode)
            {
                case RenameMode.BlenderToUnderscore:
                    return Regex.Replace(name, @"\.([LlRr])(?=$|[._])", "_$1");
                case RenameMode.UnderscoreToBlender:
                    return Regex.Replace(name, @"_([LlRr])(?=$|[._])", ".$1");
                case RenameMode.LeftRightWordToBlender:
                {
                    var match = Regex.Match(name, @"^(Left|Right)[\s_.-]*(.+)$", RegexOptions.IgnoreCase);
                    return match.Success ? match.Groups[2].Value + (char.ToLowerInvariant(match.Groups[1].Value[0]) == 'l' ? ".L" : ".R") : name;
                }
                case RenameMode.BlenderToLeftRightWord:
                {
                    var match = Regex.Match(name, @"^(.+?)[._]([LlRr])$");
                    return match.Success ? (char.ToLowerInvariant(match.Groups[2].Value[0]) == 'l' ? "Left " : "Right ") + match.Groups[1].Value : name;
                }
                case RenameMode.StripPrefix:
                    return !string.IsNullOrEmpty(prefix) && name.StartsWith(prefix, StringComparison.Ordinal) && name.Length > prefix.Length
                        ? name.Substring(prefix.Length) : name;
                case RenameMode.AddPrefix:
                    return string.IsNullOrEmpty(prefix) || name.StartsWith(prefix, StringComparison.Ordinal) ? name : prefix + name;
                case RenameMode.FindReplace:
                    if (string.IsNullOrEmpty(find)) return name;
                    return regex ? Regex.Replace(name, find, replace ?? "") : name.Replace(find, replace ?? "");
                default:
                    throw new ArgumentOutOfRangeException(nameof(mode));
            }
        }

        public static List<Rename> Preview(Transform root, Func<string, string> rename)
        {
            var targets = root.GetComponentsInChildren<Transform>(true)
                .Where(t => t.GetComponent<VRCAvatarDescriptor>() == null).ToList();
            var newNames = targets.ToDictionary(t => t, t =>
            {
                var renamed = rename(t.name);
                return string.IsNullOrEmpty(renamed) ? t.name : renamed;
            });
            string FinalName(Transform t) => newNames.TryGetValue(t, out var n) ? n : t.name;

            var result = new List<Rename>();
            foreach (var t in targets)
            {
                var newName = newNames[t];
                if (newName == t.name) continue;
                var siblings = t.parent != null ? t.parent.Cast<Transform>() : t.gameObject.scene.GetRootGameObjects().Select(g => g.transform);
                result.Add(new Rename
                {
                    Target = t, OldName = t.name, NewName = newName,
                    Collides = siblings.Any(s => s != t && FinalName(s) == newName)
                });
            }
            return result;
        }

        public static (int updatedClips, int skippedClips) Apply(List<Rename> renames, VRCAvatarDescriptor avatar)
        {
            Undo.IncrementCurrentGroup();
            var group = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("Rename Bones");

            Dictionary<Transform, string> oldPaths = null;
            if (avatar != null)
                oldPaths = avatar.GetComponentsInChildren<Transform>(true).ToDictionary(t => t, t => AvatarToolsUtil.GetPath(avatar.transform, t));

            foreach (var rename in renames)
            {
                Undo.RecordObject(rename.Target.gameObject, "Rename Bones");
                rename.Target.name = rename.NewName;
            }

            var result = (updatedClips: 0, skippedClips: 0);
            if (oldPaths != null)
            {
                var pathMap = new Dictionary<string, string>();
                foreach (var pair in oldPaths)
                {
                    var newPath = AvatarToolsUtil.GetPath(avatar.transform, pair.Key);
                    if (newPath != pair.Value) pathMap[pair.Value] = newPath;
                }
                if (pathMap.Count > 0)
                {
                    foreach (var clip in AvatarToolsUtil.AllControllers(avatar).SelectMany(AvatarToolsUtil.AllClips).Distinct())
                    {
                        if (!RebindNeeded(clip, pathMap)) continue;
                        var path = AssetDatabase.GetAssetPath(clip);
                        if (!path.EndsWith(".anim", StringComparison.OrdinalIgnoreCase) && !path.EndsWith(".controller", StringComparison.OrdinalIgnoreCase)
                            || path.StartsWith("Packages/", StringComparison.Ordinal))
                        {
                            result.skippedClips++;
                            continue;
                        }
                        RebindClip(clip, pathMap);
                        result.updatedClips++;
                    }
                    AssetDatabase.SaveAssets();
                }
            }
            Undo.CollapseUndoOperations(group);
            return result;
        }

        static bool RebindNeeded(AnimationClip clip, Dictionary<string, string> pathMap)
        {
            return AnimationUtility.GetCurveBindings(clip).Concat(AnimationUtility.GetObjectReferenceCurveBindings(clip))
                .Any(b => RemapPath(b.path, pathMap) != null);
        }

        public static string RemapPath(string path, Dictionary<string, string> pathMap)
        {
            if (pathMap.TryGetValue(path, out var exact)) return exact;
            for (var index = path.LastIndexOf('/'); index > 0; index = path.LastIndexOf('/', index - 1))
                if (pathMap.TryGetValue(path.Substring(0, index), out var prefix))
                    return prefix + path.Substring(index);
            return null;
        }

        public static void RebindClip(AnimationClip clip, Dictionary<string, string> pathMap)
        {
            Undo.RecordObject(clip, "Rename Bones");
            EditorCurveBinding Moved(EditorCurveBinding binding)
            {
                binding.path = RemapPath(binding.path, pathMap);
                return binding;
            }
            var curves = AnimationUtility.GetCurveBindings(clip).Where(b => RemapPath(b.path, pathMap) != null)
                .Select(b => (binding: b, curve: AnimationUtility.GetEditorCurve(clip, b))).ToList();
            var references = AnimationUtility.GetObjectReferenceCurveBindings(clip).Where(b => RemapPath(b.path, pathMap) != null)
                .Select(b => (binding: b, keys: AnimationUtility.GetObjectReferenceCurve(clip, b))).ToList();
            foreach (var entry in curves) AnimationUtility.SetEditorCurve(clip, entry.binding, null);
            foreach (var entry in references) AnimationUtility.SetObjectReferenceCurve(clip, entry.binding, null);
            foreach (var entry in curves) AnimationUtility.SetEditorCurve(clip, Moved(entry.binding), entry.curve);
            foreach (var entry in references) AnimationUtility.SetObjectReferenceCurve(clip, Moved(entry.binding), entry.keys);
            EditorUtility.SetDirty(clip);
        }
    }
}
