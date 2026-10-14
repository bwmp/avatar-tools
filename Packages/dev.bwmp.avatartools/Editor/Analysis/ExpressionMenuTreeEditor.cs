using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.IMGUI.Controls;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Avatars.ScriptableObjects;
using ControlType = VRC.SDK3.Avatars.ScriptableObjects.VRCExpressionsMenu.Control.ControlType;
using Object = UnityEngine.Object;

namespace AvatarTools.Editor
{
    public class ExpressionMenuTreeEditor : EditorWindow
    {
        [SerializeField] TreeViewState treeState;
        [SerializeField] MultiColumnHeaderState headerState;
        VRCAvatarDescriptor avatar;
        MenuTree tree;
        Vector2 detailsScroll;

        [MenuItem(AvatarToolsUtil.MenuRoot + "Expressions/Menu Tree Editor")]
        static void Open() => GetWindow<ExpressionMenuTreeEditor>("Menu Tree");

        void OnEnable()
        {
            treeState = treeState ?? new TreeViewState();
            var fresh = CreateHeaderState();
            if (MultiColumnHeaderState.CanOverwriteSerializedFields(headerState, fresh))
                MultiColumnHeaderState.OverwriteSerializedFields(headerState, fresh);
            headerState = fresh;
            avatar = avatar ? avatar : AvatarToolsUtil.SelectedAvatar();
            Undo.undoRedoPerformed += Reload;
            Rebuild();
        }

        void OnDisable() => Undo.undoRedoPerformed -= Reload;

        void OnFocus() => Reload();

        static MultiColumnHeaderState CreateHeaderState()
        {
            MultiColumnHeaderState.Column Column(string title, float width) => new MultiColumnHeaderState.Column
            {
                headerContent = new GUIContent(title), width = width, minWidth = 30, autoResize = true,
                canSort = false, allowToggleVisibility = false
            };
            return new MultiColumnHeaderState(new[]
            {
                Column("Name", 200), Column("Type", 95), Column("Parameter", 140), Column("Value", 50),
                Column("Icon", 40), Column("Warning", 240)
            });
        }

        public static int SubParameterCount(ControlType type)
        {
            switch (type)
            {
                case ControlType.TwoAxisPuppet: return 2;
                case ControlType.FourAxisPuppet: return 4;
                case ControlType.RadialPuppet: return 1;
                default: return 0;
            }
        }

        static readonly string[][] SubParameterLabels =
        {
            new[] { "Horizontal", "Vertical" },
            new[] { "Up", "Right", "Down", "Left" },
            new[] { "Rotation" }
        };

        static string SubParameterLabel(ControlType type, int index)
        {
            var labels = type == ControlType.TwoAxisPuppet ? SubParameterLabels[0]
                : type == ControlType.FourAxisPuppet ? SubParameterLabels[1] : SubParameterLabels[2];
            return labels[index];
        }

        public static string Warning(VRCExpressionsMenu.Control control, VRCExpressionParameters parameters)
        {
            var issues = new List<string>();
            bool Missing(string name) => !string.IsNullOrEmpty(name) && (parameters == null || parameters.FindParameter(name) == null);
            if (Missing(control.parameter?.name)) issues.Add($"Missing parameter \"{control.parameter.name}\"");
            var subs = control.subParameters ?? Array.Empty<VRCExpressionsMenu.Control.Parameter>();
            for (var i = 0; i < Math.Min(subs.Length, SubParameterCount(control.type)); i++)
                if (Missing(subs[i]?.name)) issues.Add($"Missing parameter \"{subs[i].name}\"");
            if (control.type == ControlType.SubMenu)
            {
                if (control.subMenu == null) issues.Add("Submenu not set");
                else if (control.subMenu.controls.Count > VRCExpressionsMenu.MAX_CONTROLS)
                    issues.Add($"Submenu has {control.subMenu.controls.Count} controls (max {VRCExpressionsMenu.MAX_CONTROLS})");
            }
            return string.Join("; ", issues);
        }

        public static bool CanMove(VRCExpressionsMenu from, int fromIndex, VRCExpressionsMenu to)
        {
            if (from == null || to == null || fromIndex < 0 || fromIndex >= from.controls.Count) return false;
            var control = from.controls[fromIndex];
            if (control.type == ControlType.SubMenu && control.subMenu != null && AvatarToolsUtil.AllMenus(control.subMenu).Contains(to))
                return false;
            return from == to || to.controls.Count < VRCExpressionsMenu.MAX_CONTROLS;
        }

        public static int MoveControl(VRCExpressionsMenu from, int fromIndex, VRCExpressionsMenu to, int toIndex)
        {
            if (!CanMove(from, fromIndex, to)) return -1;
            Undo.RecordObjects(from == to ? new Object[] { from } : new Object[] { from, to }, "Move Menu Control");
            var control = from.controls[fromIndex];
            from.controls.RemoveAt(fromIndex);
            if (from == to && toIndex > fromIndex) toIndex--;
            toIndex = Mathf.Clamp(toIndex, 0, to.controls.Count);
            to.controls.Insert(toIndex, control);
            EditorUtility.SetDirty(from);
            EditorUtility.SetDirty(to);
            AssetDatabase.SaveAssets();
            return toIndex;
        }

        public static int AddControl(VRCExpressionsMenu menu)
        {
            if (menu == null || menu.controls.Count >= VRCExpressionsMenu.MAX_CONTROLS) return -1;
            Undo.RecordObject(menu, "Add Menu Control");
            menu.controls.Add(new VRCExpressionsMenu.Control
            {
                name = "New Control", type = ControlType.Toggle, value = 1,
                parameter = new VRCExpressionsMenu.Control.Parameter { name = "" },
                subParameters = Array.Empty<VRCExpressionsMenu.Control.Parameter>(),
                labels = Array.Empty<VRCExpressionsMenu.Control.Label>()
            });
            EditorUtility.SetDirty(menu);
            AssetDatabase.SaveAssets();
            return menu.controls.Count - 1;
        }

        public static void DeleteControl(VRCExpressionsMenu menu, int index)
        {
            Undo.RecordObject(menu, "Delete Menu Control");
            menu.controls.RemoveAt(index);
            EditorUtility.SetDirty(menu);
            AssetDatabase.SaveAssets();
        }

        public static void RenameControl(VRCExpressionsMenu menu, int index, string name)
        {
            Undo.RecordObject(menu, "Rename Menu Control");
            menu.controls[index].name = name;
            EditorUtility.SetDirty(menu);
            AssetDatabase.SaveAssets();
        }

        void Rebuild()
        {
            tree = new MenuTree(this, treeState, new MultiColumnHeader(headerState));
            Reload();
            if (treeState.expandedIDs.Count == 0) tree.ExpandAll();
        }

        void Reload()
        {
            if (tree == null) return;
            tree.Root = avatar != null ? avatar.expressionsMenu : null;
            tree.Parameters = avatar != null ? avatar.expressionParameters : null;
            tree.Reload();
            Repaint();
        }

        void OnGUI()
        {
            EditorGUI.BeginChangeCheck();
            avatar = AvatarToolsUtil.AvatarField(avatar);
            if (EditorGUI.EndChangeCheck())
            {
                treeState.selectedIDs.Clear();
                Reload();
                if (tree.Root != null) tree.ExpandAll();
            }
            if (avatar == null)
            {
                EditorGUILayout.HelpBox("Select an avatar with a VRC Avatar Descriptor.", MessageType.Info);
                return;
            }
            if (avatar.expressionsMenu == null)
            {
                EditorGUILayout.HelpBox("This avatar has no Expressions Menu.", MessageType.Warning);
                return;
            }
            if (tree.Root != avatar.expressionsMenu || tree.Parameters != avatar.expressionParameters) Reload();
            if (avatar.expressionParameters == null)
                EditorGUILayout.HelpBox("This avatar has no Expression Parameters asset; every parameter will be reported missing.", MessageType.Warning);

            using (new EditorGUILayout.HorizontalScope())
            {
                var rect = GUILayoutUtility.GetRect(0, 100000, 0, 100000);
                tree.OnGUI(rect);
                using (new EditorGUILayout.VerticalScope(GUILayout.Width(300)))
                {
                    detailsScroll = EditorGUILayout.BeginScrollView(detailsScroll);
                    DetailsGUI(tree.SelectedItem);
                    EditorGUILayout.EndScrollView();
                }
            }
        }

        static VRCExpressionsMenu TargetMenu(MenuTree.Item item)
        {
            if (item == null) return null;
            return item.Index < 0 || item.Control.type == ControlType.SubMenu ? item.ChildMenu : item.Menu;
        }

        void DetailsGUI(MenuTree.Item item)
        {
            var target = TargetMenu(item) ?? tree.Root;
            using (new EditorGUI.DisabledScope(target.controls.Count >= VRCExpressionsMenu.MAX_CONTROLS))
            {
                if (GUILayout.Button($"Add Control to \"{target.name}\" ({target.controls.Count}/{VRCExpressionsMenu.MAX_CONTROLS})", GUILayout.Height(24)))
                {
                    var index = AddControl(target);
                    Reload();
                    tree.SelectControl(target, index);
                    GUIUtility.ExitGUI();
                }
            }
            EditorGUILayout.Space();
            if (item == null)
            {
                EditorGUILayout.HelpBox("Select a control to edit it. Drag controls to reorder them or drop them onto a submenu to move them.", MessageType.None);
                return;
            }
            if (item.Index < 0)
            {
                EditorGUILayout.ObjectField("Menu", item.Menu, typeof(VRCExpressionsMenu), false);
                EditorGUILayout.LabelField("Controls", $"{item.Menu.controls.Count}/{VRCExpressionsMenu.MAX_CONTROLS}");
                return;
            }

            var menu = item.Menu;
            var control = item.Control;
            EditorGUILayout.ObjectField("In menu", menu, typeof(VRCExpressionsMenu), false);
            EditorGUI.BeginChangeCheck();
            var name = EditorGUILayout.TextField("Name", control.name);
            var type = (ControlType)EditorGUILayout.EnumPopup("Type", control.type);
            var parameter = ParameterField("Parameter", control.parameter?.name);
            var value = EditorGUILayout.FloatField("Value", control.value);
            var icon = (Texture2D)EditorGUILayout.ObjectField("Icon", control.icon, typeof(Texture2D), false);
            var subMenu = control.subMenu;
            if (type == ControlType.SubMenu)
                subMenu = (VRCExpressionsMenu)EditorGUILayout.ObjectField("Submenu", control.subMenu, typeof(VRCExpressionsMenu), false);
            var subCount = SubParameterCount(type);
            var subNames = new string[subCount];
            for (var i = 0; i < subCount; i++)
            {
                var existing = control.subParameters != null && i < control.subParameters.Length ? control.subParameters[i]?.name : "";
                subNames[i] = ParameterField(SubParameterLabel(type, i), existing);
            }
            if (EditorGUI.EndChangeCheck())
            {
                if (subMenu != null && AvatarToolsUtil.AllMenus(subMenu).Contains(menu))
                {
                    ShowNotification(new GUIContent("That submenu contains this menu"));
                    subMenu = control.subMenu;
                }
                Undo.RecordObject(menu, "Edit Menu Control");
                control.name = name;
                control.type = type;
                control.parameter = new VRCExpressionsMenu.Control.Parameter { name = parameter ?? "" };
                control.value = value;
                control.icon = icon;
                control.subMenu = type == ControlType.SubMenu ? subMenu : null;
                control.subParameters = subNames.Select(n => new VRCExpressionsMenu.Control.Parameter { name = n ?? "" }).ToArray();
                if (type == ControlType.TwoAxisPuppet || type == ControlType.FourAxisPuppet)
                {
                    var labels = control.labels ?? Array.Empty<VRCExpressionsMenu.Control.Label>();
                    if (labels.Length < 4) control.labels = labels.Concat(Enumerable.Range(0, 4 - labels.Length).Select(_ => new VRCExpressionsMenu.Control.Label())).ToArray();
                }
                EditorUtility.SetDirty(menu);
                tree.Reload();
            }

            var warning = Warning(control, avatar.expressionParameters);
            if (warning != "") EditorGUILayout.HelpBox(warning, MessageType.Warning);
            EditorGUILayout.Space();
            if (GUILayout.Button("Delete Control")) DeleteSelected();
        }

        string ParameterField(string label, string current)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                current = EditorGUILayout.TextField(label, current ?? "");
                var names = avatar.expressionParameters?.parameters?.Where(p => p != null && !string.IsNullOrEmpty(p.name))
                    .Select(p => p.name).Distinct().ToArray() ?? Array.Empty<string>();
                var picked = EditorGUILayout.Popup(-1, names, GUILayout.Width(20));
                if (picked >= 0) current = names[picked];
            }
            return current;
        }

        internal void DeleteSelected()
        {
            var item = tree.SelectedItem;
            if (item == null || item.Index < 0) return;
            if (!EditorUtility.DisplayDialog("Delete Control",
                    $"Delete 1 control \"{item.Control.name}\" from \"{item.Menu.name}\"? Submenu assets are not deleted.", "Delete", "Cancel"))
                return;
            DeleteControl(item.Menu, item.Index);
            treeState.selectedIDs.Clear();
            Reload();
            GUIUtility.ExitGUI();
        }

        internal void Move(MenuTree.Item source, VRCExpressionsMenu to, int index)
        {
            var newIndex = MoveControl(source.Menu, source.Index, to, index);
            Reload();
            if (newIndex >= 0) tree.SelectControl(to, newIndex);
        }

        internal void Rename(MenuTree.Item item, string name)
        {
            RenameControl(item.Menu, item.Index, name);
            Reload();
        }

        internal class MenuTree : TreeView
        {
            const string DragKey = "AvatarToolsMenuControl";

            internal class Item : TreeViewItem
            {
                public VRCExpressionsMenu Menu;
                public int Index;
                public VRCExpressionsMenu ChildMenu;
                public bool Recursive;
                public VRCExpressionsMenu.Control Control => Index >= 0 && Index < Menu.controls.Count ? Menu.controls[Index] : null;
            }

            readonly ExpressionMenuTreeEditor owner;
            readonly List<Item> items = new List<Item>();
            public VRCExpressionsMenu Root;
            public VRCExpressionParameters Parameters;

            public MenuTree(ExpressionMenuTreeEditor owner, TreeViewState state, MultiColumnHeader header) : base(state, header)
            {
                this.owner = owner;
                rowHeight = 20;
                showAlternatingRowBackgrounds = true;
            }

            public Item SelectedItem
            {
                get
                {
                    var selection = GetSelection();
                    return selection.Count == 0 ? null : items.FirstOrDefault(i => i.id == selection[0]);
                }
            }

            public void SelectControl(VRCExpressionsMenu menu, int index)
            {
                var item = items.FirstOrDefault(i => i.Menu == menu && i.Index == index);
                if (item == null) return;
                SetSelection(new[] { item.id }, TreeViewSelectionOptions.RevealAndFrame);
            }

            protected override TreeViewItem BuildRoot()
            {
                items.Clear();
                var hidden = new TreeViewItem { id = 0, depth = -1, displayName = "Hidden", children = new List<TreeViewItem>() };
                if (Root == null) return hidden;
                var nextId = 1;
                var top = new Item { id = nextId++, displayName = Root.name, Menu = Root, Index = -1, ChildMenu = Root };
                items.Add(top);
                hidden.AddChild(top);
                AddChildren(top, Root, new HashSet<VRCExpressionsMenu> { Root }, ref nextId);
                SetupDepthsFromParentsAndChildren(hidden);
                return hidden;
            }

            void AddChildren(Item parent, VRCExpressionsMenu menu, HashSet<VRCExpressionsMenu> ancestors, ref int nextId)
            {
                for (var i = 0; i < menu.controls.Count; i++)
                {
                    var control = menu.controls[i];
                    var child = new Item
                    {
                        id = nextId++, displayName = control.name, Menu = menu, Index = i,
                        ChildMenu = control.type == ControlType.SubMenu ? control.subMenu : null
                    };
                    items.Add(child);
                    parent.AddChild(child);
                    if (child.ChildMenu == null) continue;
                    if (ancestors.Contains(child.ChildMenu))
                    {
                        child.Recursive = true;
                        continue;
                    }
                    ancestors.Add(child.ChildMenu);
                    AddChildren(child, child.ChildMenu, ancestors, ref nextId);
                    ancestors.Remove(child.ChildMenu);
                }
            }

            protected override void RowGUI(RowGUIArgs args)
            {
                var item = (Item)args.item;
                var control = item.Control;
                for (var i = 0; i < args.GetNumVisibleColumns(); i++)
                {
                    var rect = args.GetCellRect(i);
                    CenterRectUsingSingleLineHeight(ref rect);
                    switch (args.GetColumn(i))
                    {
                        case 0:
                            rect.xMin += GetContentIndent(item);
                            if (!args.isRenaming) GUI.Label(rect, item.Index < 0 ? $"{item.displayName} (root)" : item.displayName, args.selected ? EditorStyles.whiteLabel : EditorStyles.label);
                            break;
                        case 1:
                            GUI.Label(rect, control != null ? control.type.ToString() : "Menu");
                            break;
                        case 2:
                            GUI.Label(rect, control?.parameter?.name ?? "");
                            break;
                        case 3:
                            if (control != null) GUI.Label(rect, control.value.ToString("0.##"));
                            break;
                        case 4:
                            var iconRect = args.GetCellRect(i);
                            if (control?.icon != null) GUI.DrawTexture(iconRect, control.icon, ScaleMode.ScaleToFit);
                            break;
                        case 5:
                            var warning = ItemWarning(item);
                            if (warning != "")
                                GUI.Label(rect, new GUIContent(warning, EditorGUIUtility.IconContent("console.warnicon.sml").image, warning));
                            break;
                    }
                }
            }

            string ItemWarning(Item item)
            {
                if (item.Index < 0)
                    return item.Menu.controls.Count > VRCExpressionsMenu.MAX_CONTROLS
                        ? $"Menu has {item.Menu.controls.Count} controls (max {VRCExpressionsMenu.MAX_CONTROLS})" : "";
                var warning = Warning(item.Control, Parameters);
                if (item.Recursive) warning = warning == "" ? "Submenu loops back to an ancestor" : warning + "; Submenu loops back to an ancestor";
                return warning;
            }

            protected override bool CanRename(TreeViewItem item) => item is Item i && i.Index >= 0;

            protected override Rect GetRenameRect(Rect rowRect, int row, TreeViewItem item)
            {
                var rect = multiColumnHeader.GetCellRect(0, rowRect);
                rect.xMin += GetContentIndent(item);
                CenterRectUsingSingleLineHeight(ref rect);
                return rect;
            }

            protected override void RenameEnded(RenameEndedArgs args)
            {
                if (!args.acceptedRename || args.newName == args.originalName) return;
                var item = items.FirstOrDefault(i => i.id == args.itemID);
                if (item != null && item.Index >= 0) owner.Rename(item, args.newName);
            }

            protected override bool CanMultiSelect(TreeViewItem item) => false;

            protected override bool CanStartDrag(CanStartDragArgs args) => args.draggedItem is Item i && i.Index >= 0;

            protected override void SetupDragAndDrop(SetupDragAndDropArgs args)
            {
                var item = items.FirstOrDefault(i => i.id == args.draggedItemIDs[0]);
                if (item == null) return;
                DragAndDrop.PrepareStartDrag();
                DragAndDrop.SetGenericData(DragKey, item.id);
                DragAndDrop.objectReferences = Array.Empty<Object>();
                DragAndDrop.StartDrag(item.displayName);
            }

            protected override DragAndDropVisualMode HandleDragAndDrop(DragAndDropArgs args)
            {
                if (!(DragAndDrop.GetGenericData(DragKey) is int id)) return DragAndDropVisualMode.None;
                var source = items.FirstOrDefault(i => i.id == id);
                if (source == null) return DragAndDropVisualMode.Rejected;
                VRCExpressionsMenu target = null;
                var index = 0;
                if (args.dragAndDropPosition == DragAndDropPosition.UponItem && args.parentItem is Item upon)
                {
                    target = upon.ChildMenu;
                    index = target != null ? target.controls.Count : 0;
                }
                else if (args.dragAndDropPosition == DragAndDropPosition.BetweenItems && args.parentItem is Item parent && !parent.Recursive)
                {
                    target = parent.ChildMenu;
                    index = args.insertAtIndex;
                }
                if (target == null || !CanMove(source.Menu, source.Index, target)) return DragAndDropVisualMode.Rejected;
                if (args.performDrop) owner.Move(source, target, index);
                return DragAndDropVisualMode.Move;
            }

            protected override void KeyEvent()
            {
                var e = Event.current;
                if (e.type != EventType.KeyDown || e.keyCode != KeyCode.Delete && e.keyCode != KeyCode.Backspace) return;
                if (SelectedItem == null || SelectedItem.Index < 0) return;
                e.Use();
                owner.DeleteSelected();
            }
        }
    }
}
