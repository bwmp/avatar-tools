using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using VRC.Core;
using VRC.Dynamics;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Avatars.ScriptableObjects;
using VRC.SDKBase.Editor.BuildPipeline;
using VRC.SDKBase.Validation.Performance;
using VRC.SDKBase.Validation.Performance.Stats;

namespace AvatarTools.Editor
{
    public class UploadDiff : EditorWindow
    {
        [Serializable]
        public class ParameterData
        {
            public string Name, Type;
            public bool Synced, Saved;
            public float Default;
        }

        [Serializable]
        public class MenuData
        {
            public string Path, Type, Parameter, SubParameters;
            public float Value;
        }

        [Serializable]
        public class LayerData
        {
            public string Name;
            public float Weight;
            public int States;
        }

        [Serializable]
        public class AnimatorParameterData
        {
            public string Name, Type, Default;
        }

        [Serializable]
        public class PlayableData
        {
            public string Type, Controller;
            public List<LayerData> Layers = new List<LayerData>();
            public List<AnimatorParameterData> Parameters = new List<AnimatorParameterData>();
        }

        [Serializable]
        public class StatData
        {
            public string Category, Value, Rating;
        }

        [Serializable]
        public class RendererData
        {
            public string Path, Type;
            public int Triangles, Blendshapes;
            public List<string> Materials = new List<string>();
        }

        [Serializable]
        public class Snapshot
        {
            public string AvatarName, BlueprintId, TakenAt, Source;
            public int ParameterCost;
            public List<ParameterData> Parameters = new List<ParameterData>();
            public List<MenuData> Menu = new List<MenuData>();
            public List<PlayableData> Playables = new List<PlayableData>();
            public string PcRating, MobileRating;
            public List<StatData> PcStats = new List<StatData>();
            public List<StatData> MobileStats = new List<StatData>();
            public List<RendererData> Renderers = new List<RendererData>();
            public int PhysBones, PhysBoneColliders, ContactSenders, ContactReceivers;
        }

        public enum ChangeKind { Added, Removed, Changed }

        public class DiffLine
        {
            public ChangeKind Kind;
            public string Key, Old, New;
        }

        public class DiffSection
        {
            public string Name;
            public List<DiffLine> Lines = new List<DiffLine>();
        }

        public class StoredSnapshot
        {
            public string FilePath;
            public Snapshot Data;
            public string Label => $"{Data.TakenAt}  ({Data.Source})";
        }

        const string AutoSnapshotPref = "AvatarTools.UploadDiff.AutoSnapshot";
        static readonly Color AddedColor = new Color(0.45f, 0.85f, 0.45f);
        static readonly Color RemovedColor = new Color(1f, 0.45f, 0.45f);
        static readonly Color ChangedColor = new Color(0.95f, 0.8f, 0.3f);

        VRCAvatarDescriptor avatar;
        List<StoredSnapshot> snapshots = new List<StoredSnapshot>();
        Snapshot live;
        int oldIndex = 1, newIndex;
        List<DiffSection> diff;
        readonly HashSet<string> collapsed = new HashSet<string>();
        Vector2 listScroll, diffScroll;

        public static bool AutoSnapshot
        {
            get => EditorPrefs.GetBool(AutoSnapshotPref, true);
            set => EditorPrefs.SetBool(AutoSnapshotPref, value);
        }

        public static string SnapshotRoot => Path.GetFullPath(Path.Combine("Library", "AvatarTools", "UploadSnapshots"));

        [MenuItem(AvatarToolsUtil.MenuRoot + "Setup/Upload Diff")]
        static void Open() => GetWindow<UploadDiff>("Upload Diff");

        void OnEnable()
        {
            avatar = avatar ? avatar : AvatarToolsUtil.SelectedAvatar();
            Reload();
        }

        public static Snapshot Capture(VRCAvatarDescriptor avatar, string source)
        {
            var root = avatar.gameObject;
            var pipeline = root.GetComponent<PipelineManager>();
            var snapshot = new Snapshot
            {
                AvatarName = StripClone(root.name),
                BlueprintId = pipeline != null ? pipeline.blueprintId ?? "" : "",
                TakenAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                Source = source,
                ParameterCost = AvatarToolsUtil.ParameterCost(avatar.expressionParameters)
            };

            if (avatar.expressionParameters != null && avatar.expressionParameters.parameters != null)
                foreach (var p in avatar.expressionParameters.parameters)
                {
                    if (p == null || string.IsNullOrEmpty(p.name)) continue;
                    snapshot.Parameters.Add(new ParameterData
                    {
                        Name = p.name, Type = p.valueType.ToString(), Synced = p.networkSynced, Saved = p.saved, Default = p.defaultValue
                    });
                }

            FlattenMenu(avatar.expressionsMenu, "", snapshot.Menu, new HashSet<VRCExpressionsMenu>());

            foreach (var (type, controller) in AnalysisUtil.PlayableLayers(avatar))
            {
                var playable = new PlayableData { Type = type.ToString(), Controller = controller.name };
                foreach (var layer in controller.layers)
                    playable.Layers.Add(new LayerData
                    {
                        Name = layer.name, Weight = layer.defaultWeight,
                        States = layer.stateMachine != null ? AvatarToolsUtil.AllStates(layer.stateMachine).Count() : 0
                    });
                foreach (var p in controller.parameters)
                    playable.Parameters.Add(new AnimatorParameterData { Name = p.name, Type = p.type.ToString(), Default = ParameterDefault(p) });
                snapshot.Playables.Add(playable);
            }

            var performance = PerformanceRankPreview.Calculate(root, avatar);
            snapshot.PcRating = performance.Pc.GetPerformanceRatingForCategory(AvatarPerformanceCategory.Overall).ToString();
            snapshot.MobileRating = performance.Mobile.GetPerformanceRatingForCategory(AvatarPerformanceCategory.Overall).ToString();
            snapshot.PcStats = Stats(performance.Pc);
            snapshot.MobileStats = Stats(performance.Mobile);

            foreach (var renderer in root.GetComponentsInChildren<Renderer>(true))
            {
                var mesh = renderer is SkinnedMeshRenderer skinned ? skinned.sharedMesh
                    : renderer.TryGetComponent<MeshFilter>(out var filter) ? filter.sharedMesh : null;
                snapshot.Renderers.Add(new RendererData
                {
                    Path = AvatarToolsUtil.GetPath(root.transform, renderer.transform),
                    Type = renderer.GetType().Name,
                    Triangles = mesh != null ? TriangleCount(mesh) : 0,
                    Blendshapes = renderer is SkinnedMeshRenderer && mesh != null ? mesh.blendShapeCount : 0,
                    Materials = renderer.sharedMaterials.Select(m => m != null ? m.name : "(none)").ToList()
                });
            }

            snapshot.PhysBones = root.GetComponentsInChildren<VRCPhysBoneBase>(true).Length;
            snapshot.PhysBoneColliders = root.GetComponentsInChildren<VRCPhysBoneColliderBase>(true).Length;
            snapshot.ContactSenders = root.GetComponentsInChildren<ContactSender>(true).Length;
            snapshot.ContactReceivers = root.GetComponentsInChildren<ContactReceiver>(true).Length;
            return snapshot;
        }

        static string StripClone(string name)
        {
            return name.EndsWith("(Clone)", StringComparison.Ordinal) ? name.Substring(0, name.Length - "(Clone)".Length).TrimEnd() : name;
        }

        static string ParameterDefault(AnimatorControllerParameter p)
        {
            switch (p.type)
            {
                case AnimatorControllerParameterType.Float: return p.defaultFloat.ToString("0.###");
                case AnimatorControllerParameterType.Int: return p.defaultInt.ToString();
                case AnimatorControllerParameterType.Bool: return p.defaultBool ? "true" : "false";
                default: return "";
            }
        }

        static void FlattenMenu(VRCExpressionsMenu menu, string prefix, List<MenuData> result, HashSet<VRCExpressionsMenu> ancestors)
        {
            if (menu == null || !ancestors.Add(menu)) return;
            foreach (var control in menu.controls)
            {
                var path = prefix + control.name;
                result.Add(new MenuData
                {
                    Path = path, Type = control.type.ToString(), Parameter = control.parameter?.name ?? "", Value = control.value,
                    SubParameters = string.Join(", ", (control.subParameters ?? Array.Empty<VRCExpressionsMenu.Control.Parameter>())
                        .Select(s => s?.name ?? "").Where(s => s != ""))
                });
                if (control.type == VRCExpressionsMenu.Control.ControlType.SubMenu)
                    FlattenMenu(control.subMenu, path + "/", result, ancestors);
            }
            ancestors.Remove(menu);
        }

        static List<StatData> Stats(AvatarPerformanceStats stats)
        {
            var result = new List<StatData>();
            foreach (AvatarPerformanceCategory category in Enum.GetValues(typeof(AvatarPerformanceCategory)))
            {
                if (category == AvatarPerformanceCategory.None || category == AvatarPerformanceCategory.Overall ||
                    category == AvatarPerformanceCategory.AvatarPerformanceCategoryCount) continue;
                var value = PerformanceRankPreview.FormatValue(stats, category);
                if (value == "-") continue;
                result.Add(new StatData
                {
                    Category = AvatarPerformanceStats.GetPerformanceCategoryDisplayName(category), Value = value,
                    Rating = stats.GetPerformanceRatingForCategory(category).ToString()
                });
            }
            return result;
        }

        static int TriangleCount(Mesh mesh)
        {
            var total = 0L;
            for (var i = 0; i < mesh.subMeshCount; i++)
            {
                var topology = mesh.GetTopology(i);
                if (topology == MeshTopology.Triangles) total += mesh.GetIndexCount(i) / 3;
                else if (topology == MeshTopology.Quads) total += mesh.GetIndexCount(i) / 2;
            }
            return (int)total;
        }

        static string FolderFor(string key) => Path.Combine(SnapshotRoot, AvatarToolsUtil.SafeName(key));

        public static string Save(Snapshot snapshot)
        {
            var folder = FolderFor(string.IsNullOrEmpty(snapshot.BlueprintId) ? snapshot.AvatarName : snapshot.BlueprintId);
            Directory.CreateDirectory(folder);
            var stem = DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + snapshot.Source.ToLowerInvariant();
            var path = Path.Combine(folder, stem + ".json");
            for (var i = 2; File.Exists(path); i++) path = Path.Combine(folder, $"{stem}-{i}.json");
            File.WriteAllText(path, JsonUtility.ToJson(snapshot, true));
            return path;
        }

        public static List<StoredSnapshot> Load(string blueprintId, string avatarName)
        {
            var result = new List<StoredSnapshot>();
            var keys = new[] { blueprintId, avatarName }.Where(k => !string.IsNullOrEmpty(k)).Select(FolderFor).Distinct();
            foreach (var folder in keys)
            {
                if (!Directory.Exists(folder)) continue;
                foreach (var file in Directory.GetFiles(folder, "*.json"))
                {
                    Snapshot data;
                    try
                    {
                        data = JsonUtility.FromJson<Snapshot>(File.ReadAllText(file));
                    }
                    catch (ArgumentException e)
                    {
                        Debug.LogWarning($"Upload Diff: skipping unreadable snapshot {file}: {e.Message}");
                        continue;
                    }
                    if (data != null) result.Add(new StoredSnapshot { FilePath = file, Data = data });
                }
            }
            return result.OrderByDescending(s => Path.GetFileNameWithoutExtension(s.FilePath), StringComparer.Ordinal).ToList();
        }

        public static List<(string section, List<(string key, string value)> entries)> Flatten(Snapshot s)
        {
            var sections = new List<(string, List<(string, string)>)>();
            void Section(string name, IEnumerable<(string key, string value)> entries)
            {
                var list = new List<(string, string)>();
                var seen = new Dictionary<string, int>();
                foreach (var (key, value) in entries)
                {
                    seen[key] = seen.TryGetValue(key, out var n) ? n + 1 : 1;
                    list.Add((seen[key] > 1 ? $"{key} #{seen[key]}" : key, value));
                }
                sections.Add((name, list));
            }

            Section("General", new[]
            {
                ("Avatar name", s.AvatarName), ("Blueprint ID", s.BlueprintId),
                ("Expression parameter cost", $"{s.ParameterCost} / {VRCExpressionParameters.MAX_PARAMETER_COST} bits"),
                ("PhysBones", s.PhysBones.ToString()), ("PhysBone colliders", s.PhysBoneColliders.ToString()),
                ("Contact senders", s.ContactSenders.ToString()), ("Contact receivers", s.ContactReceivers.ToString()),
                ("Renderers", s.Renderers.Count.ToString()),
                ("Total blendshapes", s.Renderers.Sum(r => r.Blendshapes).ToString()),
                ("Total triangles", s.Renderers.Sum(r => (long)r.Triangles).ToString())
            });
            Section("Performance (PC)", new[] { ("Overall", s.PcRating) }.Concat(s.PcStats.Select(x => (x.Category, $"{x.Value} ({x.Rating})"))));
            Section("Performance (Mobile)", new[] { ("Overall", s.MobileRating) }.Concat(s.MobileStats.Select(x => (x.Category, $"{x.Value} ({x.Rating})"))));
            Section("Expression Parameters", s.Parameters.Select(p =>
                (p.Name, $"{p.Type}, {(p.Synced ? "synced" : "local")}, {(p.Saved ? "saved" : "not saved")}, default {p.Default:0.###}")));
            Section("Expressions Menu", s.Menu.Select(m => ($"{m.Path} [{m.Type}]",
                (m.Parameter != "" ? $"{m.Parameter} = {m.Value:0.###}" : "no parameter") + (m.SubParameters != "" ? $"; sub: {m.SubParameters}" : ""))));
            foreach (var playable in s.Playables)
                Section("Playable Layer: " + playable.Type, new[] { ("Controller", playable.Controller) }
                    .Concat(playable.Layers.Select(l => ("Layer: " + l.Name, $"weight {l.Weight:0.##}, {l.States} state(s)")))
                    .Concat(playable.Parameters.Select(p => ("Parameter: " + p.Name, $"{p.Type} = {p.Default}"))));
            Section("Renderers", s.Renderers.Select(r => (r.Path == "" ? "(root)" : r.Path,
                $"{r.Type}, {r.Triangles} tris, {r.Blendshapes} blendshapes, materials: {string.Join(", ", r.Materials)}")));
            return sections;
        }

        public static List<DiffSection> Diff(Snapshot older, Snapshot newer)
        {
            var a = Flatten(older);
            var b = Flatten(newer);
            var names = a.Select(x => x.section).Concat(b.Select(x => x.section)).Distinct();
            var result = new List<DiffSection>();
            foreach (var name in names)
            {
                var before = a.Where(x => x.section == name).SelectMany(x => x.entries).ToList();
                var after = b.Where(x => x.section == name).SelectMany(x => x.entries).ToList();
                var beforeMap = before.ToDictionary(e => e.key, e => e.value);
                var afterMap = after.ToDictionary(e => e.key, e => e.value);
                var section = new DiffSection { Name = name };
                foreach (var (key, value) in before)
                {
                    if (!afterMap.TryGetValue(key, out var now)) section.Lines.Add(new DiffLine { Kind = ChangeKind.Removed, Key = key, Old = value });
                    else if (now != value) section.Lines.Add(new DiffLine { Kind = ChangeKind.Changed, Key = key, Old = value, New = now });
                }
                foreach (var (key, value) in after)
                    if (!beforeMap.ContainsKey(key)) section.Lines.Add(new DiffLine { Kind = ChangeKind.Added, Key = key, New = value });
                if (section.Lines.Count > 0) result.Add(section);
            }
            return result;
        }

        void Reload()
        {
            if (avatar == null)
            {
                snapshots = new List<StoredSnapshot>();
                live = null;
                diff = null;
                Repaint();
                return;
            }
            var pipeline = avatar.GetComponent<PipelineManager>();
            snapshots = Load(pipeline != null ? pipeline.blueprintId : null, avatar.gameObject.name);
            live = Capture(avatar, "Live");
            oldIndex = Mathf.Clamp(oldIndex, 0, snapshots.Count);
            newIndex = Mathf.Clamp(newIndex, 0, snapshots.Count);
            if (oldIndex == newIndex && snapshots.Count > 0) oldIndex = newIndex == 0 ? 1 : 0;
            Recompare();
        }

        Snapshot Option(int index) => index == 0 ? live : snapshots[index - 1].Data;

        void Recompare()
        {
            diff = live != null && snapshots.Count > 0 ? Diff(Option(oldIndex), Option(newIndex)) : null;
            Repaint();
        }

        void OnGUI()
        {
            EditorGUI.BeginChangeCheck();
            avatar = AvatarToolsUtil.AvatarField(avatar);
            if (EditorGUI.EndChangeCheck()) Reload();
            if (avatar == null)
            {
                EditorGUILayout.HelpBox("Select an avatar with a VRC Avatar Descriptor.", MessageType.Info);
                return;
            }

            AutoSnapshot = EditorGUILayout.ToggleLeft("Take a snapshot automatically on every SDK build / upload", AutoSnapshot);
            if (GUILayout.Button("Take Snapshot Now", GUILayout.Height(28)))
            {
                Save(Capture(avatar, "Manual"));
                newIndex = 0;
                oldIndex = 1;
                Reload();
                GUIUtility.ExitGUI();
            }

            EditorGUILayout.LabelField($"Snapshots ({snapshots.Count})", EditorStyles.boldLabel);
            if (snapshots.Count == 0)
            {
                EditorGUILayout.HelpBox("No snapshots yet for this avatar. Take one now or build/upload with auto-snapshot enabled.", MessageType.Info);
                return;
            }
            listScroll = EditorGUILayout.BeginScrollView(listScroll, GUILayout.MaxHeight(140));
            foreach (var stored in snapshots)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField(stored.Label, GUILayout.MinWidth(200));
                    EditorGUILayout.LabelField($"PC {stored.Data.PcRating}, Mobile {stored.Data.MobileRating}, {stored.Data.ParameterCost} bits", EditorStyles.miniLabel);
                    if (GUILayout.Button("Show File", EditorStyles.miniButtonLeft, GUILayout.Width(70))) EditorUtility.RevealInFinder(stored.FilePath);
                    if (GUILayout.Button("Delete", EditorStyles.miniButtonRight, GUILayout.Width(50)) &&
                        EditorUtility.DisplayDialog("Delete Snapshot", $"Delete the snapshot from {stored.Data.TakenAt}?", "Delete", "Cancel"))
                    {
                        File.Delete(stored.FilePath);
                        Reload();
                        GUIUtility.ExitGUI();
                    }
                }
            }
            EditorGUILayout.EndScrollView();

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Compare", EditorStyles.boldLabel);
            var options = new[] { "Current (live)" }.Concat(snapshots.Select(s => s.Label)).ToArray();
            EditorGUI.BeginChangeCheck();
            oldIndex = EditorGUILayout.Popup("Old", oldIndex, options);
            newIndex = EditorGUILayout.Popup("New", newIndex, options);
            if (EditorGUI.EndChangeCheck()) Recompare();
            if (GUILayout.Button("Refresh Live State", GUILayout.Width(140)))
            {
                live = Capture(avatar, "Live");
                Recompare();
            }

            if (diff == null) return;
            if (diff.Count == 0)
            {
                EditorGUILayout.HelpBox("No differences.", MessageType.Info);
                return;
            }
            diffScroll = EditorGUILayout.BeginScrollView(diffScroll);
            foreach (var section in diff)
            {
                var open = !collapsed.Contains(section.Name);
                var label = $"{section.Name}   +{section.Lines.Count(l => l.Kind == ChangeKind.Added)} " +
                            $"-{section.Lines.Count(l => l.Kind == ChangeKind.Removed)} ~{section.Lines.Count(l => l.Kind == ChangeKind.Changed)}";
                var now = EditorGUILayout.Foldout(open, label, true, EditorStyles.foldoutHeader);
                if (now != open)
                {
                    if (now) collapsed.Remove(section.Name);
                    else collapsed.Add(section.Name);
                }
                if (!now) continue;
                using (new EditorGUI.IndentLevelScope())
                    foreach (var line in section.Lines) DrawLine(line);
            }
            EditorGUILayout.EndScrollView();
        }

        static void DrawLine(DiffLine line)
        {
            string text;
            Color color;
            switch (line.Kind)
            {
                case ChangeKind.Added: text = $"+ {line.Key}: {line.New}"; color = AddedColor; break;
                case ChangeKind.Removed: text = $"- {line.Key}: {line.Old}"; color = RemovedColor; break;
                default: text = $"~ {line.Key}: {line.Old}  ->  {line.New}"; color = ChangedColor; break;
            }
            var previous = GUI.contentColor;
            GUI.contentColor = color;
            EditorGUILayout.LabelField(text, EditorStyles.wordWrappedLabel);
            GUI.contentColor = previous;
        }
    }

    internal class UploadDiffBuildHook : IVRCSDKPreprocessAvatarCallback
    {
        public int callbackOrder => 10000;

        public bool OnPreprocessAvatar(GameObject avatarGameObject)
        {
            if (!UploadDiff.AutoSnapshot) return true;
            var avatar = avatarGameObject.GetComponent<VRCAvatarDescriptor>();
            if (avatar == null) return true;
            // The snapshot is purely informational, so no failure in it may ever block the user's build or upload.
            try
            {
                UploadDiff.Save(UploadDiff.Capture(avatar, "Build"));
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }
            return true;
        }
    }
}
