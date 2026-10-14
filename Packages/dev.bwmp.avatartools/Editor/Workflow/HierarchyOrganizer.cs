using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using VRC.Dynamics;
using VRC.SDK3.Avatars.Components;

namespace AvatarTools.Editor
{
    public class HierarchyOrganizer : EditorWindow
    {
        const string UndoName = "Organize Avatar Hierarchy";

        [Serializable]
        public class CustomGroup
        {
            public string Name = "Accessories";
            public List<GameObject> Objects = new List<GameObject>();
        }

        [Serializable]
        public class Settings
        {
            public List<string> Pinned = new List<string> { "Armature", "Body" };
            public bool Sort = true;
            public bool Group = true;
            public bool GroupMeshes = true;
            public string MeshesName = "Meshes";
            public bool GroupDynamics = true;
            public string DynamicsName = "Dynamics";
            public List<CustomGroup> CustomGroups = new List<CustomGroup>();
            public bool MovePhysBones;
            public string PhysBonesName = "PhysBones";

            public IEnumerable<string> ContainerNames()
            {
                if (Group && GroupMeshes) yield return MeshesName;
                if (Group && GroupDynamics) yield return DynamicsName;
                if (Group) foreach (var custom in CustomGroups) yield return custom.Name;
                if (MovePhysBones) yield return PhysBonesName;
            }
        }

        public class GroupMove
        {
            public Transform Target;
            public string Container;
            public string OldPath;
            public string NewPath;
            public bool Collides;
        }

        public class PhysBoneMove
        {
            public VRCPhysBoneBase Bone;
            public string ObjectName;
            public string OldPath;
            public string NewPath;
        }

        public class Plan
        {
            public List<GroupMove> Groups = new List<GroupMove>();
            public List<PhysBoneMove> PhysBones = new List<PhysBoneMove>();
            public List<string> CurrentOrder = new List<string>();
            public List<string> NewOrder;
            public bool OrderChanges => NewOrder != null && !NewOrder.SequenceEqual(CurrentOrder);
            public bool Empty => Groups.All(g => g.Collides) && PhysBones.Count == 0 && !OrderChanges;
        }

        public class Result
        {
            public int Moved;
            public int PhysBones;
            public int UpdatedClips;
            public List<AnimationClip> SkippedClips = new List<AnimationClip>();
        }

        VRCAvatarDescriptor avatar;
        [SerializeField] Settings settings = new Settings();
        Vector2 scroll;

        [MenuItem(AvatarToolsUtil.MenuRoot + "Workflow/Hierarchy Organizer")]
        static void Open()
        {
            GetWindow<HierarchyOrganizer>("Hierarchy Organizer");
        }

        void OnEnable()
        {
            if (avatar == null) avatar = AvatarToolsUtil.SelectedAvatar();
        }

        void OnHierarchyChange()
        {
            Repaint();
        }

        static bool IsDynamicsOnly(Transform t)
        {
            var components = t.GetComponentsInChildren<Component>(true).Where(c => !(c is Transform)).ToList();
            return components.Count > 0 && components.All(c => c is VRCPhysBoneBase || c is ContactBase || c is VRCPhysBoneColliderBase);
        }

        static HashSet<Transform> HumanoidRoots(VRCAvatarDescriptor avatar)
        {
            var result = new HashSet<Transform>();
            var animator = avatar.GetComponent<Animator>();
            if (animator == null || !animator.isHuman) return result;
            for (var bone = HumanBodyBones.Hips; bone < HumanBodyBones.LastBone; bone++)
            {
                var t = animator.GetBoneTransform(bone);
                while (t != null && t.parent != null && t.parent != avatar.transform) t = t.parent;
                if (t != null && t.parent == avatar.transform) result.Add(t);
            }
            return result;
        }

        static string UniqueName(string name, ICollection<string> taken)
        {
            if (!taken.Contains(name)) return name;
            for (var i = 1;; i++)
                if (!taken.Contains($"{name} ({i})")) return $"{name} ({i})";
        }

        public static List<string> SortedOrder(IEnumerable<string> names, IList<string> pinned)
        {
            var list = names.ToList();
            var head = pinned.Where(list.Contains).Distinct().ToList();
            var rest = list.Where(n => !head.Contains(n)).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ThenBy(n => n, StringComparer.Ordinal);
            var result = new List<string>();
            foreach (var name in head) result.AddRange(list.Where(n => n == name));
            result.AddRange(rest);
            return result;
        }

        public static Plan BuildPlan(VRCAvatarDescriptor avatar, Settings settings)
        {
            var root = avatar.transform;
            var plan = new Plan();
            var children = root.Cast<Transform>().ToList();
            plan.CurrentOrder = children.Select(c => c.name).ToList();
            var containerNames = new HashSet<string>(settings.ContainerNames().Where(n => !string.IsNullOrWhiteSpace(n)));
            var protectedSet = HumanoidRoots(avatar);

            if (settings.Group)
            {
                var planned = new Dictionary<string, HashSet<string>>();
                foreach (var child in children)
                {
                    if (protectedSet.Contains(child) || settings.Pinned.Contains(child.name) || containerNames.Contains(child.name)) continue;
                    var container = settings.CustomGroups.FirstOrDefault(g => !string.IsNullOrWhiteSpace(g.Name) && g.Objects.Contains(child.gameObject))?.Name;
                    if (container == null && settings.GroupMeshes && (child.GetComponent<SkinnedMeshRenderer>() != null || child.GetComponent<MeshRenderer>() != null))
                        container = settings.MeshesName;
                    if (container == null && settings.GroupDynamics && IsDynamicsOnly(child))
                        container = settings.DynamicsName;
                    if (string.IsNullOrWhiteSpace(container)) continue;

                    if (!planned.TryGetValue(container, out var names))
                    {
                        var existing = root.Find(container);
                        planned[container] = names = new HashSet<string>(existing != null && existing.parent == root
                            ? existing.Cast<Transform>().Select(t => t.name) : Enumerable.Empty<string>());
                    }
                    plan.Groups.Add(new GroupMove
                    {
                        Target = child, Container = container, OldPath = child.name, NewPath = container + "/" + child.name,
                        Collides = !names.Add(child.name)
                    });
                }
            }

            if (settings.MovePhysBones && !string.IsNullOrWhiteSpace(settings.PhysBonesName))
            {
                var existing = children.FirstOrDefault(c => c.name == settings.PhysBonesName);
                var taken = new HashSet<string>(existing != null ? existing.Cast<Transform>().Select(t => t.name) : Enumerable.Empty<string>());
                foreach (var bone in avatar.GetComponentsInChildren<VRCPhysBoneBase>(true))
                {
                    if (existing != null && bone.transform.IsChildOf(existing)) continue;
                    if (bone.rootTransform != null && bone.rootTransform != bone.transform) continue;
                    if (bone.transform.childCount == 0) continue;
                    var name = UniqueName(bone.gameObject.name, taken);
                    taken.Add(name);
                    plan.PhysBones.Add(new PhysBoneMove
                    {
                        Bone = bone, ObjectName = name,
                        OldPath = AvatarToolsUtil.GetPath(root, bone.transform) + " [" + bone.GetType().Name + "]",
                        NewPath = settings.PhysBonesName + "/" + name
                    });
                }
            }

            if (settings.Sort)
            {
                var moved = new HashSet<Transform>(plan.Groups.Where(g => !g.Collides).Select(g => g.Target));
                var finalNames = children.Where(c => !moved.Contains(c)).Select(c => c.name).ToList();
                foreach (var container in plan.Groups.Where(g => !g.Collides).Select(g => g.Container)
                             .Concat(plan.PhysBones.Count > 0 ? new[] { settings.PhysBonesName } : Array.Empty<string>()).Distinct())
                    if (!finalNames.Contains(container)) finalNames.Add(container);
                plan.NewOrder = SortedOrder(finalNames, settings.Pinned);
            }
            return plan;
        }

        static Transform GetOrCreateContainer(Transform root, string name)
        {
            foreach (Transform child in root)
                if (child.name == name) return child;
            var go = new GameObject(name);
            go.transform.SetParent(root, false);
            Undo.RegisterCreatedObjectUndo(go, UndoName);
            return go.transform;
        }

        static bool RebindTyped(AnimationClip clip, Dictionary<(string path, Type type), string> map)
        {
            var curves = AnimationUtility.GetCurveBindings(clip).Where(b => map.ContainsKey((b.path, b.type)))
                .Select(b => (binding: b, curve: AnimationUtility.GetEditorCurve(clip, b))).ToList();
            var references = AnimationUtility.GetObjectReferenceCurveBindings(clip).Where(b => map.ContainsKey((b.path, b.type)))
                .Select(b => (binding: b, keys: AnimationUtility.GetObjectReferenceCurve(clip, b))).ToList();
            if (curves.Count == 0 && references.Count == 0) return false;
            Undo.RecordObject(clip, UndoName);
            EditorCurveBinding Moved(EditorCurveBinding binding)
            {
                binding.path = map[(binding.path, binding.type)];
                return binding;
            }
            foreach (var entry in curves) AnimationUtility.SetEditorCurve(clip, entry.binding, null);
            foreach (var entry in references) AnimationUtility.SetObjectReferenceCurve(clip, entry.binding, null);
            foreach (var entry in curves) AnimationUtility.SetEditorCurve(clip, Moved(entry.binding), entry.curve);
            foreach (var entry in references) AnimationUtility.SetObjectReferenceCurve(clip, Moved(entry.binding), entry.keys);
            EditorUtility.SetDirty(clip);
            return true;
        }

        static bool UsesPaths(AnimationClip clip, Dictionary<string, string> pathMap)
        {
            return AnimationUtility.GetCurveBindings(clip).Concat(AnimationUtility.GetObjectReferenceCurveBindings(clip))
                .Any(b => BoneNameRemapper.RemapPath(b.path, pathMap) != null);
        }

        static bool UsesTyped(AnimationClip clip, Dictionary<(string path, Type type), string> map)
        {
            return AnimationUtility.GetCurveBindings(clip).Concat(AnimationUtility.GetObjectReferenceCurveBindings(clip))
                .Any(b => map.ContainsKey((b.path, b.type)));
        }

        public static Result Apply(VRCAvatarDescriptor avatar, Plan plan, Settings settings)
        {
            var root = avatar.transform;
            var result = new Result();
            var updated = new HashSet<AnimationClip>();
            var skipped = new HashSet<AnimationClip>();
            var clips = MissingAnimationPathFinder.CollectClips(avatar, null);

            Undo.IncrementCurrentGroup();
            var group = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName(UndoName);

            var pathMap = new Dictionary<string, string>();
            foreach (var move in plan.Groups.Where(m => !m.Collides && m.Target != null && m.Target.parent == root))
            {
                var container = GetOrCreateContainer(root, move.Container);
                var oldPath = AvatarToolsUtil.GetPath(root, move.Target);
                Undo.SetTransformParent(move.Target, container, UndoName);
                pathMap[oldPath] = AvatarToolsUtil.GetPath(root, move.Target);
                result.Moved++;
            }
            if (pathMap.Count > 0)
            {
                foreach (var clip in clips.Where(c => UsesPaths(c, pathMap)))
                {
                    if (MissingAnimationPathFinder.IsReadOnly(clip))
                    {
                        skipped.Add(clip);
                        continue;
                    }
                    BoneNameRemapper.RebindClip(clip, pathMap);
                    updated.Add(clip);
                }
            }

            var moves = plan.PhysBones.Where(m => m.Bone != null).ToList();
            if (moves.Count > 0)
            {
                var container = GetOrCreateContainer(root, settings.PhysBonesName);
                var typedMap = new Dictionary<(string path, Type type), string>();
                foreach (var move in moves)
                {
                    var bone = move.Bone;
                    var boneTransform = bone.transform;
                    var go = new GameObject(move.ObjectName);
                    go.transform.SetParent(container, false);
                    Undo.RegisterCreatedObjectUndo(go, UndoName);
                    var copy = (VRCPhysBoneBase)Undo.AddComponent(go, bone.GetType());
                    EditorUtility.CopySerialized(bone, copy);
                    copy.rootTransform = boneTransform;
                    EditorUtility.SetDirty(copy);
                    var key = (AvatarToolsUtil.GetPath(root, boneTransform), bone.GetType());
                    if (!typedMap.ContainsKey(key)) typedMap[key] = AvatarToolsUtil.GetPath(root, go.transform);
                    Undo.DestroyObjectImmediate(bone);
                    result.PhysBones++;
                }
                foreach (var clip in clips.Where(c => UsesTyped(c, typedMap)))
                {
                    if (MissingAnimationPathFinder.IsReadOnly(clip))
                    {
                        skipped.Add(clip);
                        continue;
                    }
                    RebindTyped(clip, typedMap);
                    updated.Add(clip);
                }
            }

            if (settings.Sort)
            {
                var children = root.Cast<Transform>().ToList();
                var order = SortedOrder(children.Select(c => c.name), settings.Pinned);
                var remaining = new List<Transform>(children);
                for (var i = 0; i < order.Count; i++)
                {
                    var t = remaining.First(c => c.name == order[i]);
                    remaining.Remove(t);
                    if (t.GetSiblingIndex() != i) Undo.SetSiblingIndex(t, i, UndoName);
                }
            }

            Undo.SetCurrentGroupName(UndoName);
            Undo.CollapseUndoOperations(group);
            if (updated.Count > 0) AssetDatabase.SaveAssets();
            result.UpdatedClips = updated.Count;
            result.SkippedClips = skipped.ToList();
            return result;
        }

        void OnGUI()
        {
            var picked = AvatarToolsUtil.AvatarField(avatar);
            avatar = picked != null && EditorUtility.IsPersistent(picked) ? null : picked;
            if (avatar == null)
            {
                EditorGUILayout.HelpBox("Pick an avatar in the scene. Only its top-level children are reorganized.", MessageType.Info);
                return;
            }

            var pinned = EditorGUILayout.DelayedTextField(new GUIContent("Pinned Names", "Comma separated. Kept first in this order and never grouped."),
                string.Join(", ", settings.Pinned));
            settings.Pinned = pinned.Split(',').Select(s => s.Trim()).Where(s => s.Length > 0).ToList();

            settings.Sort = EditorGUILayout.ToggleLeft("Sort top-level children alphabetically", settings.Sort);
            settings.Group = EditorGUILayout.ToggleLeft("Group top-level children into containers", settings.Group);
            if (settings.Group)
            {
                using (new EditorGUI.IndentLevelScope())
                {
                    RuleGUI("Renderers", ref settings.GroupMeshes, ref settings.MeshesName);
                    RuleGUI("PhysBone / Contact only", ref settings.GroupDynamics, ref settings.DynamicsName);
                    CustomGroupsGUI();
                }
            }
            using (new EditorGUILayout.HorizontalScope())
            {
                settings.MovePhysBones = EditorGUILayout.ToggleLeft(new GUIContent("Move PhysBones off bones into",
                    "Only components on objects with children whose Root Transform is empty or the object itself. The new component's Root Transform points at the bone."),
                    settings.MovePhysBones, GUILayout.Width(200));
                using (new EditorGUI.DisabledScope(!settings.MovePhysBones))
                    settings.PhysBonesName = EditorGUILayout.TextField(settings.PhysBonesName);
            }

            var plan = BuildPlan(avatar, settings);
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Preview", EditorStyles.boldLabel);
            if (plan.Empty)
            {
                EditorGUILayout.HelpBox("Nothing to change.", MessageType.Info);
                return;
            }
            var collisions = plan.Groups.Count(g => g.Collides);
            if (collisions > 0)
                EditorGUILayout.HelpBox($"{collisions} object(s) share a name with an object already in their container and will be skipped.", MessageType.Warning);
            var multi = plan.PhysBones.GroupBy(m => m.Bone.transform).Count(g => g.Count() > 1);
            if (multi > 0)
                EditorGUILayout.HelpBox($"{multi} bone(s) carry several PhysBones. Animations of those components are retargeted to the first moved copy only.", MessageType.Warning);

            scroll = EditorGUILayout.BeginScrollView(scroll);
            var collisionColor = new Color(1f, 0.5f, 0.45f);
            foreach (var move in plan.Groups)
                MoveRow(move.OldPath, move.NewPath + (move.Collides ? "  (name taken, skipped)" : ""), move.Target, move.Collides ? collisionColor : (Color?)null);
            foreach (var move in plan.PhysBones)
                MoveRow(move.OldPath, move.NewPath, move.Bone, null);
            if (plan.OrderChanges)
            {
                EditorGUILayout.Space();
                EditorGUILayout.LabelField("New top-level order", EditorStyles.miniBoldLabel);
                EditorGUILayout.LabelField(string.Join(", ", plan.NewOrder), EditorStyles.wordWrappedMiniLabel);
            }
            EditorGUILayout.EndScrollView();

            var moveCount = plan.Groups.Count(g => !g.Collides);
            if (!GUILayout.Button("Apply", GUILayout.Height(30))) return;
            if (!EditorUtility.DisplayDialog("Organize Hierarchy",
                    $"Move {moveCount} object(s), move {plan.PhysBones.Count} PhysBone(s){(plan.OrderChanges ? ", reorder top-level children" : "")} on {avatar.name}? " +
                    "Animation paths in the avatar's controllers are updated.", "Apply", "Cancel"))
                return;
            var result = Apply(avatar, plan, settings);
            var message = $"Moved {result.Moved} object(s) and {result.PhysBones} PhysBone(s). Updated {result.UpdatedClips} clip(s).";
            if (result.SkippedClips.Count > 0)
                message += $"\n\nSkipped {result.SkippedClips.Count} read-only clip(s) (in models or packages), their bindings now miss: " +
                           string.Join(", ", result.SkippedClips.Select(c => c.name));
            EditorUtility.DisplayDialog("Organize Hierarchy", message, "OK");
            GUIUtility.ExitGUI();
        }

        static void MoveRow(string oldPath, string newPath, UnityEngine.Object target, Color? color)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                var previous = GUI.color;
                if (color.HasValue) GUI.color = color.Value;
                EditorGUILayout.LabelField(oldPath, GUILayout.MinWidth(120));
                EditorGUILayout.LabelField("->", GUILayout.Width(20));
                EditorGUILayout.LabelField(newPath, GUILayout.MinWidth(120));
                GUI.color = previous;
                if (GUILayout.Button("Ping", EditorStyles.miniButton, GUILayout.Width(40))) EditorGUIUtility.PingObject(target);
            }
        }

        static void RuleGUI(string label, ref bool enabled, ref string container)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                enabled = EditorGUILayout.ToggleLeft(label + " into", enabled, GUILayout.Width(200));
                using (new EditorGUI.DisabledScope(!enabled))
                    container = EditorGUILayout.TextField(container);
            }
        }

        void CustomGroupsGUI()
        {
            EditorGUILayout.LabelField("Custom groups (take priority over the rules)", EditorStyles.miniBoldLabel);
            for (var g = 0; g < settings.CustomGroups.Count; g++)
            {
                var custom = settings.CustomGroups[g];
                using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        custom.Name = EditorGUILayout.TextField("Container", custom.Name);
                        if (GUILayout.Button("Remove Group", GUILayout.Width(100)))
                        {
                            settings.CustomGroups.RemoveAt(g);
                            GUIUtility.ExitGUI();
                        }
                    }
                    custom.Objects.RemoveAll(o => o == null);
                    for (var i = 0; i < custom.Objects.Count; i++)
                    {
                        using (new EditorGUILayout.HorizontalScope())
                        {
                            EditorGUILayout.ObjectField(custom.Objects[i], typeof(GameObject), true);
                            if (custom.Objects[i].transform.parent != avatar.transform)
                                EditorGUILayout.LabelField("not top-level, ignored", EditorStyles.miniLabel, GUILayout.Width(120));
                            if (GUILayout.Button("X", EditorStyles.miniButton, GUILayout.Width(22)))
                            {
                                custom.Objects.RemoveAt(i);
                                GUIUtility.ExitGUI();
                            }
                        }
                    }
                    DropArea(custom);
                }
            }
            if (GUILayout.Button("Add Custom Group", GUILayout.Width(140))) settings.CustomGroups.Add(new CustomGroup());
        }

        static void DropArea(CustomGroup custom)
        {
            var drop = GUILayoutUtility.GetRect(0, 24, GUILayout.ExpandWidth(true));
            GUI.Box(drop, "Drop top-level objects here", EditorStyles.helpBox);
            var evt = Event.current;
            if (!drop.Contains(evt.mousePosition) || evt.type != EventType.DragUpdated && evt.type != EventType.DragPerform) return;
            var dropped = DragAndDrop.objectReferences.OfType<GameObject>().Where(o => !EditorUtility.IsPersistent(o)).ToList();
            DragAndDrop.visualMode = dropped.Count > 0 ? DragAndDropVisualMode.Copy : DragAndDropVisualMode.Rejected;
            if (evt.type == EventType.DragPerform && dropped.Count > 0)
            {
                DragAndDrop.AcceptDrag();
                custom.Objects.AddRange(dropped.Where(o => !custom.Objects.Contains(o)));
            }
            evt.Use();
        }
    }
}
