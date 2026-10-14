using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using VRC.SDK3.Avatars.Components;

namespace AvatarTools.Editor
{
    public class AnimationClipRecorder : EditorWindow
    {
        const string MaterialPrefix = "m_Materials.Array.data[";
        const string BlendShapePrefix = "blendShape.";
        const double RefreshInterval = 1.0;
        const float Tolerance = 1e-4f;

        [Serializable]
        public class Entry
        {
            public UnityEngine.Object target;
            public string property;
            public float value;
            public UnityEngine.Object reference;
            public bool isReference;

            public string Key => target.GetInstanceID() + "|" + property;
        }

        public struct Change
        {
            public Entry before;
            public Entry after;
        }

        VRCAvatarDescriptor avatar;
        bool includeTransforms;
        bool createReverse = true;
        string clipName = "";
        [SerializeField] List<Entry> snapshot = new List<Entry>();
        List<Change> changes = new List<Change>();
        double lastRefresh;
        Vector2 scroll;

        bool Recording => snapshot.Count > 0;

        [MenuItem(AvatarToolsUtil.MenuRoot + "Animator/Animation Clip Recorder")]
        static void Open()
        {
            var window = GetWindow<AnimationClipRecorder>("Clip Recorder");
            window.avatar = AvatarToolsUtil.SelectedAvatar();
        }

        void OnInspectorUpdate()
        {
            if (!Recording || EditorApplication.timeSinceStartup - lastRefresh < RefreshInterval) return;
            RefreshChanges();
            Repaint();
        }

        void RefreshChanges()
        {
            lastRefresh = EditorApplication.timeSinceStartup;
            changes = avatar == null ? new List<Change>() : Diff(avatar, snapshot, includeTransforms);
        }

        void OnGUI()
        {
            scroll = EditorGUILayout.BeginScrollView(scroll);
            using (new EditorGUI.DisabledScope(Recording))
            {
                avatar = AvatarToolsUtil.AvatarField(avatar);
                includeTransforms = EditorGUILayout.Toggle("Include Transforms", includeTransforms);
            }
            var avatarError = AnimatorToolsShared.ValidateAvatar(avatar);
            AnimatorToolsShared.HelpIfAny(avatarError);

            if (!Recording)
            {
                EditorGUILayout.HelpBox("Start recording, then change objects, blendshapes, materials or renderers on the avatar in the scene. Only the changed values are saved into the clip.", MessageType.Info);
                using (new EditorGUI.DisabledScope(avatarError != null))
                    if (GUILayout.Button("Start Recording", GUILayout.Height(30)))
                    {
                        snapshot = Capture(avatar, includeTransforms);
                        changes.Clear();
                    }
                EditorGUILayout.EndScrollView();
                return;
            }

            if (avatar == null)
            {
                EditorGUILayout.HelpBox("The recorded avatar no longer exists.", MessageType.Error);
                if (GUILayout.Button("Discard Recording")) snapshot.Clear();
                EditorGUILayout.EndScrollView();
                return;
            }

            clipName = EditorGUILayout.TextField("Clip Name", clipName);
            createReverse = EditorGUILayout.Toggle("Also Create Reverse Clip", createReverse);
            EditorGUILayout.Space();
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField($"Detected Changes ({changes.Count})", EditorStyles.boldLabel);
                if (GUILayout.Button("Refresh Changes", GUILayout.Width(120))) RefreshChanges();
            }
            if (changes.Count == 0) EditorGUILayout.LabelField("No changes yet.", EditorStyles.miniLabel);
            foreach (var change in changes)
                EditorGUILayout.LabelField(Describe(avatar, change), EditorStyles.miniLabel);

            EditorGUILayout.Space();
            var error = string.IsNullOrWhiteSpace(clipName) ? "Enter a clip name."
                : changes.Count == 0 ? "Nothing has changed since recording started." : null;
            AnimatorToolsShared.HelpIfAny(error, MessageType.Warning);
            using (new EditorGUI.DisabledScope(error != null))
                if (GUILayout.Button("Stop & Save", GUILayout.Height(30)))
                {
                    RefreshChanges();
                    var clips = SaveClips(avatar, changes, clipName, createReverse);
                    EditorGUIUtility.PingObject(clips.on);
                    ShowNotification(new GUIContent("Saved " + clips.on.name));
                    snapshot.Clear();
                    changes.Clear();
                }
            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(changes.Count == 0))
                    if (GUILayout.Button("Revert to Snapshot"))
                    {
                        RefreshChanges();
                        Revert(changes);
                        RefreshChanges();
                    }
                if (GUILayout.Button("Discard Recording"))
                {
                    snapshot.Clear();
                    changes.Clear();
                }
            }
            EditorGUILayout.EndScrollView();
        }

        static string Describe(VRCAvatarDescriptor avatar, Change change)
        {
            var path = AvatarToolsUtil.GetPath(avatar.transform, TransformOf(change.after.target));
            var target = string.IsNullOrEmpty(path) ? "(root)" : path;
            var from = change.before.isReference ? NameOf(change.before.reference) : change.before.value.ToString("0.###");
            var to = change.after.isReference ? NameOf(change.after.reference) : change.after.value.ToString("0.###");
            return $"{target}  {change.after.target.GetType().Name}.{change.after.property}: {from} -> {to}";
        }

        static string NameOf(UnityEngine.Object obj) => obj == null ? "None" : obj.name;

        static Transform TransformOf(UnityEngine.Object target)
        {
            return target is GameObject go ? go.transform : ((Component)target).transform;
        }

        public static List<Entry> Capture(VRCAvatarDescriptor avatar, bool includeTransforms)
        {
            var entries = new List<Entry>();
            void Add(UnityEngine.Object target, string property, float value) =>
                entries.Add(new Entry { target = target, property = property, value = value });

            foreach (var t in avatar.GetComponentsInChildren<Transform>(true))
            {
                if (t == avatar.transform) continue;
                Add(t.gameObject, "m_IsActive", t.gameObject.activeSelf ? 1 : 0);
                if (!includeTransforms) continue;
                AddVector(entries, t, "m_LocalPosition", t.localPosition);
                AddVector(entries, t, "localEulerAnglesRaw", t.localEulerAngles);
                AddVector(entries, t, "m_LocalScale", t.localScale);
            }
            foreach (var renderer in avatar.GetComponentsInChildren<Renderer>(true))
            {
                Add(renderer, "m_Enabled", renderer.enabled ? 1 : 0);
                var materials = renderer.sharedMaterials;
                for (var i = 0; i < materials.Length; i++)
                    entries.Add(new Entry { target = renderer, property = $"{MaterialPrefix}{i}]", reference = materials[i], isReference = true });
                if (!(renderer is SkinnedMeshRenderer smr) || smr.sharedMesh == null) continue;
                var seen = new HashSet<string>();
                for (var i = 0; i < smr.sharedMesh.blendShapeCount; i++)
                {
                    var shape = smr.sharedMesh.GetBlendShapeName(i);
                    if (seen.Add(shape)) Add(smr, BlendShapePrefix + shape, smr.GetBlendShapeWeight(i));
                }
            }
            return entries;
        }

        static void AddVector(List<Entry> entries, Transform t, string property, Vector3 v)
        {
            entries.Add(new Entry { target = t, property = property + ".x", value = v.x });
            entries.Add(new Entry { target = t, property = property + ".y", value = v.y });
            entries.Add(new Entry { target = t, property = property + ".z", value = v.z });
        }

        public static List<Change> Diff(VRCAvatarDescriptor avatar, List<Entry> snapshot, bool includeTransforms)
        {
            var before = snapshot.Where(e => e.target != null).GroupBy(e => e.Key).ToDictionary(g => g.Key, g => g.First());
            var current = Capture(avatar, includeTransforms).Where(e => before.ContainsKey(e.Key)).ToList();
            bool Differs(Entry e)
            {
                var old = before[e.Key];
                return e.isReference ? old.reference != e.reference : Mathf.Abs(old.value - e.value) > Tolerance;
            }
            var changedVectors = new HashSet<string>(current.Where(e => e.target is Transform && Differs(e)).Select(VectorKey));
            return current.Where(e => e.target is Transform ? changedVectors.Contains(VectorKey(e)) : Differs(e))
                .Select(e => new Change { before = before[e.Key], after = e }).ToList();
        }

        static string VectorKey(Entry e) => e.target.GetInstanceID() + "|" + e.property.Substring(0, e.property.LastIndexOf('.'));

        public static (AnimationClip on, AnimationClip off) SaveClips(VRCAvatarDescriptor avatar, IList<Change> changes, string name, bool createReverse)
        {
            if (changes.Count == 0) throw new InvalidOperationException("There are no changes to save.");
            name = name.Trim();
            var folder = AvatarToolsUtil.AvatarFolder(avatar, "Recorded");
            var on = AnimatorToolsShared.SaveOrReplace(BuildClip(avatar, name, changes.Select(c => c.after)), folder, name, "anim");
            var off = createReverse
                ? AnimatorToolsShared.SaveOrReplace(BuildClip(avatar, name + " Off", changes.Select(c => c.before)), folder, name + "_Off", "anim")
                : null;
            AssetDatabase.SaveAssets();
            return (on, off);
        }

        static AnimationClip BuildClip(VRCAvatarDescriptor avatar, string name, IEnumerable<Entry> entries)
        {
            var clip = new AnimationClip { name = name };
            foreach (var e in entries)
            {
                var path = AvatarToolsUtil.GetPath(avatar.transform, TransformOf(e.target));
                var type = e.target.GetType();
                if (e.isReference)
                    AnimationUtility.SetObjectReferenceCurve(clip, EditorCurveBinding.PPtrCurve(path, type, e.property), new[]
                    {
                        new ObjectReferenceKeyframe { time = 0, value = e.reference },
                        new ObjectReferenceKeyframe { time = AnimatorToolsShared.FrameTime, value = e.reference }
                    });
                else
                    AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve(path, type, e.property),
                        AnimationCurve.Constant(0, AnimatorToolsShared.FrameTime, e.value));
            }
            return clip;
        }

        public static void Revert(IEnumerable<Change> changes)
        {
            var group = Undo.GetCurrentGroup();
            var transformValues = new Dictionary<(Transform, string), Dictionary<char, float>>();
            foreach (var e in changes.Select(c => c.before).Where(e => e.target != null))
            {
                if (e.target is Transform t)
                {
                    var dot = e.property.LastIndexOf('.');
                    var key = (t, e.property.Substring(0, dot));
                    if (!transformValues.TryGetValue(key, out var components)) transformValues[key] = components = new Dictionary<char, float>();
                    components[e.property[dot + 1]] = e.value;
                    continue;
                }
                Undo.RecordObject(e.target, "Revert to Snapshot");
                if (e.target is GameObject go) go.SetActive(e.value > 0.5f);
                else if (e.property == "m_Enabled") ((Renderer)e.target).enabled = e.value > 0.5f;
                else if (e.isReference) SetMaterial((Renderer)e.target, e.property, e.reference as Material);
                else if (e.property.StartsWith(BlendShapePrefix)) SetBlendShape((SkinnedMeshRenderer)e.target, e.property.Substring(BlendShapePrefix.Length), e.value);
                EditorUtility.SetDirty(e.target);
            }
            foreach (var pair in transformValues)
            {
                var (t, property) = pair.Key;
                Undo.RecordObject(t, "Revert to Snapshot");
                var c = pair.Value;
                Vector3 Merge(Vector3 v) => new Vector3(c.TryGetValue('x', out var x) ? x : v.x, c.TryGetValue('y', out var y) ? y : v.y, c.TryGetValue('z', out var z) ? z : v.z);
                if (property == "m_LocalPosition") t.localPosition = Merge(t.localPosition);
                else if (property == "localEulerAnglesRaw") t.localEulerAngles = Merge(t.localEulerAngles);
                else if (property == "m_LocalScale") t.localScale = Merge(t.localScale);
                EditorUtility.SetDirty(t);
            }
            Undo.CollapseUndoOperations(group);
        }

        static void SetMaterial(Renderer renderer, string property, Material material)
        {
            var index = int.Parse(property.Substring(MaterialPrefix.Length, property.Length - MaterialPrefix.Length - 1));
            var materials = renderer.sharedMaterials;
            if (index >= materials.Length) return;
            materials[index] = material;
            renderer.sharedMaterials = materials;
        }

        static void SetBlendShape(SkinnedMeshRenderer smr, string shape, float value)
        {
            var index = smr.sharedMesh == null ? -1 : smr.sharedMesh.GetBlendShapeIndex(shape);
            if (index >= 0) smr.SetBlendShapeWeight(index, value);
        }
    }
}
