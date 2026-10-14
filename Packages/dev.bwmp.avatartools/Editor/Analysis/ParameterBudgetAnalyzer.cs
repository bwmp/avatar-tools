using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Avatars.ScriptableObjects;

namespace AvatarTools.Editor
{
    public class ParameterBudgetAnalyzer : EditorWindow
    {
        public class ParameterRow
        {
            public int Index;
            public VRCExpressionParameters.Parameter Parameter;
            public int Cost;
            public bool InMenu;
            public bool InAnimator;
            public bool Reserved;
            public bool Duplicate;
            public bool Unused => !InMenu && !InAnimator && !Reserved;
            public bool AnimatorOnly => InAnimator && !InMenu && !Reserved && Parameter.networkSynced;
        }

        public class BudgetReport
        {
            public VRCExpressionParameters Asset;
            public List<ParameterRow> Rows = new List<ParameterRow>();
            public List<string> MissingFromAsset = new List<string>();
            public List<string> Duplicates = new List<string>();
            public int TotalCost;
        }

        VRCAvatarDescriptor avatar;
        BudgetReport report;
        readonly HashSet<int> selected = new HashSet<int>();
        Vector2 scroll;

        [MenuItem(AvatarToolsUtil.MenuRoot + "Expressions/Parameter Budget Analyzer")]
        static void Open() => GetWindow<ParameterBudgetAnalyzer>("Parameter Budget");

        void OnEnable()
        {
            avatar = avatar ? avatar : AvatarToolsUtil.SelectedAvatar();
            Undo.undoRedoPerformed += Refresh;
            Refresh();
        }

        void OnDisable() => Undo.undoRedoPerformed -= Refresh;

        void OnFocus() => Refresh();

        public static BudgetReport Analyze(VRCAvatarDescriptor avatar)
        {
            var report = new BudgetReport { Asset = avatar.expressionParameters };
            var menuNames = new HashSet<string>();
            foreach (var menu in AvatarToolsUtil.AllMenus(avatar.expressionsMenu))
            foreach (var control in menu.controls)
            {
                if (!string.IsNullOrEmpty(control.parameter?.name)) menuNames.Add(control.parameter.name);
                foreach (var sub in control.subParameters ?? new VRCExpressionsMenu.Control.Parameter[0])
                    if (!string.IsNullOrEmpty(sub?.name)) menuNames.Add(sub.name);
            }
            var animatorNames = new HashSet<string>(AnalysisUtil.ComponentParameters(avatar));
            foreach (var controller in AvatarToolsUtil.AllControllers(avatar))
                animatorNames.UnionWith(AnalysisUtil.ControllerReferences(controller));

            var parameters = report.Asset != null && report.Asset.parameters != null
                ? report.Asset.parameters
                : new VRCExpressionParameters.Parameter[0];
            var counts = parameters.Where(p => p != null).GroupBy(p => p.name).ToDictionary(g => g.Key ?? "", g => g.Count());
            for (var i = 0; i < parameters.Length; i++)
            {
                var p = parameters[i];
                if (p == null || string.IsNullOrEmpty(p.name)) continue;
                report.Rows.Add(new ParameterRow
                {
                    Index = i, Parameter = p,
                    Cost = p.networkSynced ? VRCExpressionParameters.TypeCost(p.valueType) : 0,
                    InMenu = menuNames.Contains(p.name),
                    InAnimator = animatorNames.Contains(p.name),
                    Reserved = AnalysisUtil.BuiltInParameters.Contains(p.name),
                    Duplicate = counts[p.name] > 1
                });
            }
            report.Duplicates = counts.Where(c => c.Value > 1 && c.Key != "").Select(c => c.Key).ToList();
            var assetNames = new HashSet<string>(parameters.Where(p => p != null).Select(p => p.name));
            report.MissingFromAsset = menuNames.Where(n => !assetNames.Contains(n)).OrderBy(n => n).ToList();
            report.TotalCost = report.Asset != null ? report.Asset.CalcTotalCost() : 0;
            return report;
        }

        public static void SetSynced(VRCExpressionParameters asset, int index, bool synced)
        {
            Undo.RecordObject(asset, synced ? "Sync Parameter" : "Unsync Parameter");
            asset.parameters[index].networkSynced = synced;
            EditorUtility.SetDirty(asset);
            AssetDatabase.SaveAssets();
        }

        public static int RemoveParameters(VRCExpressionParameters asset, ICollection<int> indices)
        {
            Undo.RecordObject(asset, "Remove Expression Parameters");
            var before = asset.parameters.Length;
            asset.parameters = asset.parameters.Where((p, i) => !indices.Contains(i)).ToArray();
            EditorUtility.SetDirty(asset);
            AssetDatabase.SaveAssets();
            return before - asset.parameters.Length;
        }

        void Refresh()
        {
            report = avatar != null ? Analyze(avatar) : null;
            selected.RemoveWhere(i => report == null || !report.Rows.Any(r => r.Index == i && r.Unused));
            Repaint();
        }

        void OnGUI()
        {
            EditorGUI.BeginChangeCheck();
            avatar = AvatarToolsUtil.AvatarField(avatar);
            if (EditorGUI.EndChangeCheck())
            {
                selected.Clear();
                Refresh();
            }
            if (avatar == null)
            {
                EditorGUILayout.HelpBox("Select an avatar with a VRC Avatar Descriptor.", MessageType.Info);
                return;
            }
            if (report == null || report.Asset == null)
            {
                EditorGUILayout.HelpBox("This avatar has no Expression Parameters asset.", MessageType.Warning);
                return;
            }

            var max = VRCExpressionParameters.MAX_PARAMETER_COST;
            var rect = EditorGUILayout.GetControlRect(false, 20);
            EditorGUI.ProgressBar(rect, Mathf.Clamp01(report.TotalCost / (float)max), $"{report.TotalCost} / {max} bits");
            if (report.TotalCost > max)
                EditorGUILayout.HelpBox($"Synced parameters exceed the budget by {report.TotalCost - max} bits.", MessageType.Error);
            foreach (var name in report.MissingFromAsset)
                EditorGUILayout.HelpBox($"Menu uses \"{name}\" but it is not in the parameters asset.", MessageType.Error);
            foreach (var name in report.Duplicates)
                EditorGUILayout.HelpBox($"\"{name}\" is defined more than once.", MessageType.Error);
            var unused = report.Rows.Count(r => r.Unused);
            if (unused > 0)
                EditorGUILayout.HelpBox($"{unused} parameter(s) are not used by any menu, controller, PhysBone or Contact.", MessageType.Warning);

            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                GUILayout.Space(22);
                GUILayout.Label("Name", EditorStyles.miniBoldLabel, GUILayout.MinWidth(140));
                GUILayout.Label("Type", EditorStyles.miniBoldLabel, GUILayout.Width(45));
                GUILayout.Label("Bits", EditorStyles.miniBoldLabel, GUILayout.Width(30));
                GUILayout.Label("Synced", EditorStyles.miniBoldLabel, GUILayout.Width(48));
                GUILayout.Label("Saved", EditorStyles.miniBoldLabel, GUILayout.Width(40));
                GUILayout.Label("Default", EditorStyles.miniBoldLabel, GUILayout.Width(50));
                GUILayout.Label("Status", EditorStyles.miniBoldLabel, GUILayout.MinWidth(160));
            }
            scroll = EditorGUILayout.BeginScrollView(scroll);
            foreach (var row in report.Rows)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    using (new EditorGUI.DisabledScope(!row.Unused))
                    {
                        var isSelected = EditorGUILayout.Toggle(selected.Contains(row.Index), GUILayout.Width(18));
                        if (isSelected) selected.Add(row.Index);
                        else selected.Remove(row.Index);
                    }
                    EditorGUILayout.SelectableLabel(row.Parameter.name, GUILayout.MinWidth(140), GUILayout.Height(EditorGUIUtility.singleLineHeight));
                    GUILayout.Label(row.Parameter.valueType.ToString(), GUILayout.Width(45));
                    GUILayout.Label(row.Cost.ToString(), GUILayout.Width(30));
                    var synced = EditorGUILayout.Toggle(row.Parameter.networkSynced, GUILayout.Width(48));
                    if (synced != row.Parameter.networkSynced)
                    {
                        SetSynced(report.Asset, row.Index, synced);
                        GUIUtility.ExitGUI();
                    }
                    GUILayout.Label(row.Parameter.saved ? "Yes" : "No", GUILayout.Width(40));
                    GUILayout.Label(row.Parameter.defaultValue.ToString("0.##"), GUILayout.Width(50));
                    GUILayout.Label(Status(row), GUILayout.MinWidth(160));
                }
            }
            EditorGUILayout.EndScrollView();

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Refresh", GUILayout.Width(80))) Refresh();
                GUILayout.FlexibleSpace();
                using (new EditorGUI.DisabledScope(selected.Count == 0))
                {
                    if (GUILayout.Button($"Remove Selected Unused ({selected.Count})", GUILayout.Height(24), GUILayout.Width(220)))
                    {
                        var names = string.Join(", ", report.Rows.Where(r => selected.Contains(r.Index)).Select(r => r.Parameter.name));
                        if (EditorUtility.DisplayDialog("Remove Parameters", $"Remove {selected.Count} parameter(s) from {report.Asset.name}?\n\n{names}", "Remove", "Cancel"))
                        {
                            RemoveParameters(report.Asset, selected.ToList());
                            selected.Clear();
                            Refresh();
                            GUIUtility.ExitGUI();
                        }
                    }
                }
            }
        }

        static string Status(ParameterRow row)
        {
            if (row.Duplicate) return "Duplicate";
            if (row.Unused) return "Unused";
            if (row.Reserved) return "VRChat built-in";
            if (row.AnimatorOnly) return "Animator-only (could be unsynced?)";
            return row.InMenu ? "Menu" + (row.InAnimator ? " + Animator" : "") : "Animator";
        }
    }
}
