using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using VRC.SDKBase;
using AnimLayerType = VRC.SDK3.Avatars.Components.VRCAvatarDescriptor.AnimLayerType;

namespace AvatarTools.Editor
{
    public class AnimatorCleaner : EditorWindow
    {
        public class Finding
        {
            public string Label;
            public string Name;
            public int Index;
            public Object Target;
            public bool Selected = true;
        }

        public class ControllerReport
        {
            public AnimatorController Controller;
            public AnimLayerType LayerType;
            public List<Finding> UnusedParameters = new List<Finding>();
            public List<Finding> EmptyLayers = new List<Finding>();
            public List<Finding> UnreachableStates = new List<Finding>();
            public List<Finding> OrphanedAssets = new List<Finding>();
            public IEnumerable<Finding> All => UnusedParameters.Concat(EmptyLayers).Concat(UnreachableStates).Concat(OrphanedAssets);
        }

        VRCAvatarDescriptor avatar;
        List<ControllerReport> reports;
        readonly Dictionary<string, bool> foldouts = new Dictionary<string, bool>();
        Vector2 scroll;

        [MenuItem(AvatarToolsUtil.MenuRoot + "Animator/Animator Cleaner")]
        static void Open() => GetWindow<AnimatorCleaner>("Animator Cleaner");

        void OnEnable()
        {
            avatar = avatar ? avatar : AvatarToolsUtil.SelectedAvatar();
            Undo.undoRedoPerformed += Refresh;
            Refresh();
        }

        void OnDisable() => Undo.undoRedoPerformed -= Refresh;

        public static List<ControllerReport> Analyze(VRCAvatarDescriptor avatar)
        {
            return AnalysisUtil.PlayableLayers(avatar).Select(l => Analyze(l.controller, l.type)).ToList();
        }

        public static ControllerReport Analyze(AnimatorController controller, AnimLayerType layerType)
        {
            var report = new ControllerReport { Controller = controller, LayerType = layerType };
            var references = AnalysisUtil.ControllerReferences(controller);
            foreach (var p in controller.parameters)
                if (!references.Contains(p.name) && !AnalysisUtil.BuiltInParameters.Contains(p.name))
                    report.UnusedParameters.Add(new Finding { Label = $"{p.name} ({p.type})", Name = p.name, Target = controller });

            var layers = controller.layers;
            for (var i = 1; i < layers.Length; i++)
            {
                var layer = layers[i];
                if (layer.syncedLayerIndex >= 0 || layers.Any(l => l.syncedLayerIndex == i)) continue;
                if (layer.stateMachine == null || !AvatarToolsUtil.AllStates(layer.stateMachine).Any())
                    report.EmptyLayers.Add(new Finding { Label = $"[{i}] {layer.name}", Name = layer.name, Index = i, Target = layer.stateMachine });
            }

            foreach (var layer in layers)
            {
                if (layer.stateMachine == null || layer.syncedLayerIndex >= 0) continue;
                var reachable = ReachableStates(layer.stateMachine);
                foreach (var (state, path) in AnalysisUtil.StatesWithPaths(layer.stateMachine, layer.name + "/"))
                    if (!reachable.Contains(state))
                        report.UnreachableStates.Add(new Finding { Label = path, Target = state });
            }

            var referenced = GraphObjects(controller);
            foreach (var obj in AssetDatabase.LoadAllAssetsAtPath(AssetDatabase.GetAssetPath(controller)))
            {
                if (obj == null || referenced.Contains(obj)) continue;
                if (obj is AnimatorState || obj is AnimatorStateMachine || obj is AnimatorTransitionBase || obj is BlendTree || obj is StateMachineBehaviour)
                    report.OrphanedAssets.Add(new Finding { Label = $"{obj.GetType().Name} \"{obj.name}\"", Target = obj });
            }
            return report;
        }

        public static HashSet<AnimatorState> ReachableStates(AnimatorStateMachine root)
        {
            var reachable = new HashSet<AnimatorState>();
            var owner = new Dictionary<AnimatorState, AnimatorStateMachine>();
            var parent = new Dictionary<AnimatorStateMachine, AnimatorStateMachine>();
            var machines = AnalysisUtil.AllMachines(root).ToList();
            foreach (var machine in machines)
            {
                foreach (var child in machine.states) owner[child.state] = machine;
                foreach (var sub in machine.stateMachines) parent[sub.stateMachine] = machine;
            }
            var entered = new HashSet<AnimatorStateMachine>();
            var queue = new Queue<AnimatorState>();

            void Enqueue(AnimatorState state)
            {
                if (state != null && reachable.Add(state)) queue.Enqueue(state);
            }

            void Enter(AnimatorStateMachine machine)
            {
                if (machine == null || !entered.Add(machine)) return;
                Enqueue(machine.defaultState);
                foreach (var t in machine.entryTransitions) Follow(t, machine);
            }

            void Follow(AnimatorTransitionBase t, AnimatorStateMachine from)
            {
                if (t == null) return;
                if (t.destinationState != null) Enqueue(t.destinationState);
                else if (t.destinationStateMachine != null) Enter(t.destinationStateMachine);
                else if (t.isExit) Exit(from);
            }

            void Exit(AnimatorStateMachine machine)
            {
                if (!parent.TryGetValue(machine, out var p)) return;
                var transitions = p.GetStateMachineTransitions(machine);
                if (transitions.Length == 0) Exit(p);
                foreach (var t in transitions) Follow(t, p);
            }

            Enter(root);
            foreach (var machine in machines)
            foreach (var t in machine.anyStateTransitions)
                Follow(t, machine);
            while (queue.Count > 0)
            {
                var state = queue.Dequeue();
                foreach (var t in state.transitions)
                    Follow(t, owner.TryGetValue(state, out var m) ? m : root);
            }
            return reachable;
        }

        static HashSet<Object> GraphObjects(AnimatorController controller)
        {
            var set = new HashSet<Object> { controller };
            void AddMotion(Motion motion)
            {
                if (motion == null || !set.Add(motion)) return;
                if (motion is BlendTree tree)
                    foreach (var child in tree.children) AddMotion(child.motion);
            }
            foreach (var layer in controller.layers)
            {
                if (layer.avatarMask != null) set.Add(layer.avatarMask);
                if (layer.stateMachine == null) continue;
                foreach (var machine in AnalysisUtil.AllMachines(layer.stateMachine))
                {
                    set.Add(machine);
                    foreach (var t in AnalysisUtil.MachineTransitions(machine)) set.Add(t);
                    foreach (var b in machine.behaviours) set.Add(b);
                    foreach (var child in machine.states)
                    {
                        set.Add(child.state);
                        foreach (var t in child.state.transitions) set.Add(t);
                        foreach (var b in child.state.behaviours) set.Add(b);
                        AddMotion(child.state.motion);
                        if (layer.syncedLayerIndex < 0) continue;
                        AddMotion(layer.GetOverrideMotion(child.state));
                        foreach (var b in layer.GetOverrideBehaviours(child.state)) set.Add(b);
                    }
                }
            }
            set.Remove(null);
            return set;
        }

        public static int Clean(VRCAvatarDescriptor avatar, ControllerReport report)
        {
            var controller = report.Controller;
            var count = 0;
            Undo.RecordObject(controller, "Clean Animator");

            foreach (var f in report.UnusedParameters.Where(f => f.Selected))
            {
                var index = System.Array.FindIndex(controller.parameters, p => p.name == f.Name);
                if (index < 0) continue;
                controller.RemoveParameter(index);
                count++;
            }

            foreach (var f in report.UnreachableStates.Where(f => f.Selected))
            {
                if (!(f.Target is AnimatorState state)) continue;
                var machine = controller.layers.Where(l => l.stateMachine != null)
                    .SelectMany(l => AnalysisUtil.AllMachines(l.stateMachine))
                    .FirstOrDefault(m => m.states.Any(s => s.state == state));
                if (machine == null) continue;
                foreach (var t in IncomingTransitions(controller, state))
                    RemoveTransition(controller, t);
                foreach (var t in state.transitions) Undo.DestroyObjectImmediate(t);
                foreach (var b in state.behaviours) Undo.DestroyObjectImmediate(b);
                if (state.motion is BlendTree tree && AssetDatabase.GetAssetPath(tree) == AssetDatabase.GetAssetPath(controller))
                    DestroyBlendTree(controller, tree);
                Undo.RecordObject(machine, "Remove State");
                machine.states = machine.states.Where(s => s.state != state).ToArray();
                Undo.DestroyObjectImmediate(state);
                count++;
            }

            var layerIndices = report.EmptyLayers
                .Where(f => f.Selected && f.Index > 0 && f.Index < controller.layers.Length && controller.layers[f.Index].name == f.Name)
                .Select(f => f.Index).Distinct().OrderByDescending(i => i).ToList();
            foreach (var index in layerIndices)
            {
                RemoveLayer(avatar, controller, report.LayerType, index);
                count++;
            }

            foreach (var f in report.OrphanedAssets.Where(f => f.Selected))
            {
                if (f.Target == null) continue;
                Undo.DestroyObjectImmediate(f.Target);
                count++;
            }

            EditorUtility.SetDirty(controller);
            AssetDatabase.SaveAssets();
            return count;
        }

        static IEnumerable<AnimatorTransitionBase> IncomingTransitions(AnimatorController controller, AnimatorState state)
        {
            foreach (var layer in controller.layers)
            {
                if (layer.stateMachine == null) continue;
                foreach (var machine in AnalysisUtil.AllMachines(layer.stateMachine))
                {
                    foreach (var t in AnalysisUtil.MachineTransitions(machine))
                        if (t.destinationState == state) yield return t;
                    foreach (var child in machine.states)
                    foreach (var t in child.state.transitions)
                        if (t.destinationState == state) yield return t;
                }
            }
        }

        static void RemoveTransition(AnimatorController controller, AnimatorTransitionBase transition)
        {
            foreach (var layer in controller.layers)
            {
                if (layer.stateMachine == null) continue;
                foreach (var machine in AnalysisUtil.AllMachines(layer.stateMachine))
                {
                    if (machine.anyStateTransitions.Contains(transition))
                    {
                        Undo.RecordObject(machine, "Remove Transition");
                        machine.anyStateTransitions = machine.anyStateTransitions.Where(t => t != transition).ToArray();
                    }
                    if (machine.entryTransitions.Contains(transition))
                    {
                        Undo.RecordObject(machine, "Remove Transition");
                        machine.entryTransitions = machine.entryTransitions.Where(t => t != transition).ToArray();
                    }
                    foreach (var sub in machine.stateMachines)
                    {
                        var smTransitions = machine.GetStateMachineTransitions(sub.stateMachine);
                        if (!smTransitions.Contains(transition)) continue;
                        Undo.RecordObject(machine, "Remove Transition");
                        machine.SetStateMachineTransitions(sub.stateMachine, smTransitions.Where(t => t != transition).ToArray());
                    }
                    foreach (var child in machine.states)
                    {
                        if (!child.state.transitions.Contains(transition)) continue;
                        Undo.RecordObject(child.state, "Remove Transition");
                        child.state.transitions = child.state.transitions.Where(t => t != transition).ToArray();
                    }
                }
            }
            Undo.DestroyObjectImmediate(transition);
        }

        static void DestroyBlendTree(AnimatorController controller, BlendTree tree)
        {
            foreach (var child in tree.children)
                if (child.motion is BlendTree nested && AssetDatabase.GetAssetPath(nested) == AssetDatabase.GetAssetPath(controller))
                    DestroyBlendTree(controller, nested);
            Undo.DestroyObjectImmediate(tree);
        }

        static void RemoveLayer(VRCAvatarDescriptor avatar, AnimatorController controller, AnimLayerType layerType, int index)
        {
            var layers = controller.layers.ToList();
            var machine = layers[index].stateMachine;
            layers.RemoveAt(index);
            foreach (var layer in layers)
                if (layer.syncedLayerIndex > index) layer.syncedLayerIndex--;
            controller.layers = layers.ToArray();
            if (machine != null)
            {
                foreach (var sub in AnalysisUtil.AllMachines(machine).Reverse().ToList())
                {
                    foreach (var t in AnalysisUtil.MachineTransitions(sub)) Undo.DestroyObjectImmediate(t);
                    foreach (var b in sub.behaviours) Undo.DestroyObjectImmediate(b);
                    if (sub != machine) Undo.DestroyObjectImmediate(sub);
                }
                Undo.DestroyObjectImmediate(machine);
            }
            if (!TryBlendableLayer(layerType, out var blendable)) return;
            foreach (var other in AvatarToolsUtil.AllControllers(avatar))
            foreach (var layer in other.layers.Where(l => l.stateMachine != null))
            foreach (var m in AnalysisUtil.AllMachines(layer.stateMachine))
            foreach (var b in m.behaviours.Concat(m.states.SelectMany(s => s.state.behaviours)))
            {
                if (!(b is VRC_AnimatorLayerControl control) || control.playable != blendable || control.layer <= index) continue;
                Undo.RecordObject(control, "Reindex Layer Control");
                control.layer--;
                EditorUtility.SetDirty(control);
            }
        }

        static bool TryBlendableLayer(AnimLayerType type, out VRC_AnimatorLayerControl.BlendableLayer layer)
        {
            switch (type)
            {
                case AnimLayerType.Action: layer = VRC_AnimatorLayerControl.BlendableLayer.Action; return true;
                case AnimLayerType.FX: layer = VRC_AnimatorLayerControl.BlendableLayer.FX; return true;
                case AnimLayerType.Gesture: layer = VRC_AnimatorLayerControl.BlendableLayer.Gesture; return true;
                case AnimLayerType.Additive: layer = VRC_AnimatorLayerControl.BlendableLayer.Additive; return true;
                default: layer = default; return false;
            }
        }

        void Refresh()
        {
            reports = avatar != null ? Analyze(avatar) : null;
            Repaint();
        }

        void OnGUI()
        {
            EditorGUI.BeginChangeCheck();
            avatar = AvatarToolsUtil.AvatarField(avatar);
            if (EditorGUI.EndChangeCheck()) Refresh();
            if (avatar == null)
            {
                EditorGUILayout.HelpBox("Select an avatar with a VRC Avatar Descriptor.", MessageType.Info);
                return;
            }
            if (reports == null || reports.Count == 0)
            {
                EditorGUILayout.HelpBox("This avatar has no custom playable layer controllers.", MessageType.Info);
                if (GUILayout.Button("Refresh")) Refresh();
                return;
            }
            EditorGUILayout.HelpBox("The first layer of each controller is never listed as empty because it defines the controller's base layer. Removing layers re-indexes VRC Animator Layer Control behaviours that target later layers.", MessageType.None);

            scroll = EditorGUILayout.BeginScrollView(scroll);
            foreach (var report in reports)
            {
                var total = report.All.Count();
                EditorGUILayout.Space(4);
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField($"{report.LayerType}: {report.Controller.name}", EditorStyles.boldLabel);
                    GUILayout.FlexibleSpace();
                    GUILayout.Label(total == 0 ? "Clean" : $"{total} finding(s)", EditorStyles.miniLabel);
                }
                Group(report, "Unused parameters", report.UnusedParameters);
                Group(report, "Empty layers", report.EmptyLayers);
                Group(report, "Unreachable states", report.UnreachableStates);
                Group(report, "Orphaned sub-assets", report.OrphanedAssets);
            }
            EditorGUILayout.EndScrollView();

            var selectedCount = reports.Sum(r => r.All.Count(f => f.Selected));
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Refresh", GUILayout.Width(80))) Refresh();
                GUILayout.FlexibleSpace();
                using (new EditorGUI.DisabledScope(selectedCount == 0))
                {
                    if (GUILayout.Button($"Clean Selected ({selectedCount})", GUILayout.Height(24), GUILayout.Width(180)))
                    {
                        var summary = string.Join("\n", reports.Where(r => r.All.Any(f => f.Selected)).Select(r =>
                            $"{r.Controller.name}: {r.UnusedParameters.Count(f => f.Selected)} parameters, {r.EmptyLayers.Count(f => f.Selected)} layers, " +
                            $"{r.UnreachableStates.Count(f => f.Selected)} states, {r.OrphanedAssets.Count(f => f.Selected)} orphaned assets"));
                        if (EditorUtility.DisplayDialog("Clean Animators", "Remove the selected items?\n\n" + summary, "Clean", "Cancel"))
                        {
                            foreach (var report in reports.Where(r => r.All.Any(f => f.Selected)))
                                Clean(avatar, report);
                            Refresh();
                            GUIUtility.ExitGUI();
                        }
                    }
                }
            }
        }

        void Group(ControllerReport report, string title, List<Finding> findings)
        {
            if (findings.Count == 0) return;
            var key = report.Controller.GetInstanceID() + title;
            foldouts.TryGetValue(key, out var open);
            using (new EditorGUILayout.HorizontalScope())
            {
                var all = findings.All(f => f.Selected);
                var toggled = EditorGUILayout.Toggle(all, GUILayout.Width(18));
                if (toggled != all) findings.ForEach(f => f.Selected = toggled);
                foldouts[key] = open = EditorGUILayout.Foldout(open, $"{title} ({findings.Count})", true);
            }
            if (!open) return;
            using (new EditorGUI.IndentLevelScope())
            {
                foreach (var f in findings)
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        GUILayout.Space(20);
                        f.Selected = EditorGUILayout.ToggleLeft(f.Label, f.Selected);
                    }
                }
            }
        }
    }
}
