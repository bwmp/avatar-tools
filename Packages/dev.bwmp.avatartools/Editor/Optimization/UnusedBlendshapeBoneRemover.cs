using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using VRC.Dynamics;
using VRC.SDK3.Avatars.Components;
using VRC.SDKBase;

namespace AvatarTools.Editor
{
    public class UnusedBlendshapeBoneRemover : EditorWindow
    {
        [Flags]
        public enum ShapeUse
        {
            None = 0,
            Animated = 1,
            Viseme = 2,
            Eyelid = 4,
            NonZero = 8
        }

        public class ShapeInfo
        {
            public string Name;
            public ShapeUse Use;
            public float Weight;
            public bool Keep;
        }

        public class BoneScan
        {
            public readonly List<Transform> Roots = new List<Transform>();
            public int TotalCount;
        }

        static readonly string[] Tabs = { "Blendshapes", "Bones" };

        VRCAvatarDescriptor avatar;
        int tab;
        SkinnedMeshRenderer renderer;
        bool bakeNonZero;
        List<ShapeInfo> shapes;
        BoneScan boneScan;
        readonly HashSet<Transform> bonePicked = new HashSet<Transform>();
        Vector2 scroll;

        [MenuItem(AvatarToolsUtil.MenuRoot + "Optimization/Unused Blendshape & Bone Remover")]
        public static void Open()
        {
            var window = GetWindow<UnusedBlendshapeBoneRemover>("Blendshape & Bone Remover");
            window.avatar = AvatarToolsUtil.SelectedAvatar();
        }

        static IEnumerable<AnimationClip> AvatarClips(VRCAvatarDescriptor avatar)
        {
            var controllers = AvatarToolsUtil.AllControllers(avatar).ToList();
            var animator = avatar.GetComponent<Animator>();
            if (animator != null && animator.runtimeAnimatorController is AnimatorController own && !controllers.Contains(own))
                controllers.Add(own);
            return controllers.SelectMany(AvatarToolsUtil.AllClips).Distinct();
        }

        public static List<ShapeInfo> AnalyzeBlendshapes(VRCAvatarDescriptor avatar, SkinnedMeshRenderer smr, bool bakeNonZero)
        {
            var mesh = smr.sharedMesh;
            var path = AvatarToolsUtil.GetPath(avatar.transform, smr.transform);
            var animated = new HashSet<string>();
            foreach (var clip in AvatarClips(avatar))
            foreach (var binding in AnimationUtility.GetCurveBindings(clip))
                if (binding.path == path && binding.type == typeof(SkinnedMeshRenderer) && binding.propertyName.StartsWith("blendShape.", StringComparison.Ordinal))
                    animated.Add(binding.propertyName.Substring("blendShape.".Length));
            var visemes = new HashSet<string>();
            if (avatar.VisemeSkinnedMesh == smr)
            {
                if (avatar.lipSync == VRC_AvatarDescriptor.LipSyncStyle.VisemeBlendShape && avatar.VisemeBlendShapes != null)
                    visemes.UnionWith(avatar.VisemeBlendShapes.Where(n => !string.IsNullOrEmpty(n)));
                if (avatar.lipSync == VRC_AvatarDescriptor.LipSyncStyle.JawFlapBlendShape && !string.IsNullOrEmpty(avatar.MouthOpenBlendShapeName))
                    visemes.Add(avatar.MouthOpenBlendShapeName);
            }
            var eyelids = new HashSet<int>();
            var eyes = avatar.customEyeLookSettings;
            if (avatar.enableEyeLook && eyes.eyelidType == VRCAvatarDescriptor.EyelidType.Blendshapes && eyes.eyelidsSkinnedMesh == smr && eyes.eyelidsBlendshapes != null)
                eyelids.UnionWith(eyes.eyelidsBlendshapes);

            var result = new List<ShapeInfo>();
            for (var i = 0; i < mesh.blendShapeCount; i++)
            {
                var name = mesh.GetBlendShapeName(i);
                var info = new ShapeInfo { Name = name, Weight = smr.GetBlendShapeWeight(i) };
                if (animated.Contains(name)) info.Use |= ShapeUse.Animated;
                if (visemes.Contains(name)) info.Use |= ShapeUse.Viseme;
                if (eyelids.Contains(i)) info.Use |= ShapeUse.Eyelid;
                if (Mathf.Abs(info.Weight) > 0.0001f) info.Use |= ShapeUse.NonZero;
                var required = info.Use & ~ShapeUse.NonZero;
                info.Keep = required != ShapeUse.None || (info.Use.HasFlag(ShapeUse.NonZero) && !bakeNonZero);
                result.Add(info);
            }
            return result;
        }

        public static Mesh RemoveBlendshapes(VRCAvatarDescriptor avatar, SkinnedMeshRenderer smr, ICollection<string> keep, bool bakeRemovedWeights)
        {
            var source = smr.sharedMesh;
            var copy = Instantiate(source);
            copy.name = source.name + "_Trimmed";
            copy.ClearBlendShapes();
            var oldWeights = Enumerable.Range(0, source.blendShapeCount).ToDictionary(source.GetBlendShapeName, smr.GetBlendShapeWeight);

            if (bakeRemovedWeights)
            {
                var vertices = source.vertices;
                var normals = source.normals;
                var tangents = source.tangents;
                var baked = false;
                for (var s = 0; s < source.blendShapeCount; s++)
                {
                    var name = source.GetBlendShapeName(s);
                    var weight = oldWeights[name];
                    if (keep.Contains(name) || Mathf.Abs(weight) <= 0.0001f) continue;
                    baked = true;
                    var frames = MeshMerger.ReadFrames(source, s);
                    for (var v = 0; v < vertices.Length; v++)
                    {
                        MeshMerger.SampleFrames(frames, weight, v, out var dv, out var dn, out var dt);
                        vertices[v] += dv;
                        if (normals.Length == vertices.Length) normals[v] += dn;
                        if (tangents.Length == vertices.Length) tangents[v] += new Vector4(dt.x, dt.y, dt.z, 0);
                    }
                }
                if (baked)
                {
                    copy.vertices = vertices;
                    if (normals.Length == vertices.Length) copy.normals = normals.Select(n => n.normalized).ToArray();
                    if (tangents.Length == vertices.Length)
                        copy.tangents = tangents.Select(t =>
                        {
                            var v = new Vector3(t.x, t.y, t.z).normalized;
                            return new Vector4(v.x, v.y, v.z, t.w);
                        }).ToArray();
                }
            }

            for (var s = 0; s < source.blendShapeCount; s++)
            {
                var name = source.GetBlendShapeName(s);
                if (!keep.Contains(name)) continue;
                foreach (var frame in MeshMerger.ReadFrames(source, s))
                    copy.AddBlendShapeFrame(name, frame.Weight, frame.Vertices, frame.Normals, frame.Tangents);
            }
            copy.bounds = source.bounds;
            AvatarToolsUtil.CreateAsset(copy, AvatarToolsUtil.AvatarFolder(avatar, "Meshes"), copy.name, "asset");

            Undo.IncrementCurrentGroup();
            var group = Undo.GetCurrentGroup();
            Undo.RecordObject(smr, "Remove Blendshapes");
            smr.sharedMesh = copy;
            for (var i = 0; i < copy.blendShapeCount; i++)
                smr.SetBlendShapeWeight(i, oldWeights[copy.GetBlendShapeName(i)]);
            var eyes = avatar.customEyeLookSettings;
            if (eyes.eyelidsSkinnedMesh == smr && eyes.eyelidsBlendshapes != null)
            {
                Undo.RecordObject(avatar, "Remap Eyelid Blendshapes");
                eyes.eyelidsBlendshapes = eyes.eyelidsBlendshapes
                    .Select(i => i >= 0 && i < source.blendShapeCount ? copy.GetBlendShapeIndex(source.GetBlendShapeName(i)) : -1).ToArray();
                avatar.customEyeLookSettings = eyes;
                EditorUtility.SetDirty(avatar);
            }
            Undo.CollapseUndoOperations(group);
            AssetDatabase.SaveAssets();
            return copy;
        }

        public static BoneScan FindRemovableBones(VRCAvatarDescriptor avatar)
        {
            var root = avatar.transform;
            var renderers = avatar.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            var needed = new HashSet<Transform>();
            var armature = new HashSet<Transform>();
            foreach (var smr in renderers)
            {
                var bones = smr.bones;
                foreach (var bone in bones.Where(b => b != null))
                    armature.UnionWith(bone.GetComponentsInChildren<Transform>(true));
                if (smr.rootBone != null) needed.Add(smr.rootBone);
                if (smr.probeAnchor != null) needed.Add(smr.probeAnchor);
                if (smr.sharedMesh == null) continue;
                var perVertex = smr.sharedMesh.GetBonesPerVertex();
                if (perVertex.Length == 0) continue;
                foreach (var weight in smr.sharedMesh.GetAllBoneWeights())
                    if (weight.weight > 0 && weight.boneIndex < bones.Length && bones[weight.boneIndex] != null)
                        needed.Add(bones[weight.boneIndex]);
            }

            foreach (var component in avatar.GetComponentsInChildren<Component>(true))
            {
                if (component == null || component is Transform || component is SkinnedMeshRenderer) continue;
                if (component is VRCPhysBoneBase physBone)
                    needed.UnionWith(physBone.GetRootTransform().GetComponentsInChildren<Transform>(true));
                var serialized = new SerializedObject(component);
                var property = serialized.GetIterator();
                while (property.Next(true))
                {
                    if (property.propertyType != SerializedPropertyType.ObjectReference) continue;
                    if (property.objectReferenceValue is Transform t) needed.Add(t);
                    else if (property.objectReferenceValue is GameObject g) needed.Add(g.transform);
                }
            }

            var animator = avatar.GetComponent<Animator>();
            if (animator != null && animator.isHuman)
                foreach (HumanBodyBones bone in Enum.GetValues(typeof(HumanBodyBones)))
                    if (bone != HumanBodyBones.LastBone && animator.GetBoneTransform(bone) is Transform human)
                        needed.Add(human);

            var animatedPaths = new HashSet<string>();
            foreach (var clip in AvatarClips(avatar))
            {
                foreach (var binding in AnimationUtility.GetCurveBindings(clip)) animatedPaths.Add(binding.path);
                foreach (var binding in AnimationUtility.GetObjectReferenceCurveBindings(clip)) animatedPaths.Add(binding.path);
            }

            var removable = new Dictionary<Transform, bool>();
            bool IsRemovable(Transform t)
            {
                if (removable.TryGetValue(t, out var known)) return known;
                var candidate = t != root && armature.Contains(t) && !needed.Contains(t) &&
                                t.GetComponents<Component>().Length == 1 &&
                                !animatedPaths.Contains(AvatarToolsUtil.GetPath(root, t));
                var allChildren = true;
                foreach (Transform child in t)
                    allChildren &= IsRemovable(child);
                return removable[t] = candidate && allChildren;
            }
            foreach (var t in root.GetComponentsInChildren<Transform>(true)) IsRemovable(t);

            var scan = new BoneScan();
            foreach (var pair in removable.Where(p => p.Value))
            {
                scan.TotalCount++;
                if (pair.Key.parent == null || !removable.TryGetValue(pair.Key.parent, out var parentRemovable) || !parentRemovable)
                    scan.Roots.Add(pair.Key);
            }
            return scan;
        }

        public static int RemoveBones(VRCAvatarDescriptor avatar, IList<Transform> roots)
        {
            var doomed = new HashSet<Transform>(roots.Where(r => r != null).SelectMany(r => r.GetComponentsInChildren<Transform>(true)));
            Undo.IncrementCurrentGroup();
            var group = Undo.GetCurrentGroup();
            foreach (var smr in avatar.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                var bones = smr.bones;
                if (!bones.Any(doomed.Contains)) continue;
                Undo.RecordObject(smr, "Remove Unused Bones");
                for (var i = 0; i < bones.Length; i++)
                {
                    if (!doomed.Contains(bones[i])) continue;
                    var survivor = bones[i].parent;
                    while (survivor != null && doomed.Contains(survivor)) survivor = survivor.parent;
                    bones[i] = survivor;
                }
                smr.bones = bones;
            }
            var count = doomed.Count;
            foreach (var r in roots.Where(r => r != null)) Undo.DestroyObjectImmediate(r.gameObject);
            Undo.CollapseUndoOperations(group);
            return count;
        }

        void OnGUI()
        {
            EditorGUI.BeginChangeCheck();
            avatar = AvatarToolsUtil.AvatarField(avatar);
            if (EditorGUI.EndChangeCheck())
            {
                renderer = null;
                shapes = null;
                boneScan = null;
            }
            if (avatar == null)
            {
                EditorGUILayout.HelpBox("Select an avatar with a VRCAvatarDescriptor.", MessageType.Info);
                return;
            }
            tab = GUILayout.Toolbar(tab, Tabs);
            if (tab == 0) DrawBlendshapes();
            else DrawBones();
        }

        void DrawBlendshapes()
        {
            var renderers = avatar.GetComponentsInChildren<SkinnedMeshRenderer>(true).Where(r => r.sharedMesh != null && r.sharedMesh.blendShapeCount > 0).ToList();
            if (renderers.Count == 0)
            {
                EditorGUILayout.HelpBox("No skinned meshes with blendshapes under this avatar.", MessageType.Info);
                return;
            }
            var index = Mathf.Max(0, renderers.IndexOf(renderer));
            EditorGUI.BeginChangeCheck();
            index = EditorGUILayout.Popup("Skinned mesh", index, renderers.Select(r => AvatarToolsUtil.GetPath(avatar.transform, r.transform).Replace("/", " ∕ ")).ToArray());
            bakeNonZero = EditorGUILayout.ToggleLeft("Bake non-zero unused blendshapes into the base mesh", bakeNonZero);
            if (EditorGUI.EndChangeCheck() || renderer != renderers[index] || shapes == null)
            {
                renderer = renderers[index];
                shapes = AnalyzeBlendshapes(avatar, renderer, bakeNonZero);
            }
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Reset to Used", GUILayout.Width(110))) shapes = AnalyzeBlendshapes(avatar, renderer, bakeNonZero);
                if (GUILayout.Button("Keep All", GUILayout.Width(80))) shapes.ForEach(s => s.Keep = true);
            }
            scroll = EditorGUILayout.BeginScrollView(scroll);
            foreach (var shape in shapes)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    shape.Keep = EditorGUILayout.ToggleLeft(shape.Name, shape.Keep);
                    GUILayout.Label(shape.Use == ShapeUse.None ? "unused" : shape.Use.ToString(), EditorStyles.miniLabel, GUILayout.Width(200));
                    GUILayout.Label(shape.Weight.ToString("0.#"), EditorStyles.miniLabel, GUILayout.Width(40));
                }
            }
            EditorGUILayout.EndScrollView();

            var removed = shapes.Where(s => !s.Keep).ToList();
            var risky = removed.Where(s => (s.Use & ~ShapeUse.NonZero) != ShapeUse.None).Select(s => s.Name).ToList();
            var lostWeight = bakeNonZero ? new List<string>() : removed.Where(s => s.Use.HasFlag(ShapeUse.NonZero)).Select(s => s.Name).ToList();
            if (risky.Count > 0)
                EditorGUILayout.HelpBox("Removing blendshapes that are animated or used by the descriptor: " + string.Join(", ", risky), MessageType.Warning);
            if (lostWeight.Count > 0)
                EditorGUILayout.HelpBox("These have a non-zero weight and will change the mesh's look (enable baking to keep it): " + string.Join(", ", lostWeight), MessageType.Warning);
            using (new EditorGUI.DisabledScope(removed.Count == 0))
            {
                if (GUILayout.Button($"Create Mesh Without {removed.Count} Blendshape(s)", GUILayout.Height(28)) &&
                    EditorUtility.DisplayDialog("Remove Blendshapes",
                        $"Create a copy of '{renderer.sharedMesh.name}' without {removed.Count} blendshape(s) " +
                        $"({string.Join(", ", removed.Take(12).Select(s => s.Name))}{(removed.Count > 12 ? ", ..." : "")}) and assign it to {renderer.name}." +
                        (bakeNonZero ? "\n\nNon-zero weights of removed blendshapes are baked into the new mesh." : "") +
                        "\n\nThe original mesh asset is not modified.", "Create", "Cancel"))
                {
                    RemoveBlendshapes(avatar, renderer, new HashSet<string>(shapes.Where(s => s.Keep).Select(s => s.Name)), bakeNonZero);
                    shapes = AnalyzeBlendshapes(avatar, renderer, bakeNonZero);
                    GUIUtility.ExitGUI();
                }
            }
        }

        void DrawBones()
        {
            EditorGUILayout.HelpBox("A bone is removable when it has no weight in any skinned mesh, only a Transform component, is not humanoid-mapped, " +
                                    "not referenced by any component (PhysBones and their chains, constraints, contacts, descriptor), not animated, and all its children are removable.",
                MessageType.None);
            if (GUILayout.Button("Scan Bones"))
            {
                boneScan = FindRemovableBones(avatar);
                bonePicked.Clear();
                bonePicked.UnionWith(boneScan.Roots);
            }
            if (boneScan == null) return;
            boneScan.Roots.RemoveAll(t => t == null);
            if (boneScan.Roots.Count == 0)
            {
                EditorGUILayout.HelpBox("No removable bones found.", MessageType.Info);
                return;
            }
            scroll = EditorGUILayout.BeginScrollView(scroll);
            foreach (var bone in boneScan.Roots)
            {
                var descendants = bone.GetComponentsInChildren<Transform>(true).Length - 1;
                var on = bonePicked.Contains(bone);
                var label = AvatarToolsUtil.GetPath(avatar.transform, bone) + (descendants > 0 ? $"  (+{descendants} children)" : "");
                if (EditorGUILayout.ToggleLeft(label, on) != on)
                {
                    if (on) bonePicked.Remove(bone);
                    else bonePicked.Add(bone);
                }
            }
            EditorGUILayout.EndScrollView();
            var picked = boneScan.Roots.Where(bonePicked.Contains).ToList();
            var total = picked.Sum(b => b.GetComponentsInChildren<Transform>(true).Length);
            using (new EditorGUI.DisabledScope(picked.Count == 0))
            {
                if (GUILayout.Button($"Remove {total} Bone(s)", GUILayout.Height(28)) &&
                    EditorUtility.DisplayDialog("Remove Bones",
                        $"Delete these {total} transform(s) from the scene (Undo supported):\n\n" +
                        string.Join("\n", picked.Take(20).Select(b => AvatarToolsUtil.GetPath(avatar.transform, b))) + (picked.Count > 20 ? "\n..." : "") +
                        "\n\nSkinned mesh bone slots pointing at them are redirected to the nearest kept parent.", "Remove", "Cancel"))
                {
                    RemoveBones(avatar, picked);
                    boneScan = null;
                    GUIUtility.ExitGUI();
                }
            }
        }
    }
}
