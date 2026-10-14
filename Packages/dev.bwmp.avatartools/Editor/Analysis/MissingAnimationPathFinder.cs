using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using VRC.SDK3.Avatars.Components;

namespace AvatarTools.Editor
{
    public class MissingAnimationPathFinder : EditorWindow
    {
        public class MissingBinding
        {
            public AnimationClip Clip;
            public EditorCurveBinding Binding;
            public bool ObjectReference;
            public string Reason;
        }

        public class MissingGroup
        {
            public string Path;
            public List<MissingBinding> Bindings = new List<MissingBinding>();
            public Transform Target;
            public string NewPath = "";
            public bool Expanded;
            public IEnumerable<AnimationClip> Clips => Bindings.Select(b => b.Clip).Distinct();
        }

        const string BlendShapePrefix = "blendShape.";

        VRCAvatarDescriptor avatar;
        [SerializeField] List<AnimationClip> extraClips = new List<AnimationClip>();
        List<MissingGroup> groups;
        List<AnimationClip> readOnlyClips = new List<AnimationClip>();
        int scannedClips;
        Vector2 scroll;

        [MenuItem(AvatarToolsUtil.MenuRoot + "Animator/Missing Animation Path Finder")]
        static void Open() => GetWindow<MissingAnimationPathFinder>("Missing Paths");

        void OnEnable()
        {
            avatar = avatar ? avatar : AvatarToolsUtil.SelectedAvatar();
            Undo.undoRedoPerformed += Scan;
        }

        void OnDisable() => Undo.undoRedoPerformed -= Scan;

        public static List<AnimationClip> CollectClips(VRCAvatarDescriptor avatar, IEnumerable<AnimationClip> extra)
        {
            return AvatarToolsUtil.AllControllers(avatar).SelectMany(AvatarToolsUtil.AllClips)
                .Concat(extra ?? Enumerable.Empty<AnimationClip>()).Where(c => c != null).Distinct().ToList();
        }

        public static string CheckBinding(GameObject root, EditorCurveBinding binding)
        {
            var transform = binding.path == "" ? root.transform : root.transform.Find(binding.path);
            if (transform == null) return "Path not found";
            if (binding.type == null) return "Unknown component type";
            if (binding.type != typeof(GameObject) && transform.GetComponent(binding.type) == null)
                return $"No {binding.type.Name} on object";
            if (binding.propertyName.StartsWith(BlendShapePrefix) && typeof(SkinnedMeshRenderer).IsAssignableFrom(binding.type))
            {
                var mesh = transform.GetComponent<SkinnedMeshRenderer>().sharedMesh;
                var shape = binding.propertyName.Substring(BlendShapePrefix.Length);
                if (mesh == null) return "Renderer has no mesh";
                if (mesh.GetBlendShapeIndex(shape) < 0) return $"Blendshape \"{shape}\" not on mesh";
            }
            return null;
        }

        public static List<MissingGroup> FindMissing(GameObject root, IEnumerable<AnimationClip> clips)
        {
            var groups = new Dictionary<string, MissingGroup>();
            foreach (var clip in clips)
            {
                var bindings = AnimationUtility.GetCurveBindings(clip).Select(b => (b, false))
                    .Concat(AnimationUtility.GetObjectReferenceCurveBindings(clip).Select(b => (b, true)));
                foreach (var (binding, objectReference) in bindings)
                {
                    var reason = CheckBinding(root, binding);
                    if (reason == null) continue;
                    if (!groups.TryGetValue(binding.path, out var group))
                        groups[binding.path] = group = new MissingGroup { Path = binding.path };
                    group.Bindings.Add(new MissingBinding { Clip = clip, Binding = binding, ObjectReference = objectReference, Reason = reason });
                }
            }
            return groups.Values.OrderBy(g => g.Path).ToList();
        }

        public static bool IsReadOnly(AnimationClip clip)
        {
            var path = AssetDatabase.GetAssetPath(clip);
            return string.IsNullOrEmpty(path) || path.StartsWith("Packages/") || AssetImporter.GetAtPath(path) is ModelImporter;
        }

        public static int Remap(IEnumerable<MissingBinding> bindings, string newPath)
        {
            var count = 0;
            foreach (var clipGroup in bindings.Where(b => !IsReadOnly(b.Clip)).GroupBy(b => b.Clip))
            {
                var clip = clipGroup.Key;
                Undo.RecordObject(clip, "Remap Animation Path");
                foreach (var missing in clipGroup)
                {
                    var target = missing.Binding;
                    target.path = newPath;
                    if (missing.ObjectReference)
                    {
                        var keys = AnimationUtility.GetObjectReferenceCurve(clip, missing.Binding);
                        AnimationUtility.SetObjectReferenceCurve(clip, missing.Binding, null);
                        AnimationUtility.SetObjectReferenceCurve(clip, target, keys);
                    }
                    else
                    {
                        var curve = AnimationUtility.GetEditorCurve(clip, missing.Binding);
                        AnimationUtility.SetEditorCurve(clip, missing.Binding, null);
                        AnimationUtility.SetEditorCurve(clip, target, curve);
                    }
                    count++;
                }
                EditorUtility.SetDirty(clip);
            }
            AssetDatabase.SaveAssets();
            return count;
        }

        public static int Remove(IEnumerable<MissingBinding> bindings)
        {
            var count = 0;
            foreach (var clipGroup in bindings.Where(b => !IsReadOnly(b.Clip)).GroupBy(b => b.Clip))
            {
                var clip = clipGroup.Key;
                Undo.RecordObject(clip, "Remove Missing Bindings");
                foreach (var missing in clipGroup)
                {
                    if (missing.ObjectReference) AnimationUtility.SetObjectReferenceCurve(clip, missing.Binding, null);
                    else AnimationUtility.SetEditorCurve(clip, missing.Binding, null);
                    count++;
                }
                EditorUtility.SetDirty(clip);
            }
            AssetDatabase.SaveAssets();
            return count;
        }

        public static Transform Suggest(Transform root, string missingPath, System.Type componentType)
        {
            var leaf = missingPath.Substring(missingPath.LastIndexOf('/') + 1);
            var candidates = root.GetComponentsInChildren<Transform>(true).Where(t => t != root && t.name == leaf).ToList();
            return candidates.FirstOrDefault(t => componentType == null || componentType == typeof(GameObject) || t.GetComponent(componentType) != null)
                   ?? candidates.FirstOrDefault();
        }

        void Scan()
        {
            if (avatar == null)
            {
                groups = null;
                return;
            }
            var clips = CollectClips(avatar, extraClips);
            scannedClips = clips.Count;
            groups = FindMissing(avatar.gameObject, clips);
            readOnlyClips = groups.SelectMany(g => g.Clips).Distinct().Where(IsReadOnly).ToList();
            Repaint();
        }

        void OnGUI()
        {
            EditorGUI.BeginChangeCheck();
            avatar = AvatarToolsUtil.AvatarField(avatar);
            if (EditorGUI.EndChangeCheck()) groups = null;
            if (avatar == null)
            {
                EditorGUILayout.HelpBox("Select an avatar with a VRC Avatar Descriptor.", MessageType.Info);
                return;
            }

            ExtraClipsGUI();
            if (GUILayout.Button("Scan", GUILayout.Height(24))) Scan();
            if (groups == null) return;

            if (groups.Count == 0)
            {
                EditorGUILayout.HelpBox($"All bindings in {scannedClips} clip(s) resolve on {avatar.name}.", MessageType.Info);
                return;
            }
            var total = groups.Sum(g => g.Bindings.Count);
            EditorGUILayout.HelpBox($"{total} missing binding(s) across {groups.Count} path(s) in {scannedClips} scanned clip(s).", MessageType.Warning);
            if (readOnlyClips.Count > 0)
                EditorGUILayout.HelpBox("Read-only clips (in packages or model files) will be skipped: " + string.Join(", ", readOnlyClips.Select(c => c.name)), MessageType.Info);

            scroll = EditorGUILayout.BeginScrollView(scroll);
            foreach (var group in groups.ToList())
            {
                EditorGUILayout.Space(2);
                var reasons = string.Join(", ", group.Bindings.Select(b => b.Reason).Distinct());
                group.Expanded = EditorGUILayout.Foldout(group.Expanded,
                    $"{(group.Path == "" ? "(root)" : group.Path)} - {group.Bindings.Count} binding(s) in {group.Clips.Count()} clip(s): {reasons}", true);
                using (new EditorGUI.IndentLevelScope())
                {
                    if (group.Expanded)
                        foreach (var b in group.Bindings)
                            EditorGUILayout.LabelField($"{b.Clip.name}: {b.Binding.type?.Name ?? "?"}.{b.Binding.propertyName}", b.Reason, EditorStyles.miniLabel);

                    using (new EditorGUILayout.HorizontalScope())
                    {
                        EditorGUI.BeginChangeCheck();
                        group.Target = (Transform)EditorGUILayout.ObjectField(group.Target, typeof(Transform), true, GUILayout.Width(180));
                        if (EditorGUI.EndChangeCheck() && group.Target != null)
                        {
                            if (group.Target.IsChildOf(avatar.transform))
                                group.NewPath = AvatarToolsUtil.GetPath(avatar.transform, group.Target);
                            else
                            {
                                group.Target = null;
                                ShowNotification(new GUIContent("Pick an object inside the avatar"));
                            }
                        }
                        group.NewPath = EditorGUILayout.TextField(group.NewPath);
                        if (GUILayout.Button("Suggest", GUILayout.Width(65)))
                        {
                            var found = Suggest(avatar.transform, group.Path, group.Bindings[0].Binding.type);
                            if (found != null)
                            {
                                group.Target = found;
                                group.NewPath = AvatarToolsUtil.GetPath(avatar.transform, found);
                            }
                            else ShowNotification(new GUIContent("No transform with the same name"));
                        }
                        using (new EditorGUI.DisabledScope(group.NewPath == group.Path || group.NewPath == "" && group.Target != avatar.transform))
                        {
                            if (GUILayout.Button("Remap", GUILayout.Width(60)))
                            {
                                Remap(group.Bindings, group.NewPath);
                                Scan();
                                GUIUtility.ExitGUI();
                            }
                        }
                        if (GUILayout.Button("Remove", GUILayout.Width(60)) &&
                            EditorUtility.DisplayDialog("Remove Bindings", $"Remove {group.Bindings.Count} binding(s) for \"{group.Path}\" from {group.Clips.Count()} clip(s)?", "Remove", "Cancel"))
                        {
                            Remove(group.Bindings);
                            Scan();
                            GUIUtility.ExitGUI();
                        }
                    }
                }
            }
            EditorGUILayout.EndScrollView();

            if (GUILayout.Button($"Remove All Missing Bindings ({total})") &&
                EditorUtility.DisplayDialog("Remove Bindings", $"Remove {total} missing binding(s) from {groups.SelectMany(g => g.Clips).Distinct().Count()} clip(s)?", "Remove", "Cancel"))
            {
                Remove(groups.SelectMany(g => g.Bindings).ToList());
                Scan();
                GUIUtility.ExitGUI();
            }
        }

        void ExtraClipsGUI()
        {
            EditorGUILayout.LabelField("Extra clips", EditorStyles.boldLabel);
            for (var i = 0; i < extraClips.Count; i++)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    extraClips[i] = (AnimationClip)EditorGUILayout.ObjectField(extraClips[i], typeof(AnimationClip), false);
                    if (GUILayout.Button("Remove", GUILayout.Width(60)))
                    {
                        extraClips.RemoveAt(i);
                        GUIUtility.ExitGUI();
                    }
                }
            }
            var drop = GUILayoutUtility.GetRect(0, 32, GUILayout.ExpandWidth(true));
            GUI.Box(drop, "Drop animation clips here", EditorStyles.helpBox);
            var evt = Event.current;
            if (!drop.Contains(evt.mousePosition) || evt.type != EventType.DragUpdated && evt.type != EventType.DragPerform) return;
            var dropped = DragAndDrop.objectReferences.OfType<AnimationClip>().ToList();
            DragAndDrop.visualMode = dropped.Count > 0 ? DragAndDropVisualMode.Copy : DragAndDropVisualMode.Rejected;
            if (evt.type == EventType.DragPerform && dropped.Count > 0)
            {
                DragAndDrop.AcceptDrag();
                extraClips.AddRange(dropped.Where(c => !extraClips.Contains(c)));
            }
            evt.Use();
        }
    }
}
