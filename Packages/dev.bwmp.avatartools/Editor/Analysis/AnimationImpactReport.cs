using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using VRC.SDKBase;
using AnimLayerType = VRC.SDK3.Avatars.Components.VRCAvatarDescriptor.AnimLayerType;

namespace AvatarTools.Editor
{
    public class AnimationImpactReport : EditorWindow
    {
        public class LayerRef
        {
            public AnimLayerType Playable;
            public AnimatorController Controller;
            public int LayerIndex;
            public string LayerName;
            public string Label => $"{Playable} / {LayerName} (#{LayerIndex})";
        }

        public class StateImpact
        {
            public LayerRef Layer;
            public string StatePath;
            public AnimatorState State;
            public AnimationClip Clip;
            public Dictionary<string, string> Parameters = new Dictionary<string, string>();
            public List<string> Properties = new List<string>();
        }

        public class DriverRef
        {
            public LayerRef Layer;
            public string StatePath;
            public AnimatorState State;
            public string Description;
        }

        public class ParameterInfo
        {
            public string Name;
            public string Source;
            public List<StateImpact> Impacts = new List<StateImpact>();
            public List<DriverRef> DrivenBy = new List<DriverRef>();
        }

        public class TargetInfo
        {
            public string Property;
            public List<StateImpact> Touches = new List<StateImpact>();
            public bool LayerConflict;
            public bool ParameterConflict;
            public LayerRef Winner;
            public bool Conflict => LayerConflict || ParameterConflict;
        }

        public class Report
        {
            public List<StateImpact> Impacts = new List<StateImpact>();
            public List<ParameterInfo> Parameters = new List<ParameterInfo>();
            public List<TargetInfo> Targets = new List<TargetInfo>();
        }

        static readonly string[] Views = { "By Parameter", "By Target" };
        const string ComponentSuffixes = "xyzwrgba";

        VRCAvatarDescriptor avatar;
        Report report;
        int view;
        string search = "";
        bool conflictsOnly;
        readonly HashSet<string> expanded = new HashSet<string>();
        Vector2 scroll;

        [MenuItem(AvatarToolsUtil.MenuRoot + "Expressions/Animation Impact Report")]
        static void Open() => GetWindow<AnimationImpactReport>("Animation Impact");

        void OnEnable()
        {
            avatar = avatar ? avatar : AvatarToolsUtil.SelectedAvatar();
            Undo.undoRedoPerformed += Refresh;
            Refresh();
        }

        void OnDisable() => Undo.undoRedoPerformed -= Refresh;

        void Refresh()
        {
            report = avatar != null ? Analyze(avatar) : null;
            Repaint();
        }

        public static Report Analyze(VRCAvatarDescriptor avatar)
        {
            var report = new Report();
            var bindingCache = new Dictionary<AnimationClip, List<string>>();
            var drivers = new List<(string name, DriverRef driver)>();
            var animatorParameters = new HashSet<string>();

            foreach (var (type, controller) in AnalysisUtil.PlayableLayers(avatar))
            {
                animatorParameters.UnionWith(controller.parameters.Select(p => p.name));
                var layers = controller.layers;
                for (var i = 0; i < layers.Length; i++)
                {
                    var layer = layers[i];
                    var synced = layer.syncedLayerIndex >= 0 && layer.syncedLayerIndex < layers.Length;
                    var root = synced ? layers[layer.syncedLayerIndex].stateMachine : layer.stateMachine;
                    if (root == null) continue;
                    var layerRef = new LayerRef { Playable = type, Controller = controller, LayerIndex = i, LayerName = layer.name };
                    CollectLayer(report, layerRef, root, synced ? layer : null, bindingCache, drivers);
                }
            }

            var names = new SortedDictionary<string, ParameterInfo>(StringComparer.Ordinal);
            ParameterInfo Get(string name)
            {
                if (!names.TryGetValue(name, out var info)) names[name] = info = new ParameterInfo { Name = name, Source = "Animator" };
                return info;
            }
            foreach (var p in avatar.expressionParameters != null && avatar.expressionParameters.parameters != null
                         ? avatar.expressionParameters.parameters : Array.Empty<VRC.SDK3.Avatars.ScriptableObjects.VRCExpressionParameters.Parameter>())
            {
                if (p == null || string.IsNullOrEmpty(p.name)) continue;
                Get(p.name).Source = animatorParameters.Contains(p.name) ? "Expression + Animator" : "Expression";
            }
            foreach (var name in animatorParameters) Get(name);
            foreach (var impact in report.Impacts)
            foreach (var name in impact.Parameters.Keys)
                Get(name).Impacts.Add(impact);
            foreach (var (name, driver) in drivers) Get(name).DrivenBy.Add(driver);
            foreach (var info in names.Values)
                if (AnalysisUtil.BuiltInParameters.Contains(info.Name) && info.Source == "Animator") info.Source = "VRChat built-in";
            report.Parameters = names.Values.ToList();

            var targets = new SortedDictionary<string, TargetInfo>(StringComparer.Ordinal);
            foreach (var impact in report.Impacts)
            foreach (var property in impact.Properties)
            {
                if (!targets.TryGetValue(property, out var target)) targets[property] = target = new TargetInfo { Property = property };
                target.Touches.Add(impact);
            }
            foreach (var target in targets.Values) EvaluateConflicts(target);
            report.Targets = targets.Values.ToList();
            return report;
        }

        static void CollectLayer(Report report, LayerRef layer, AnimatorStateMachine root, AnimatorControllerLayer syncedLayer,
            Dictionary<AnimationClip, List<string>> bindingCache, List<(string, DriverRef)> drivers)
        {
            var paths = AnalysisUtil.StatesWithPaths(root, "").ToDictionary(s => s.state, s => s.path);
            var stateParameters = paths.Keys.ToDictionary(s => s, s => new Dictionary<string, string>());

            void AddConditions(AnimatorTransitionBase transition, string via)
            {
                if (transition == null || transition.isExit) return;
                IEnumerable<AnimatorState> targets = transition.destinationState != null
                    ? new[] { transition.destinationState }
                    : transition.destinationStateMachine != null
                        ? AvatarToolsUtil.AllStates(transition.destinationStateMachine)
                        : Enumerable.Empty<AnimatorState>();
                foreach (var target in targets)
                {
                    if (!stateParameters.TryGetValue(target, out var dict)) continue;
                    foreach (var c in transition.conditions)
                        if (!string.IsNullOrEmpty(c.parameter) && !dict.ContainsKey(c.parameter)) dict[c.parameter] = via;
                }
            }

            foreach (var machine in AnalysisUtil.AllMachines(root))
            {
                foreach (var t in machine.anyStateTransitions) AddConditions(t, "Any State");
                foreach (var t in machine.entryTransitions) AddConditions(t, "Entry");
                foreach (var child in machine.stateMachines)
                foreach (var t in machine.GetStateMachineTransitions(child.stateMachine))
                    AddConditions(t, "From " + child.stateMachine.name);
                foreach (var child in machine.states)
                foreach (var t in child.state.transitions)
                    AddConditions(t, "From " + child.state.name);

                foreach (var behaviour in machine.behaviours)
                    if (behaviour is VRC_AvatarParameterDriver driver)
                        AddDrivers(drivers, driver, layer, "(state machine " + machine.name + ")", null);
            }

            foreach (var pair in paths)
            {
                var state = pair.Key;
                var dict = stateParameters[state];
                if (state.speedParameterActive) AddParameter(dict, state.speedParameter, "Speed");
                if (state.timeParameterActive) AddParameter(dict, state.timeParameter, "Motion time");
                if (state.mirrorParameterActive) AddParameter(dict, state.mirrorParameter, "Mirror");
                if (state.cycleOffsetParameterActive) AddParameter(dict, state.cycleOffsetParameter, "Cycle offset");
                foreach (var behaviour in state.behaviours)
                    if (behaviour is VRC_AvatarParameterDriver driver)
                        AddDrivers(drivers, driver, layer, pair.Value, state);

                var motion = syncedLayer != null ? syncedLayer.GetOverrideMotion(state) : state.motion;
                WalkMotion(motion, dict, (clip, parameters) => report.Impacts.Add(new StateImpact
                {
                    Layer = layer, StatePath = pair.Value, State = state, Clip = clip, Parameters = parameters,
                    Properties = ClipProperties(clip, bindingCache)
                }));
            }
        }

        static void AddParameter(Dictionary<string, string> dict, string name, string via)
        {
            if (!string.IsNullOrEmpty(name) && !dict.ContainsKey(name)) dict[name] = via;
        }

        static void WalkMotion(Motion motion, Dictionary<string, string> inherited, Action<AnimationClip, Dictionary<string, string>> emit)
        {
            if (motion is AnimationClip clip)
            {
                emit(clip, new Dictionary<string, string>(inherited));
                return;
            }
            if (!(motion is BlendTree tree)) return;
            if (tree.blendType == BlendTreeType.Direct)
            {
                foreach (var child in tree.children)
                {
                    var dict = new Dictionary<string, string>(inherited);
                    AddParameter(dict, child.directBlendParameter, "Direct blend tree");
                    WalkMotion(child.motion, dict, emit);
                }
                return;
            }
            var shared = new Dictionary<string, string>(inherited);
            AddParameter(shared, tree.blendParameter, "Blend tree");
            if (tree.blendType != BlendTreeType.Simple1D) AddParameter(shared, tree.blendParameterY, "Blend tree");
            foreach (var child in tree.children) WalkMotion(child.motion, shared, emit);
        }

        static void AddDrivers(List<(string, DriverRef)> drivers, VRC_AvatarParameterDriver driver, LayerRef layer, string path, AnimatorState state)
        {
            foreach (var p in driver.parameters)
            {
                if (string.IsNullOrEmpty(p.name)) continue;
                string description;
                switch (p.type)
                {
                    case VRC_AvatarParameterDriver.ChangeType.Add: description = "Add " + p.value; break;
                    case VRC_AvatarParameterDriver.ChangeType.Random: description = $"Random {p.valueMin}..{p.valueMax} (chance {p.chance})"; break;
                    case VRC_AvatarParameterDriver.ChangeType.Copy: description = "Copy from " + p.source; break;
                    default: description = "Set " + p.value; break;
                }
                if (driver.localOnly) description += ", local only";
                drivers.Add((p.name, new DriverRef { Layer = layer, StatePath = path, State = state, Description = description }));
            }
        }

        static List<string> ClipProperties(AnimationClip clip, Dictionary<AnimationClip, List<string>> cache)
        {
            if (cache.TryGetValue(clip, out var cached)) return cached;
            var result = AnimationUtility.GetCurveBindings(clip).Concat(AnimationUtility.GetObjectReferenceCurveBindings(clip))
                .Select(PropertyLabel).Distinct().OrderBy(s => s, StringComparer.Ordinal).ToList();
            cache[clip] = result;
            return result;
        }

        public static string PropertyLabel(EditorCurveBinding binding)
        {
            var path = string.IsNullOrEmpty(binding.path) ? "(root)" : binding.path;
            var typeName = binding.type != null ? binding.type.Name : "?";
            var property = binding.propertyName;
            if (binding.type == typeof(GameObject) && property == "m_IsActive") property = "active";
            else if (property == "m_Enabled") property = "enabled";
            else if (property.StartsWith("m_Materials.Array.data[", StringComparison.Ordinal))
                property = "material slot " + property.Substring("m_Materials.Array.data[".Length).TrimEnd(']');
            else if (!property.StartsWith("blendShape.", StringComparison.Ordinal))
            {
                var dot = property.LastIndexOf('.');
                if (dot > 0 && dot == property.Length - 2 && ComponentSuffixes.IndexOf(property[dot + 1]) >= 0)
                    property = property.Substring(0, dot);
            }
            return $"{path} : {typeName}.{property}";
        }

        static int PlayableRank(AnimLayerType type)
        {
            switch (type)
            {
                case AnimLayerType.Base: return 0;
                case AnimLayerType.Additive: return 1;
                case AnimLayerType.Gesture: return 2;
                case AnimLayerType.Action: return 3;
                case AnimLayerType.FX: return 4;
                default: return -1;
            }
        }

        static void EvaluateConflicts(TargetInfo target)
        {
            var layers = target.Touches.Select(t => t.Layer).GroupBy(l => (l.Controller, l.LayerIndex)).Select(g => g.First()).ToList();
            target.LayerConflict = layers.Count > 1;
            if (target.LayerConflict)
                target.Winner = layers.OrderByDescending(l => PlayableRank(l.Playable)).ThenByDescending(l => l.LayerIndex).First();

            // Different states of one toggle share a parameter; only flag when no parameter is common to every driving state.
            var parameterSets = target.Touches
                .Select(t => new HashSet<string>(t.Parameters.Keys.Where(n => !AnalysisUtil.BuiltInParameters.Contains(n))))
                .Where(s => s.Count > 0).ToList();
            if (parameterSets.Count < 2) return;
            var common = new HashSet<string>(parameterSets[0]);
            foreach (var set in parameterSets.Skip(1)) common.IntersectWith(set);
            target.ParameterConflict = common.Count == 0;
        }

        public static string ToText(Report report, string avatarName)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"Animation Impact Report: {avatarName}");
            sb.AppendLine();
            sb.AppendLine("== By Parameter ==");
            foreach (var p in report.Parameters)
            {
                if (p.Impacts.Count == 0 && p.DrivenBy.Count == 0) continue;
                sb.AppendLine($"{p.Name} [{p.Source}]");
                foreach (var d in p.DrivenBy) sb.AppendLine($"  driven by {d.Layer.Label} / {d.StatePath}: {d.Description}");
                foreach (var impact in p.Impacts)
                {
                    sb.AppendLine($"  {impact.Layer.Label} / {impact.StatePath} ({impact.Parameters[p.Name]}) clip {impact.Clip.name}");
                    foreach (var property in impact.Properties) sb.AppendLine("    " + property);
                }
            }
            sb.AppendLine();
            sb.AppendLine("== By Target ==");
            foreach (var t in report.Targets)
            {
                sb.AppendLine(t.Property + ConflictSuffix(t));
                foreach (var touch in t.Touches)
                    sb.AppendLine($"  {touch.Layer.Label} / {touch.StatePath} [{touch.Clip.name}] <- {ParameterList(touch)}");
            }
            return sb.ToString();
        }

        static string ConflictSuffix(TargetInfo t)
        {
            if (!t.Conflict) return "";
            var parts = new List<string>();
            if (t.LayerConflict) parts.Add($"multiple layers, {t.Winner.Label} wins");
            if (t.ParameterConflict) parts.Add("multiple parameters");
            return "  CONFLICT: " + string.Join("; ", parts);
        }

        static string ParameterList(StateImpact impact)
        {
            return impact.Parameters.Count == 0 ? "(no parameter)" : string.Join(", ", impact.Parameters.Keys);
        }

        static void PingState(AnimatorController controller, AnimatorState state)
        {
            Selection.activeObject = controller;
            EditorGUIUtility.PingObject(controller);
            EditorApplication.ExecuteMenuItem("Window/Animation/Animator");
            if (state != null) EditorApplication.delayCall += () => Selection.activeObject = state;
        }

        static void PingClip(AnimationClip clip)
        {
            Selection.activeObject = clip;
            EditorGUIUtility.PingObject(clip);
        }

        bool Matches(params string[] texts)
        {
            return string.IsNullOrEmpty(search) || texts.Any(t => t != null && t.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        bool Matches(ParameterInfo p)
        {
            return Matches(p.Name) || p.Impacts.Any(i => Matches(i.StatePath, i.Layer.LayerName, i.Clip.name) || i.Properties.Any(x => Matches(x)))
                                   || p.DrivenBy.Any(d => Matches(d.StatePath, d.Layer.LayerName));
        }

        bool Matches(TargetInfo t)
        {
            return Matches(t.Property) || t.Touches.Any(i => Matches(i.StatePath, i.Layer.LayerName) || i.Parameters.Keys.Any(k => Matches(k)));
        }

        bool Foldout(string key, string label, GUIStyle style = null)
        {
            var open = expanded.Contains(key);
            var now = EditorGUILayout.Foldout(open, label, true, style ?? EditorStyles.foldout);
            if (now != open)
            {
                if (now) expanded.Add(key);
                else expanded.Remove(key);
            }
            return now;
        }

        void OnGUI()
        {
            EditorGUI.BeginChangeCheck();
            avatar = AvatarToolsUtil.AvatarField(avatar);
            if (EditorGUI.EndChangeCheck())
            {
                expanded.Clear();
                Refresh();
            }
            if (avatar == null)
            {
                EditorGUILayout.HelpBox("Select an avatar with a VRC Avatar Descriptor.", MessageType.Info);
                return;
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Analyze", GUILayout.Height(24))) Refresh();
                using (new EditorGUI.DisabledScope(report == null))
                    if (GUILayout.Button("Export as text", GUILayout.Width(110), GUILayout.Height(24)))
                    {
                        EditorGUIUtility.systemCopyBuffer = ToText(report, avatar.gameObject.name);
                        ShowNotification(new GUIContent("Copied to clipboard"));
                    }
            }
            if (report == null) return;
            if (report.Impacts.Count == 0)
            {
                EditorGUILayout.HelpBox("No animation clips found in this avatar's playable layers.", MessageType.Info);
                return;
            }

            view = GUILayout.Toolbar(view, Views);
            using (new EditorGUILayout.HorizontalScope())
            {
                search = EditorGUILayout.TextField("Search", search);
                if (view == 1) conflictsOnly = GUILayout.Toggle(conflictsOnly, "Conflicts only", GUILayout.ExpandWidth(false));
            }
            var conflictCount = report.Targets.Count(t => t.Conflict);
            if (conflictCount > 0)
                EditorGUILayout.HelpBox($"{conflictCount} propert{(conflictCount == 1 ? "y is" : "ies are")} animated by more than one layer or unrelated parameters. " +
                                        "Within a controller the lowest layer in the list wins; FX is applied after the other playable layers.", MessageType.Warning);

            scroll = EditorGUILayout.BeginScrollView(scroll);
            if (view == 0) DrawByParameter();
            else DrawByTarget();
            EditorGUILayout.EndScrollView();
        }

        void DrawByParameter()
        {
            foreach (var p in report.Parameters)
            {
                if (!Matches(p)) continue;
                var properties = p.Impacts.SelectMany(i => i.Properties).Distinct().Count();
                var label = $"{p.Name}   [{p.Source}]   {p.Impacts.Select(i => i.State).Distinct().Count()} state(s), {properties} propert{(properties == 1 ? "y" : "ies")}";
                if (p.DrivenBy.Count > 0) label += $", driven by {p.DrivenBy.Count}";
                if (!Foldout("p:" + p.Name, label)) continue;
                using (new EditorGUI.IndentLevelScope())
                {
                    if (p.Impacts.Count == 0 && p.DrivenBy.Count == 0)
                        EditorGUILayout.LabelField("No animated properties or drivers found.", EditorStyles.miniLabel);
                    foreach (var d in p.DrivenBy)
                    {
                        using (new EditorGUILayout.HorizontalScope())
                        {
                            EditorGUILayout.LabelField($"Driven by {d.Layer.Label} / {d.StatePath}: {d.Description}");
                            if (GUILayout.Button("State", EditorStyles.miniButton, GUILayout.Width(50))) PingState(d.Layer.Controller, d.State);
                        }
                    }
                    foreach (var group in p.Impacts.GroupBy(i => i.Layer.Label))
                    {
                        EditorGUILayout.LabelField(group.Key, EditorStyles.boldLabel);
                        using (new EditorGUI.IndentLevelScope())
                            foreach (var impact in group) DrawImpact("p:" + p.Name, impact, impact.Parameters[p.Name]);
                    }
                }
            }
        }

        void DrawImpact(string parentKey, StateImpact impact, string via)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                var open = Foldout($"{parentKey}|{impact.Layer.Controller.GetInstanceID()}|{impact.Layer.LayerIndex}|{impact.StatePath}|{impact.Clip.GetInstanceID()}",
                    $"{impact.StatePath}   ({via})   {impact.Clip.name}, {impact.Properties.Count} propert{(impact.Properties.Count == 1 ? "y" : "ies")}");
                if (GUILayout.Button("State", EditorStyles.miniButtonLeft, GUILayout.Width(50))) PingState(impact.Layer.Controller, impact.State);
                if (GUILayout.Button("Clip", EditorStyles.miniButtonRight, GUILayout.Width(40))) PingClip(impact.Clip);
                if (!open) return;
            }
            using (new EditorGUI.IndentLevelScope())
                foreach (var property in impact.Properties)
                    EditorGUILayout.LabelField(property, EditorStyles.miniLabel);
        }

        void DrawByTarget()
        {
            foreach (var t in report.Targets)
            {
                if (conflictsOnly && !t.Conflict) continue;
                if (!Matches(t)) continue;
                var label = t.Property + (t.Conflict ? "   CONFLICT" : "");
                if (!Foldout("t:" + t.Property, label, t.Conflict ? ConflictFoldout : null)) continue;
                using (new EditorGUI.IndentLevelScope())
                {
                    if (t.LayerConflict) EditorGUILayout.LabelField($"Animated by multiple layers; {t.Winner.Label} wins.", EditorStyles.miniBoldLabel);
                    if (t.ParameterConflict) EditorGUILayout.LabelField("Animated by states driven by unrelated parameters.", EditorStyles.miniBoldLabel);
                    foreach (var touch in t.Touches)
                    {
                        using (new EditorGUILayout.HorizontalScope())
                        {
                            EditorGUILayout.LabelField($"{touch.Layer.Label} / {touch.StatePath}  [{touch.Clip.name}]   <-   {ParameterList(touch)}");
                            if (GUILayout.Button("State", EditorStyles.miniButtonLeft, GUILayout.Width(50))) PingState(touch.Layer.Controller, touch.State);
                            if (GUILayout.Button("Clip", EditorStyles.miniButtonRight, GUILayout.Width(40))) PingClip(touch.Clip);
                        }
                    }
                }
            }
        }

        static GUIStyle conflictFoldout;

        static GUIStyle ConflictFoldout
        {
            get
            {
                if (conflictFoldout != null) return conflictFoldout;
                var color = new Color(1f, 0.6f, 0.25f);
                conflictFoldout = new GUIStyle(EditorStyles.foldout) { fontStyle = FontStyle.Bold };
                conflictFoldout.normal.textColor = conflictFoldout.onNormal.textColor = color;
                conflictFoldout.focused.textColor = conflictFoldout.onFocused.textColor = color;
                return conflictFoldout;
            }
        }
    }
}
