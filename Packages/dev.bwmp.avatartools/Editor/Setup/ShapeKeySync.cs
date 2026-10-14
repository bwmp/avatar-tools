using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;
using VRC.SDK3.Avatars.Components;

namespace AvatarTools.Editor
{
    public class ShapeKeySync : EditorWindow
    {
        public class Row
        {
            public string SourceName;
            public int SourceIndex;
            public readonly Dictionary<SkinnedMeshRenderer, int> TargetIndices = new Dictionary<SkinnedMeshRenderer, int>();
        }

        public class AnimationSyncResult
        {
            public int ClipsChanged;
            public int CurvesAdded;
            public readonly List<string> Skipped = new List<string>();
        }

        const double LiveInterval = 0.1;
        const string BlendShapePrefix = "blendShape.";

        VRCAvatarDescriptor avatar;
        SkinnedMeshRenderer source;
        readonly List<SkinnedMeshRenderer> targets = new List<SkinnedMeshRenderer>();
        bool normalized;
        bool liveSync;
        bool showUnmatched;
        List<Row> rows = new List<Row>();
        float[] lastWeights;
        double nextLiveCheck;
        Vector2 scroll;

        [MenuItem(AvatarToolsUtil.MenuRoot + "Setup/Shape Key Sync")]
        static void Open()
        {
            GetWindow<ShapeKeySync>("Shape Key Sync");
        }

        void OnEnable()
        {
            avatar = AvatarToolsUtil.SelectedAvatar();
            EditorApplication.update += LiveUpdate;
        }

        void OnDisable()
        {
            EditorApplication.update -= LiveUpdate;
        }

        void OnGUI()
        {
            EditorGUI.BeginChangeCheck();
            avatar = AvatarToolsUtil.AvatarField(avatar);
            source = (SkinnedMeshRenderer)EditorGUILayout.ObjectField("Source", source, typeof(SkinnedMeshRenderer), true);
            normalized = EditorGUILayout.ToggleLeft(new GUIContent("Case-insensitive / normalized matching",
                "Ignore case, spaces, dots, dashes and underscores when matching names."), normalized);
            if (EditorGUI.EndChangeCheck()) Rebuild();

            if (avatar == null)
            {
                EditorGUILayout.HelpBox("Pick an avatar.", MessageType.Info);
                return;
            }
            if (source == null)
            {
                if (GUILayout.Button("Use Body")) source = GuessBody(avatar);
                EditorGUILayout.HelpBox("Pick the source skinned mesh (usually Body).", MessageType.Info);
                Rebuild();
                return;
            }
            if (!source.transform.IsChildOf(avatar.transform) || source.sharedMesh == null)
            {
                EditorGUILayout.HelpBox("The source must be a skinned mesh with a mesh under the avatar.", MessageType.Warning);
                return;
            }

            DrawTargets();
            DrawTable();
            DrawActions();
        }

        void DrawTargets()
        {
            EditorGUILayout.LabelField("Targets", EditorStyles.boldLabel);
            var changed = false;
            for (var i = 0; i < targets.Count; i++)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    var next = (SkinnedMeshRenderer)EditorGUILayout.ObjectField(targets[i], typeof(SkinnedMeshRenderer), true);
                    if (next != targets[i])
                    {
                        targets[i] = next;
                        changed = true;
                    }
                    if (GUILayout.Button("Remove", GUILayout.Width(70)))
                    {
                        targets.RemoveAt(i);
                        Rebuild();
                        GUIUtility.ExitGUI();
                    }
                }
            }
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Add all clothing"))
                {
                    foreach (var r in avatar.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                        if (r != source && !targets.Contains(r)) targets.Add(r);
                    changed = true;
                }
                if (GUILayout.Button("Add Selected"))
                {
                    foreach (var r in Selection.gameObjects.Select(g => g.GetComponent<SkinnedMeshRenderer>()))
                        if (r != null && r != source && !targets.Contains(r)) targets.Add(r);
                    changed = true;
                }
                if (GUILayout.Button("Clear"))
                {
                    targets.Clear();
                    changed = true;
                }
            }
            if (changed) Rebuild();
        }

        void DrawTable()
        {
            var valid = ValidTargets();
            var matched = rows.Where(r => r.TargetIndices.Count > 0).ToList();
            EditorGUILayout.Space();
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField($"{matched.Count} of {rows.Count} source shape keys match at least one target", EditorStyles.boldLabel);
                showUnmatched = GUILayout.Toggle(showUnmatched, "Show unmatched", GUILayout.Width(110));
            }
            scroll = EditorGUILayout.BeginScrollView(scroll, GUILayout.MinHeight(120));
            foreach (var row in showUnmatched ? rows : matched)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField(row.SourceName, GUILayout.MinWidth(140));
                    GUILayout.Label(source.GetBlendShapeWeight(row.SourceIndex).ToString("0.##"), GUILayout.Width(50));
                    var names = valid.Where(t => row.TargetIndices.ContainsKey(t)).Select(t => t.name).ToList();
                    var label = names.Count == 0 ? "-" : string.Join(", ", names);
                    GUILayout.Label(new GUIContent(label, label), EditorStyles.miniLabel, GUILayout.MinWidth(120));
                }
            }
            EditorGUILayout.EndScrollView();
        }

        void DrawActions()
        {
            var valid = ValidTargets();
            var matchCount = rows.Sum(r => r.TargetIndices.Count);
            if (valid.Count == 0)
            {
                EditorGUILayout.HelpBox("Add target skinned meshes.", MessageType.Info);
                return;
            }
            if (matchCount == 0) EditorGUILayout.HelpBox("No matching shape keys between the source and the targets.", MessageType.Warning);

            var nextLive = EditorGUILayout.ToggleLeft(new GUIContent("Live sync",
                "While this window is open, copy source weight changes to the targets."), liveSync);
            if (nextLive != liveSync)
            {
                liveSync = nextLive;
                lastWeights = null;
            }

            using (new EditorGUI.DisabledScope(matchCount == 0))
            {
                if (GUILayout.Button("Sync weights", GUILayout.Height(30)))
                {
                    var count = SyncWeights(source, rows);
                    ShowNotification(new GUIContent($"Set {count} weights"));
                }
                if (GUILayout.Button("Sync animations"))
                    RunAnimationSync();
            }
        }

        void RunAnimationSync()
        {
            var clips = AvatarToolsUtil.AllControllers(avatar).SelectMany(AvatarToolsUtil.AllClips).Distinct().ToList();
            if (!EditorUtility.DisplayDialog("Sync Shape Key Animations",
                    $"Add matching target curves to every clip ({clips.Count} found in the avatar's controllers) that animates shape keys on \"{source.name}\"?", "Sync", "Cancel"))
                return;
            var result = SyncAnimations(avatar, source, rows, clips);
            var message = $"Added {result.CurvesAdded} curves to {result.ClipsChanged} clips.";
            if (result.Skipped.Count > 0)
                message += $"\n\nSkipped {result.Skipped.Count} read-only clips:\n" + string.Join("\n", result.Skipped.Take(15)) +
                           (result.Skipped.Count > 15 ? $"\n... and {result.Skipped.Count - 15} more" : "");
            EditorUtility.DisplayDialog("Sync Shape Key Animations", message, "OK");
        }

        List<SkinnedMeshRenderer> ValidTargets()
        {
            return targets.Where(t => t != null && t != source && t.sharedMesh != null).Distinct().ToList();
        }

        void Rebuild()
        {
            rows = source == null || source.sharedMesh == null ? new List<Row>() : BuildRows(source, ValidTargets(), normalized);
            lastWeights = null;
        }

        void LiveUpdate()
        {
            if (!liveSync || source == null || source.sharedMesh == null || EditorApplication.timeSinceStartup < nextLiveCheck) return;
            nextLiveCheck = EditorApplication.timeSinceStartup + LiveInterval;
            var count = source.sharedMesh.blendShapeCount;
            var current = new float[count];
            for (var i = 0; i < count; i++) current[i] = source.GetBlendShapeWeight(i);
            if (lastWeights != null && lastWeights.Length == count)
            {
                var changed = rows.Where(r => r.TargetIndices.Count > 0 && !Mathf.Approximately(current[r.SourceIndex], lastWeights[r.SourceIndex])).ToList();
                if (changed.Count > 0)
                {
                    SyncWeights(source, changed);
                    Repaint();
                }
            }
            lastWeights = current;
        }

        public static SkinnedMeshRenderer GuessBody(VRCAvatarDescriptor avatar)
        {
            var renderers = avatar.GetComponentsInChildren<SkinnedMeshRenderer>(true).Where(r => r.sharedMesh != null).ToList();
            if (avatar.VisemeSkinnedMesh != null) return avatar.VisemeSkinnedMesh;
            return renderers.FirstOrDefault(r => r.name.Equals("Body", StringComparison.OrdinalIgnoreCase))
                   ?? renderers.OrderByDescending(r => r.sharedMesh.blendShapeCount).FirstOrDefault();
        }

        public static string Normalize(string name)
        {
            return Regex.Replace(name, @"[\s._\-]", "").ToLowerInvariant();
        }

        public static List<Row> BuildRows(SkinnedMeshRenderer source, IEnumerable<SkinnedMeshRenderer> targets, bool normalized)
        {
            Func<string, string> key = normalized ? Normalize : (Func<string, string>)(n => n);
            var mesh = source.sharedMesh;
            var rows = new List<Row>();
            for (var i = 0; i < mesh.blendShapeCount; i++)
                rows.Add(new Row { SourceName = mesh.GetBlendShapeName(i), SourceIndex = i });
            foreach (var target in targets)
            {
                var lookup = new Dictionary<string, int>();
                for (var i = 0; i < target.sharedMesh.blendShapeCount; i++)
                {
                    var k = key(target.sharedMesh.GetBlendShapeName(i));
                    if (!lookup.ContainsKey(k)) lookup[k] = i;
                }
                foreach (var row in rows)
                    if (lookup.TryGetValue(key(row.SourceName), out var index))
                        row.TargetIndices[target] = index;
            }
            return rows;
        }

        public static int SyncWeights(SkinnedMeshRenderer source, IEnumerable<Row> rows)
        {
            var list = rows.ToList();
            var renderers = list.SelectMany(r => r.TargetIndices.Keys).Where(t => t != null).Distinct().ToArray();
            if (renderers.Length == 0) return 0;
            Undo.RecordObjects(renderers.Cast<UnityEngine.Object>().ToArray(), "Sync Shape Key Weights");
            var count = 0;
            foreach (var row in list)
            {
                var weight = source.GetBlendShapeWeight(row.SourceIndex);
                foreach (var pair in row.TargetIndices)
                {
                    if (pair.Key == null || Mathf.Approximately(pair.Key.GetBlendShapeWeight(pair.Value), weight)) continue;
                    pair.Key.SetBlendShapeWeight(pair.Value, weight);
                    count++;
                }
            }
            foreach (var r in renderers) PrefabUtility.RecordPrefabInstancePropertyModifications(r);
            return count;
        }

        public static bool IsWritable(UnityEngine.Object asset)
        {
            var path = AssetDatabase.GetAssetPath(asset);
            return path.StartsWith("Assets/", StringComparison.Ordinal) && !(AssetImporter.GetAtPath(path) is ModelImporter);
        }

        public static AnimationSyncResult SyncAnimations(VRCAvatarDescriptor avatar, SkinnedMeshRenderer source, IList<Row> rows, IEnumerable<AnimationClip> clips)
        {
            var result = new AnimationSyncResult();
            var root = avatar.transform;
            var sourcePath = AvatarToolsUtil.GetPath(root, source.transform);
            var bySourceName = rows.Where(r => r.TargetIndices.Count > 0).ToDictionary(r => r.SourceName);
            foreach (var clip in clips.Where(c => c != null).Distinct())
            {
                var bindings = AnimationUtility.GetCurveBindings(clip)
                    .Where(b => b.path == sourcePath && b.type == typeof(SkinnedMeshRenderer) && b.propertyName.StartsWith(BlendShapePrefix, StringComparison.Ordinal))
                    .ToList();
                var additions = new List<(EditorCurveBinding binding, AnimationCurve curve)>();
                foreach (var binding in bindings)
                {
                    if (!bySourceName.TryGetValue(binding.propertyName.Substring(BlendShapePrefix.Length), out var row)) continue;
                    var curve = AnimationUtility.GetEditorCurve(clip, binding);
                    foreach (var pair in row.TargetIndices.Where(p => p.Key != null && p.Key.transform.IsChildOf(root)))
                    {
                        var targetBinding = EditorCurveBinding.FloatCurve(AvatarToolsUtil.GetPath(root, pair.Key.transform), typeof(SkinnedMeshRenderer),
                            BlendShapePrefix + pair.Key.sharedMesh.GetBlendShapeName(pair.Value));
                        additions.Add((targetBinding, curve));
                    }
                }
                if (additions.Count == 0) continue;
                if (!IsWritable(clip))
                {
                    result.Skipped.Add($"{clip.name} ({AssetDatabase.GetAssetPath(clip)})");
                    continue;
                }
                Undo.RecordObject(clip, "Sync Shape Key Animations");
                foreach (var (binding, curve) in additions)
                    AnimationUtility.SetEditorCurve(clip, binding, curve);
                EditorUtility.SetDirty(clip);
                result.ClipsChanged++;
                result.CurvesAdded += additions.Count;
            }
            AssetDatabase.SaveAssets();
            return result;
        }
    }
}
