using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using VRC.Dynamics;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Dynamics.PhysBone.Components;
using VRC.SDKBase.Validation.Performance;
using VRC.SDKBase.Validation.Performance.Stats;
using AdvancedBool = VRC.Dynamics.VRCPhysBoneBase.AdvancedBool;

namespace AvatarTools.Editor
{
    public enum PhysBoneField { Pull, Spring, Stiffness, Gravity, Radius, Immobile, AllowGrabbing, AllowPosing }

    public struct PhysBoneTotals
    {
        public int components, transforms, colliders, checks;
    }

    public sealed class PhysBoneOverview : EditorWindow
    {
        enum Column { Object, Root, Transforms, Colliders, Checks, Pull, Spring, Stiffness, Gravity, Radius, Immobile, Grab, Pose }

        struct Row
        {
            public VRCPhysBone bone;
            public int transforms, colliders, checks;
        }

        static readonly Dictionary<Column, float> Widths = new Dictionary<Column, float>
        {
            { Column.Object, 150 }, { Column.Root, 130 }, { Column.Transforms, 75 }, { Column.Colliders, 65 }, { Column.Checks, 55 },
            { Column.Pull, 55 }, { Column.Spring, 55 }, { Column.Stiffness, 60 }, { Column.Gravity, 55 }, { Column.Radius, 55 },
            { Column.Immobile, 60 }, { Column.Grab, 60 }, { Column.Pose, 60 },
        };

        static readonly Dictionary<Column, string> Tooltips = new Dictionary<Column, string>
        {
            { Column.Transforms, "Transforms simulated by this component (ignored subtrees excluded)." },
            { Column.Checks, "Collision checks: simulated segments x assigned colliders." },
            { Column.Spring, "Momentum in Simplified integration, Spring in Advanced." },
            { Column.Stiffness, "Only used by Advanced integration." },
        };

        VRCAvatarDescriptor avatar;
        Vector2 scroll;
        Column sortColumn = Column.Object;
        bool sortAscending = true;
        readonly HashSet<VRCPhysBone> selected = new HashSet<VRCPhysBone>();
        PhysBoneField bulkField;
        float bulkValue;
        AdvancedBool bulkFlag = AdvancedBool.True;

        [MenuItem(AvatarToolsUtil.MenuRoot + "Dynamics/PhysBone Overview")]
        static void Open()
        {
            GetWindow<PhysBoneOverview>("PhysBone Overview");
        }

        void OnEnable()
        {
            if (avatar == null) avatar = AvatarToolsUtil.SelectedAvatar();
        }

        void OnHierarchyChange()
        {
            Repaint();
        }

        public static Transform Root(VRCPhysBoneBase bone)
        {
            return bone.rootTransform != null ? bone.rootTransform : bone.transform;
        }

        public static HashSet<Transform> Roots(IEnumerable<VRCPhysBoneBase> bones)
        {
            return new HashSet<Transform>(bones.Select(Root));
        }

        // Matches the SDK performance scanner: a chain stops where another PhysBone's root begins, endpoints add
        // collision segments but not transforms, and at a branch only the first child segment is simulated
        // (none when Multi Child Type is Ignore).
        public static void Measure(VRCPhysBoneBase bone, ISet<Transform> allRoots, out int transforms, out int segments)
        {
            var root = Root(bone);
            var ignored = new HashSet<Transform>(bone.ignoreTransforms.Where(t => t != null));
            ignored.UnionWith(allRoots.Where(t => t != root));
            var hasEndpoint = bone.endpointPosition != Vector3.zero;
            transforms = 0;
            segments = 0;
            var stack = new Stack<Transform>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                var current = stack.Pop();
                transforms++;
                var children = current.Cast<Transform>().Where(c => !ignored.Contains(c)).ToList();
                if (children.Count == 0 && hasEndpoint) segments++;
                for (var i = 0; i < children.Count; i++)
                {
                    if (children.Count == 1 || (i == 0 && bone.multiChildType != VRCPhysBoneBase.MultiChildType.Ignore)) segments++;
                    stack.Push(children[i]);
                }
            }
        }

        public static PhysBoneTotals ComputeTotals(IReadOnlyCollection<VRCPhysBoneBase> bones)
        {
            var totals = new PhysBoneTotals();
            var colliders = new HashSet<VRCPhysBoneColliderBase>();
            var roots = Roots(bones);
            foreach (var bone in bones)
            {
                Measure(bone, roots, out var transforms, out var segments);
                var assigned = bone.colliders.Where(c => c != null).ToList();
                totals.components++;
                totals.transforms += transforms;
                totals.checks += segments * assigned.Count;
                colliders.UnionWith(assigned);
            }
            totals.colliders = colliders.Count;
            return totals;
        }

        public static PhysBoneTotals PoorLimits(bool mobile)
        {
            var stats = AvatarPerformanceStats.GetStatLevelForRating(PerformanceRating.Poor, mobile).physBone;
            return new PhysBoneTotals
            {
                components = stats.componentCount, transforms = stats.transformCount,
                colliders = stats.colliderCount, checks = stats.collisionCheckCount
            };
        }

        public static bool IsFlag(PhysBoneField field)
        {
            return field == PhysBoneField.AllowGrabbing || field == PhysBoneField.AllowPosing;
        }

        public static float GetValue(VRCPhysBoneBase bone, PhysBoneField field)
        {
            switch (field)
            {
                case PhysBoneField.Pull: return bone.pull;
                case PhysBoneField.Spring: return bone.spring;
                case PhysBoneField.Stiffness: return bone.stiffness;
                case PhysBoneField.Gravity: return bone.gravity;
                case PhysBoneField.Radius: return bone.radius;
                case PhysBoneField.Immobile: return bone.immobile;
                default: throw new ArgumentException(field + " is not a numeric field.");
            }
        }

        public static AdvancedBool GetFlag(VRCPhysBoneBase bone, PhysBoneField field)
        {
            switch (field)
            {
                case PhysBoneField.AllowGrabbing: return bone.allowGrabbing;
                case PhysBoneField.AllowPosing: return bone.allowPosing;
                default: throw new ArgumentException(field + " is not a flag field.");
            }
        }

        public static void SetValue(IReadOnlyCollection<VRCPhysBoneBase> bones, PhysBoneField field, float value)
        {
            if (bones.Count == 0) return;
            Undo.RecordObjects(bones.Cast<UnityEngine.Object>().ToArray(), "Set PhysBone " + field);
            foreach (var bone in bones)
            {
                switch (field)
                {
                    case PhysBoneField.Pull: bone.pull = Mathf.Clamp01(value); break;
                    case PhysBoneField.Spring: bone.spring = Mathf.Clamp01(value); break;
                    case PhysBoneField.Stiffness: bone.stiffness = Mathf.Clamp01(value); break;
                    case PhysBoneField.Gravity: bone.gravity = Mathf.Clamp(value, -1, 1); break;
                    case PhysBoneField.Radius: bone.radius = Mathf.Max(0, value); break;
                    case PhysBoneField.Immobile: bone.immobile = Mathf.Clamp01(value); break;
                    default: throw new ArgumentException(field + " is not a numeric field.");
                }
                MarkModified(bone);
            }
        }

        public static void SetFlag(IReadOnlyCollection<VRCPhysBoneBase> bones, PhysBoneField field, AdvancedBool value)
        {
            if (bones.Count == 0) return;
            Undo.RecordObjects(bones.Cast<UnityEngine.Object>().ToArray(), "Set PhysBone " + field);
            foreach (var bone in bones)
            {
                switch (field)
                {
                    case PhysBoneField.AllowGrabbing: bone.allowGrabbing = value; break;
                    case PhysBoneField.AllowPosing: bone.allowPosing = value; break;
                    default: throw new ArgumentException(field + " is not a flag field.");
                }
                MarkModified(bone);
            }
        }

        static void MarkModified(VRCPhysBoneBase bone)
        {
            PrefabUtility.RecordPrefabInstancePropertyModifications(bone);
            EditorUtility.SetDirty(bone);
        }

        void OnGUI()
        {
            avatar = AvatarToolsUtil.AvatarField(avatar);
            if (avatar == null)
            {
                EditorGUILayout.HelpBox("Select an avatar with a VRC Avatar Descriptor.", MessageType.Info);
                return;
            }

            var bones = avatar.GetComponentsInChildren<VRCPhysBone>(true);
            selected.RemoveWhere(b => b == null || !bones.Contains(b));
            if (bones.Length == 0)
            {
                EditorGUILayout.HelpBox("This avatar has no PhysBone components.", MessageType.Info);
                return;
            }

            var roots = Roots(bones);
            var rows = Sort(bones.Select(b =>
            {
                Measure(b, roots, out var transforms, out var segments);
                var colliders = b.colliders.Count(c => c != null);
                return new Row { bone = b, transforms = transforms, colliders = colliders, checks = segments * colliders };
            })).ToList();

            DrawTable(rows);
            EditorGUILayout.Space();
            DrawBulkEdit();
            EditorGUILayout.Space();
            DrawFooter(bones);
        }

        IEnumerable<Row> Sort(IEnumerable<Row> rows)
        {
            Func<Row, IComparable> key;
            switch (sortColumn)
            {
                case Column.Root: key = r => Root(r.bone).name; break;
                case Column.Transforms: key = r => r.transforms; break;
                case Column.Colliders: key = r => r.colliders; break;
                case Column.Checks: key = r => r.checks; break;
                case Column.Pull: key = r => r.bone.pull; break;
                case Column.Spring: key = r => r.bone.spring; break;
                case Column.Stiffness: key = r => r.bone.stiffness; break;
                case Column.Gravity: key = r => r.bone.gravity; break;
                case Column.Radius: key = r => r.bone.radius; break;
                case Column.Immobile: key = r => r.bone.immobile; break;
                case Column.Grab: key = r => r.bone.allowGrabbing; break;
                case Column.Pose: key = r => r.bone.allowPosing; break;
                default: key = r => r.bone.name; break;
            }
            return sortAscending ? rows.OrderBy(key) : rows.OrderByDescending(key);
        }

        void DrawTable(List<Row> rows)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Select All", EditorStyles.miniButtonLeft, GUILayout.Width(80)))
                    selected.UnionWith(rows.Select(r => r.bone));
                if (GUILayout.Button("Select None", EditorStyles.miniButtonRight, GUILayout.Width(80)))
                    selected.Clear();
                GUILayout.Label($"{selected.Count} of {rows.Count} selected", EditorStyles.miniLabel);
            }

            scroll = EditorGUILayout.BeginScrollView(scroll);
            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                GUILayout.Space(22);
                foreach (Column column in Enum.GetValues(typeof(Column)))
                {
                    var label = column + (column == sortColumn ? (sortAscending ? " ▲" : " ▼") : "");
                    Tooltips.TryGetValue(column, out var tooltip);
                    if (GUILayout.Button(new GUIContent(label, tooltip), EditorStyles.toolbarButton, GUILayout.Width(Widths[column])))
                    {
                        if (sortColumn == column) sortAscending = !sortAscending;
                        else
                        {
                            sortColumn = column;
                            sortAscending = true;
                        }
                    }
                }
                GUILayout.FlexibleSpace();
            }
            foreach (var row in rows) DrawRow(row);
            EditorGUILayout.EndScrollView();
        }

        void DrawRow(Row row)
        {
            var bone = row.bone;
            using (new EditorGUILayout.HorizontalScope())
            {
                var isSelected = selected.Contains(bone);
                if (EditorGUILayout.Toggle(isSelected, GUILayout.Width(18)) != isSelected)
                {
                    if (isSelected) selected.Remove(bone);
                    else selected.Add(bone);
                }
                if (GUILayout.Button(new GUIContent(bone.name, AvatarToolsUtil.GetPath(avatar.transform, bone.transform)),
                        EditorStyles.label, GUILayout.Width(Widths[Column.Object])))
                {
                    EditorGUIUtility.PingObject(bone);
                    Selection.activeObject = bone.gameObject;
                }
                var root = Root(bone);
                if (GUILayout.Button(new GUIContent(root.name, AvatarToolsUtil.GetPath(avatar.transform, root)),
                        EditorStyles.label, GUILayout.Width(Widths[Column.Root])))
                    EditorGUIUtility.PingObject(root);
                GUILayout.Label(row.transforms.ToString(), GUILayout.Width(Widths[Column.Transforms]));
                GUILayout.Label(row.colliders.ToString(), GUILayout.Width(Widths[Column.Colliders]));
                GUILayout.Label(row.checks.ToString(), GUILayout.Width(Widths[Column.Checks]));
                ValueCell(bone, PhysBoneField.Pull, Column.Pull);
                ValueCell(bone, PhysBoneField.Spring, Column.Spring);
                using (new EditorGUI.DisabledScope(bone.integrationType == VRCPhysBoneBase.IntegrationType.Simplified))
                    ValueCell(bone, PhysBoneField.Stiffness, Column.Stiffness);
                ValueCell(bone, PhysBoneField.Gravity, Column.Gravity);
                ValueCell(bone, PhysBoneField.Radius, Column.Radius);
                ValueCell(bone, PhysBoneField.Immobile, Column.Immobile);
                FlagCell(bone, PhysBoneField.AllowGrabbing, Column.Grab);
                FlagCell(bone, PhysBoneField.AllowPosing, Column.Pose);
                GUILayout.FlexibleSpace();
            }
        }

        static void ValueCell(VRCPhysBoneBase bone, PhysBoneField field, Column column)
        {
            EditorGUI.BeginChangeCheck();
            var value = EditorGUILayout.DelayedFloatField(GetValue(bone, field), GUILayout.Width(Widths[column]));
            if (EditorGUI.EndChangeCheck()) SetValue(new[] { bone }, field, value);
        }

        static void FlagCell(VRCPhysBoneBase bone, PhysBoneField field, Column column)
        {
            EditorGUI.BeginChangeCheck();
            var value = (AdvancedBool)EditorGUILayout.EnumPopup(GetFlag(bone, field), GUILayout.Width(Widths[column]));
            if (EditorGUI.EndChangeCheck()) SetFlag(new[] { bone }, field, value);
        }

        void DrawBulkEdit()
        {
            EditorGUILayout.LabelField("Bulk Edit Selected", EditorStyles.boldLabel);
            using (new EditorGUILayout.HorizontalScope())
            {
                bulkField = (PhysBoneField)EditorGUILayout.EnumPopup(bulkField, GUILayout.Width(120));
                if (IsFlag(bulkField)) bulkFlag = (AdvancedBool)EditorGUILayout.EnumPopup(bulkFlag, GUILayout.Width(80));
                else bulkValue = EditorGUILayout.FloatField(bulkValue, GUILayout.Width(80));
                using (new EditorGUI.DisabledScope(selected.Count == 0))
                {
                    if (GUILayout.Button($"Apply to {selected.Count} Selected", GUILayout.Width(160)))
                    {
                        var targets = selected.Cast<VRCPhysBoneBase>().ToList();
                        if (IsFlag(bulkField)) SetFlag(targets, bulkField, bulkFlag);
                        else SetValue(targets, bulkField, bulkValue);
                    }
                }
                GUILayout.FlexibleSpace();
            }
        }

        static void DrawFooter(IReadOnlyCollection<VRCPhysBoneBase> bones)
        {
            var totals = ComputeTotals(bones);
            var pc = PoorLimits(false);
            var quest = PoorLimits(true);
            EditorGUILayout.LabelField("Totals (Poor rating limits)", EditorStyles.boldLabel);
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Label("", GUILayout.Width(120));
                GUILayout.Label("Avatar", EditorStyles.miniBoldLabel, GUILayout.Width(60));
                GUILayout.Label("PC", EditorStyles.miniBoldLabel, GUILayout.Width(60));
                GUILayout.Label("Quest", EditorStyles.miniBoldLabel, GUILayout.Width(60));
            }
            FooterRow("Components", totals.components, pc.components, quest.components);
            FooterRow("Transforms", totals.transforms, pc.transforms, quest.transforms);
            FooterRow("Colliders", totals.colliders, pc.colliders, quest.colliders);
            FooterRow("Collision Checks", totals.checks, pc.checks, quest.checks);
        }

        static void FooterRow(string label, int value, int pcLimit, int questLimit)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Label(label, GUILayout.Width(120));
                GUILayout.Label(value.ToString(), EditorStyles.boldLabel, GUILayout.Width(60));
                GUILayout.Label(pcLimit.ToString(), GUILayout.Width(60));
                GUILayout.Label(questLimit.ToString(), GUILayout.Width(60));
                var status = value > pcLimit ? "Very Poor on PC and Quest" : value > questLimit ? "Very Poor on Quest" : "Within limits";
                GUILayout.Label(status, value > questLimit ? EditorStyles.boldLabel : EditorStyles.label);
            }
        }
    }
}
