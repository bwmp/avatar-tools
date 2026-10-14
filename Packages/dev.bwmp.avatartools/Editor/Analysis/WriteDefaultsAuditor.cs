using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using AnimLayerType = VRC.SDK3.Avatars.Components.VRCAvatarDescriptor.AnimLayerType;

namespace AvatarTools.Editor
{
    public class WriteDefaultsAuditor : EditorWindow
    {
        public class StateEntry
        {
            public AnimatorController Controller;
            public AnimLayerType PlayableLayer;
            public string LayerName;
            public string Path;
            public AnimatorState State;
            public bool ExemptLayer;
            public bool DirectBlendTree;
            public bool WriteDefaults => State.writeDefaultValues;
        }

        public class ControllerSummary
        {
            public AnimatorController Controller;
            public AnimLayerType PlayableLayer;
            public int On;
            public int Off;
            public bool Mixed => On > 0 && Off > 0;
            public bool Majority => On >= Off;
        }

        VRCAvatarDescriptor avatar;
        List<StateEntry> entries;
        List<ControllerSummary> summaries;
        bool skipExemptLayers = true;
        bool skipDirectBlendTrees = true;
        bool onlyInconsistent;
        Vector2 scroll;

        [MenuItem(AvatarToolsUtil.MenuRoot + "Animator/Write Defaults Auditor")]
        static void Open() => GetWindow<WriteDefaultsAuditor>("Write Defaults");

        void OnEnable()
        {
            avatar = avatar ? avatar : AvatarToolsUtil.SelectedAvatar();
            Undo.undoRedoPerformed += Refresh;
            Refresh();
        }

        void OnDisable() => Undo.undoRedoPerformed -= Refresh;

        void OnFocus() => Refresh();

        public static List<StateEntry> Collect(VRCAvatarDescriptor avatar)
        {
            var result = new List<StateEntry>();
            foreach (var (type, controller) in AnalysisUtil.PlayableLayers(avatar))
            {
                var exemptPlayable = type == AnimLayerType.Base || type == AnimLayerType.Additive;
                foreach (var layer in controller.layers)
                {
                    if (layer.stateMachine == null || layer.syncedLayerIndex >= 0) continue;
                    foreach (var (state, path) in AnalysisUtil.StatesWithPaths(layer.stateMachine, ""))
                    {
                        result.Add(new StateEntry
                        {
                            Controller = controller, PlayableLayer = type, LayerName = layer.name, Path = path, State = state,
                            ExemptLayer = exemptPlayable || layer.blendingMode == AnimatorLayerBlendingMode.Additive,
                            DirectBlendTree = state.motion is BlendTree tree && tree.blendType == BlendTreeType.Direct
                        });
                    }
                }
            }
            return result;
        }

        public static List<ControllerSummary> Summarize(List<StateEntry> entries)
        {
            return entries.GroupBy(e => e.Controller).Select(g => new ControllerSummary
            {
                Controller = g.Key, PlayableLayer = g.First().PlayableLayer,
                On = g.Count(e => !e.ExemptLayer && !e.DirectBlendTree && e.WriteDefaults),
                Off = g.Count(e => !e.ExemptLayer && !e.DirectBlendTree && !e.WriteDefaults)
            }).ToList();
        }

        public static int SetAll(VRCAvatarDescriptor avatar, bool on, bool skipExemptLayers, bool skipDirectBlendTrees)
        {
            var changed = 0;
            foreach (var e in Collect(avatar))
            {
                if (e.WriteDefaults == on || skipExemptLayers && e.ExemptLayer || skipDirectBlendTrees && e.DirectBlendTree) continue;
                Undo.RecordObject(e.State, on ? "Write Defaults On" : "Write Defaults Off");
                e.State.writeDefaultValues = on;
                EditorUtility.SetDirty(e.State);
                changed++;
            }
            AssetDatabase.SaveAssets();
            return changed;
        }

        void Refresh()
        {
            entries = avatar != null ? Collect(avatar) : null;
            summaries = entries != null ? Summarize(entries) : null;
            Repaint();
        }

        bool Inconsistent(StateEntry e)
        {
            if (e.ExemptLayer || e.DirectBlendTree) return false;
            var summary = summaries.First(s => s.Controller == e.Controller);
            return summary.Mixed && e.WriteDefaults != summary.Majority;
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
            if (entries == null || entries.Count == 0)
            {
                EditorGUILayout.HelpBox("No states found in custom playable layer controllers.", MessageType.Info);
                return;
            }

            foreach (var s in summaries)
            {
                var text = $"{s.PlayableLayer}: {s.Controller.name} - {s.On} ON, {s.Off} OFF";
                EditorGUILayout.HelpBox(s.Mixed ? text + " (mixed)" : text, s.Mixed ? MessageType.Warning : MessageType.None);
            }
            var global = summaries.Sum(s => s.On) > 0 && summaries.Sum(s => s.Off) > 0;
            if (global && !summaries.Any(s => s.Mixed))
                EditorGUILayout.HelpBox("Controllers disagree with each other on Write Defaults.", MessageType.Warning);

            onlyInconsistent = EditorGUILayout.ToggleLeft("Show only inconsistent states", onlyInconsistent);
            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                GUILayout.Label("Controller", EditorStyles.miniBoldLabel, GUILayout.Width(150));
                GUILayout.Label("Layer", EditorStyles.miniBoldLabel, GUILayout.Width(120));
                GUILayout.Label("State", EditorStyles.miniBoldLabel, GUILayout.MinWidth(150));
                GUILayout.Label("WD", EditorStyles.miniBoldLabel, GUILayout.Width(30));
                GUILayout.Label("Motion", EditorStyles.miniBoldLabel, GUILayout.Width(170));
            }
            scroll = EditorGUILayout.BeginScrollView(scroll);
            var warning = new GUIStyle(EditorStyles.label) { normal = { textColor = new Color(0.95f, 0.6f, 0.2f) } };
            foreach (var e in entries)
            {
                var inconsistent = Inconsistent(e);
                if (onlyInconsistent && !inconsistent) continue;
                var style = inconsistent ? warning : EditorStyles.label;
                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.Label($"{e.PlayableLayer}/{e.Controller.name}", style, GUILayout.Width(150));
                    GUILayout.Label(e.LayerName, style, GUILayout.Width(120));
                    var note = e.DirectBlendTree ? " (direct BT)" : e.ExemptLayer ? " (base/additive)" : "";
                    GUILayout.Label(e.Path + note + (inconsistent ? " - inconsistent" : ""), style, GUILayout.MinWidth(150));
                    var wd = EditorGUILayout.Toggle(e.WriteDefaults, GUILayout.Width(30));
                    if (wd != e.WriteDefaults)
                    {
                        Undo.RecordObject(e.State, "Toggle Write Defaults");
                        e.State.writeDefaultValues = wd;
                        EditorUtility.SetDirty(e.State);
                        summaries = Summarize(entries);
                    }
                    EditorGUILayout.ObjectField(e.State.motion, typeof(Motion), false, GUILayout.Width(170));
                }
            }
            EditorGUILayout.EndScrollView();

            skipExemptLayers = EditorGUILayout.ToggleLeft("Skip Base/Additive playable layers and additive-blended layers", skipExemptLayers);
            skipDirectBlendTrees = EditorGUILayout.ToggleLeft("Skip direct blend tree states (conventionally WD on)", skipDirectBlendTrees);
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Refresh", GUILayout.Width(80))) Refresh();
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("Set All ON", GUILayout.Height(24), GUILayout.Width(120))) Apply(true);
                if (GUILayout.Button("Set All OFF", GUILayout.Height(24), GUILayout.Width(120))) Apply(false);
            }
        }

        void Apply(bool on)
        {
            var affected = entries.Count(e => e.WriteDefaults != on && !(skipExemptLayers && e.ExemptLayer) && !(skipDirectBlendTrees && e.DirectBlendTree));
            if (affected == 0)
            {
                EditorUtility.DisplayDialog("Write Defaults", $"All applicable states already have Write Defaults {(on ? "ON" : "OFF")}.", "OK");
                return;
            }
            if (!EditorUtility.DisplayDialog("Write Defaults", $"Set Write Defaults {(on ? "ON" : "OFF")} on {affected} state(s)?", "Apply", "Cancel")) return;
            SetAll(avatar, on, skipExemptLayers, skipDirectBlendTrees);
            Refresh();
            GUIUtility.ExitGUI();
        }
    }
}
