using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using VRC.SDK3.Avatars.Components;

namespace AvatarTools.Editor
{
    public class MaterialBatchEditor : EditorWindow
    {
        public class MaterialInfo
        {
            public Material Material;
            public string Path;
            public bool ReadOnly;
            public string Signature;
            public int DuplicateGroup = -1;
            public readonly List<(Renderer renderer, int slot)> Users = new List<(Renderer, int)>();
        }

        public class PropertyInfo
        {
            public string Name;
            public string Description;
            public ShaderPropertyType Type;
            public Vector2 Range;
        }

        VRCAvatarDescriptor avatar;
        List<MaterialInfo> materials = new List<MaterialInfo>();
        readonly HashSet<Material> selected = new HashSet<Material>();
        Vector2 scroll;
        Shader newShader;
        int propertyIndex;
        float floatValue;
        Color colorValue = Color.white;
        Vector4 vectorValue;
        Texture textureValue;
        int renderQueue = -1;

        [MenuItem(AvatarToolsUtil.MenuRoot + "Optimization/Material Batch Editor")]
        static void Open()
        {
            var window = GetWindow<MaterialBatchEditor>("Material Batch Editor");
            window.avatar = AvatarToolsUtil.SelectedAvatar();
            window.Refresh();
        }

        void OnEnable()
        {
            Undo.undoRedoPerformed += Refresh;
        }

        void OnDisable()
        {
            Undo.undoRedoPerformed -= Refresh;
        }

        void Refresh()
        {
            materials = avatar == null ? new List<MaterialInfo>() : Gather(avatar.gameObject);
            selected.RemoveWhere(m => m == null || materials.All(i => i.Material != m));
            Repaint();
        }

        public static bool IsReadOnly(Material material)
        {
            var path = AssetDatabase.GetAssetPath(material);
            return !path.StartsWith("Assets/", StringComparison.Ordinal) || !path.EndsWith(".mat", StringComparison.OrdinalIgnoreCase);
        }

        public static List<MaterialInfo> Gather(GameObject root)
        {
            var byMaterial = new Dictionary<Material, MaterialInfo>();
            foreach (var renderer in root.GetComponentsInChildren<Renderer>(true))
            {
                var shared = renderer.sharedMaterials;
                for (var slot = 0; slot < shared.Length; slot++)
                {
                    var material = shared[slot];
                    if (material == null) continue;
                    if (!byMaterial.TryGetValue(material, out var info))
                        byMaterial[material] = info = new MaterialInfo
                        {
                            Material = material, Path = AssetDatabase.GetAssetPath(material), ReadOnly = IsReadOnly(material), Signature = Signature(material)
                        };
                    info.Users.Add((renderer, slot));
                }
            }
            var list = byMaterial.Values.OrderBy(i => i.Material.shader != null ? i.Material.shader.name : "").ThenBy(i => i.Material.name).ToList();
            var group = 0;
            foreach (var duplicates in list.GroupBy(i => i.Signature).Where(g => g.Count() > 1))
            {
                foreach (var info in duplicates) info.DuplicateGroup = group;
                group++;
            }
            return list;
        }

        public static string Signature(Material material)
        {
            var shader = material.shader;
            var sb = new StringBuilder();
            string F(float f) => f.ToString("R", CultureInfo.InvariantCulture);
            sb.Append(shader != null ? shader.GetInstanceID() : 0).Append('|').Append(material.renderQueue)
                .Append('|').Append(material.enableInstancing).Append('|').Append(material.doubleSidedGI)
                .Append('|').Append(material.globalIlluminationFlags).Append('|');
            sb.Append(string.Join(",", material.shaderKeywords.OrderBy(k => k, StringComparer.Ordinal))).Append('|');
            for (var p = 0; p < material.passCount; p++)
            {
                var pass = material.GetPassName(p);
                if (!material.GetShaderPassEnabled(pass)) sb.Append("!").Append(pass).Append(';');
            }
            if (shader == null) return sb.ToString();
            for (var i = 0; i < shader.GetPropertyCount(); i++)
            {
                var name = shader.GetPropertyName(i);
                sb.Append(name).Append('=');
                switch (shader.GetPropertyType(i))
                {
                    case ShaderPropertyType.Color:
                        var c = material.GetColor(name);
                        sb.Append(F(c.r)).Append(',').Append(F(c.g)).Append(',').Append(F(c.b)).Append(',').Append(F(c.a));
                        break;
                    case ShaderPropertyType.Vector:
                        var v = material.GetVector(name);
                        sb.Append(F(v.x)).Append(',').Append(F(v.y)).Append(',').Append(F(v.z)).Append(',').Append(F(v.w));
                        break;
                    case ShaderPropertyType.Float:
                    case ShaderPropertyType.Range:
                        sb.Append(F(material.GetFloat(name)));
                        break;
                    case ShaderPropertyType.Int:
                        sb.Append(material.GetInteger(name));
                        break;
                    case ShaderPropertyType.Texture:
                        var texture = material.GetTexture(name);
                        var offset = material.GetTextureOffset(name);
                        var scale = material.GetTextureScale(name);
                        sb.Append(texture != null ? texture.GetInstanceID() : 0).Append(',')
                            .Append(F(offset.x)).Append(',').Append(F(offset.y)).Append(',').Append(F(scale.x)).Append(',').Append(F(scale.y));
                        break;
                }
                sb.Append(';');
            }
            return sb.ToString();
        }

        public static int MergeDuplicates(IEnumerable<MaterialInfo> infos)
        {
            var assignments = new Dictionary<Renderer, Material[]>();
            var count = 0;
            foreach (var group in infos.Where(i => i.DuplicateGroup >= 0).GroupBy(i => i.DuplicateGroup))
            {
                var canonical = group.OrderBy(i => i.ReadOnly).ThenByDescending(i => i.Users.Count).ThenBy(i => i.Material.name).First();
                foreach (var info in group.Where(i => i != canonical))
                foreach (var (renderer, slot) in info.Users)
                {
                    if (renderer == null) continue;
                    if (!assignments.TryGetValue(renderer, out var shared)) assignments[renderer] = shared = renderer.sharedMaterials;
                    shared[slot] = canonical.Material;
                    count++;
                }
            }
            AssignMaterials(assignments, "Merge Duplicate Materials");
            return count;
        }

        static void AssignMaterials(Dictionary<Renderer, Material[]> assignments, string undoName)
        {
            if (assignments.Count == 0) return;
            Undo.RecordObjects(assignments.Keys.Cast<UnityEngine.Object>().ToArray(), undoName);
            foreach (var pair in assignments)
            {
                pair.Key.sharedMaterials = pair.Value;
                PrefabUtility.RecordPrefabInstancePropertyModifications(pair.Key);
            }
        }

        public static List<PropertyInfo> PropertyUnion(IEnumerable<Material> materials)
        {
            var byName = new Dictionary<string, PropertyInfo>();
            foreach (var shader in materials.Where(m => m != null && m.shader != null).Select(m => m.shader).Distinct())
            for (var i = 0; i < shader.GetPropertyCount(); i++)
            {
                var name = shader.GetPropertyName(i);
                if (byName.ContainsKey(name)) continue;
                var type = shader.GetPropertyType(i);
                byName[name] = new PropertyInfo
                {
                    Name = name, Description = shader.GetPropertyDescription(i), Type = type,
                    Range = type == ShaderPropertyType.Range ? shader.GetPropertyRangeLimits(i) : Vector2.zero
                };
            }
            return byName.Values.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        static bool SameKind(ShaderPropertyType a, ShaderPropertyType b)
        {
            bool IsNumber(ShaderPropertyType t) => t == ShaderPropertyType.Float || t == ShaderPropertyType.Range || t == ShaderPropertyType.Int;
            return a == b || IsNumber(a) && IsNumber(b);
        }

        public static int EditMaterials(IEnumerable<Material> materials, string undoName, Func<Material, bool> edit)
        {
            var list = materials.Where(m => m != null && !IsReadOnly(m)).Distinct().ToArray();
            if (list.Length == 0) return 0;
            Undo.RecordObjects(list.Cast<UnityEngine.Object>().ToArray(), undoName);
            var count = 0;
            foreach (var material in list)
            {
                if (!edit(material)) continue;
                EditorUtility.SetDirty(material);
                count++;
            }
            AssetDatabase.SaveAssets();
            return count;
        }

        public static int SetShader(IEnumerable<Material> materials, Shader shader)
        {
            return EditMaterials(materials, "Swap Shader", m =>
            {
                if (m.shader == shader) return false;
                m.shader = shader;
                return true;
            });
        }

        public static int SetProperty(IEnumerable<Material> materials, PropertyInfo property, float number, Color color, Vector4 vector, Texture texture)
        {
            return EditMaterials(materials, "Set Material Property", m =>
            {
                var index = m.shader.FindPropertyIndex(property.Name);
                if (index < 0) return false;
                var type = m.shader.GetPropertyType(index);
                if (!SameKind(type, property.Type)) return false;
                switch (type)
                {
                    case ShaderPropertyType.Float:
                    case ShaderPropertyType.Range:
                        m.SetFloat(property.Name, number);
                        break;
                    case ShaderPropertyType.Int:
                        m.SetInteger(property.Name, Mathf.RoundToInt(number));
                        break;
                    case ShaderPropertyType.Color:
                        m.SetColor(property.Name, color);
                        break;
                    case ShaderPropertyType.Vector:
                        m.SetVector(property.Name, vector);
                        break;
                    case ShaderPropertyType.Texture:
                        m.SetTexture(property.Name, texture);
                        break;
                }
                return true;
            });
        }

        public static int SetRenderQueue(IEnumerable<Material> materials, int queue)
        {
            return EditMaterials(materials, "Set Render Queue", m =>
            {
                m.renderQueue = queue;
                return true;
            });
        }

        public static int SetInstancing(IEnumerable<Material> materials, bool enabled)
        {
            return EditMaterials(materials, "Set GPU Instancing", m =>
            {
                if (m.enableInstancing == enabled) return false;
                m.enableInstancing = enabled;
                return true;
            });
        }

        public static int MakeEditableCopies(VRCAvatarDescriptor avatar, IEnumerable<MaterialInfo> infos)
        {
            var assignments = new Dictionary<Renderer, Material[]>();
            var created = 0;
            foreach (var info in infos.Where(i => i.ReadOnly))
            {
                var copy = new Material(info.Material) { name = info.Material.name };
                AvatarToolsUtil.CreateAsset(copy, AvatarToolsUtil.AvatarFolder(avatar, "Materials"), info.Material.name, "mat");
                created++;
                foreach (var (renderer, slot) in info.Users)
                {
                    if (renderer == null) continue;
                    if (!assignments.TryGetValue(renderer, out var shared)) assignments[renderer] = shared = renderer.sharedMaterials;
                    shared[slot] = copy;
                }
            }
            AssignMaterials(assignments, "Make Editable Material Copies");
            AssetDatabase.SaveAssets();
            return created;
        }

        void OnGUI()
        {
            EditorGUI.BeginChangeCheck();
            avatar = AvatarToolsUtil.AvatarField(avatar);
            if (EditorGUI.EndChangeCheck()) Refresh();
            if (avatar == null)
            {
                EditorGUILayout.HelpBox("Select an avatar with a VRCAvatarDescriptor.", MessageType.Info);
                return;
            }
            if (materials.Any(i => i.Material == null)) Refresh();
            if (GUILayout.Button("Rescan")) Refresh();
            DrawDuplicates();
            DrawTable();
            DrawBulkActions();
        }

        void DrawDuplicates()
        {
            var groups = materials.Where(i => i.DuplicateGroup >= 0).GroupBy(i => i.DuplicateGroup).ToList();
            var readOnly = materials.Count(i => i.ReadOnly);
            EditorGUILayout.LabelField($"{materials.Count} materials, {readOnly} read-only, {groups.Count} duplicate groups", EditorStyles.boldLabel);
            if (groups.Count == 0) return;
            var redundant = groups.Sum(g => g.Count() - 1);
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField($"{redundant} material(s) are identical to another one (same shader, keywords, queue and property values).", EditorStyles.wordWrappedMiniLabel);
                if (GUILayout.Button("Merge duplicates", GUILayout.Width(130)) &&
                    EditorUtility.DisplayDialog("Merge Duplicate Materials",
                        $"Reassign the slots of {redundant} duplicate material(s) to one material per group? Editable materials are preferred as the kept material. Material assets are not deleted.",
                        "Merge", "Cancel"))
                {
                    var count = MergeDuplicates(materials);
                    ShowNotification(new GUIContent($"Reassigned {count} slots"));
                    Refresh();
                    GUIUtility.ExitGUI();
                }
            }
        }

        void DrawTable()
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Select All", GUILayout.Width(80)))
                    foreach (var i in materials) selected.Add(i.Material);
                if (GUILayout.Button("Select Editable", GUILayout.Width(110)))
                {
                    selected.Clear();
                    foreach (var i in materials.Where(i => !i.ReadOnly)) selected.Add(i.Material);
                }
                if (GUILayout.Button("Select None", GUILayout.Width(90))) selected.Clear();
            }
            scroll = EditorGUILayout.BeginScrollView(scroll);
            foreach (var info in materials)
            {
                using (new EditorGUILayout.HorizontalScope(EditorStyles.helpBox))
                {
                    var isSelected = selected.Contains(info.Material);
                    if (EditorGUILayout.Toggle(isSelected, GUILayout.Width(16)) != isSelected)
                    {
                        if (isSelected) selected.Remove(info.Material);
                        else selected.Add(info.Material);
                    }
                    using (new EditorGUILayout.VerticalScope())
                    {
                        using (new EditorGUILayout.HorizontalScope())
                        {
                            if (GUILayout.Button(info.Material.name, EditorStyles.linkLabel)) EditorGUIUtility.PingObject(info.Material);
                            GUILayout.FlexibleSpace();
                            if (info.DuplicateGroup >= 0) GUILayout.Label($"duplicate group {info.DuplicateGroup + 1}", EditorStyles.miniBoldLabel);
                            if (info.ReadOnly) GUILayout.Label("read-only", EditorStyles.miniBoldLabel);
                        }
                        var shader = info.Material.shader != null ? info.Material.shader.name : "(no shader)";
                        GUILayout.Label($"{shader}  |  queue {info.Material.renderQueue}  |  {info.Material.shaderKeywords.Length} keywords" +
                                        (info.Material.enableInstancing ? "  |  instanced" : ""), EditorStyles.miniLabel);
                        var users = string.Join(", ", info.Users.Select(u => u.renderer == null ? "(missing)" : $"{u.renderer.name}[{u.slot}]"));
                        GUILayout.Label(new GUIContent("Used by: " + users, users), EditorStyles.miniLabel);
                    }
                }
            }
            EditorGUILayout.EndScrollView();
        }

        bool Confirm(string title, string action, ICollection<Material> targets)
        {
            var names = string.Join("\n", targets.Take(15).Select(m => m.name));
            if (targets.Count > 15) names += $"\n... and {targets.Count - 15} more";
            return EditorUtility.DisplayDialog(title, $"{action} on {targets.Count} material(s)?\n\n{names}\n\nThis edits the material assets, affecting everything that uses them.", "Apply", "Cancel");
        }

        void Report(string what, int count)
        {
            ShowNotification(new GUIContent($"{what}: {count} material(s) changed"));
            Refresh();
            GUIUtility.ExitGUI();
        }

        void DrawBulkActions()
        {
            var selectedInfos = materials.Where(i => selected.Contains(i.Material)).ToList();
            var editable = selectedInfos.Where(i => !i.ReadOnly).Select(i => i.Material).ToList();
            var readOnly = selectedInfos.Where(i => i.ReadOnly).ToList();

            EditorGUILayout.Space();
            EditorGUILayout.LabelField($"Bulk edit ({editable.Count} editable selected)", EditorStyles.boldLabel);
            if (readOnly.Count > 0)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.HelpBox($"{readOnly.Count} selected material(s) are read-only (in Packages, embedded in a model, or built-in) and are skipped by bulk edits.", MessageType.Info);
                    if (GUILayout.Button("Make editable copies", GUILayout.Width(150), GUILayout.Height(38)) &&
                        EditorUtility.DisplayDialog("Make Editable Copies",
                            $"Copy {readOnly.Count} read-only material(s) into {AvatarToolsUtil.GeneratedRoot}/{AvatarToolsUtil.SafeName(avatar.gameObject.name)}/Materials and reassign their slots on this avatar?",
                            "Copy", "Cancel"))
                    {
                        var copies = MakeEditableCopies(avatar, readOnly);
                        ShowNotification(new GUIContent($"Created {copies} copies"));
                        Refresh();
                        GUIUtility.ExitGUI();
                    }
                }
            }

            using (new EditorGUI.DisabledScope(editable.Count == 0))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    newShader = (Shader)EditorGUILayout.ObjectField("Shader", newShader, typeof(Shader), false);
                    using (new EditorGUI.DisabledScope(newShader == null))
                        if (GUILayout.Button("Swap", GUILayout.Width(70)) && Confirm("Swap Shader", $"Set shader {newShader.name}", editable))
                            Report("Swap shader", SetShader(editable, newShader));
                }
                EditorGUILayout.LabelField("Properties with matching names are carried over by Unity; others keep their stored values.", EditorStyles.miniLabel);

                DrawPropertyEditor(editable);

                using (new EditorGUILayout.HorizontalScope())
                {
                    renderQueue = EditorGUILayout.IntField(new GUIContent("Render queue", "-1 uses the shader's queue."), renderQueue);
                    renderQueue = Mathf.Clamp(renderQueue, -1, 5000);
                    if (GUILayout.Button("Set", GUILayout.Width(70)) && Confirm("Set Render Queue", $"Set render queue {renderQueue}", editable))
                        Report("Render queue", SetRenderQueue(editable, renderQueue));
                }
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.PrefixLabel("GPU instancing");
                    if (GUILayout.Button("Enable") && Confirm("Enable GPU Instancing", "Enable GPU instancing", editable))
                        Report("Instancing", SetInstancing(editable, true));
                    if (GUILayout.Button("Disable") && Confirm("Disable GPU Instancing", "Disable GPU instancing", editable))
                        Report("Instancing", SetInstancing(editable, false));
                }
            }
        }

        void DrawPropertyEditor(List<Material> editable)
        {
            var properties = PropertyUnion(editable);
            if (properties.Count == 0)
            {
                EditorGUILayout.LabelField("Property", "Select editable materials to list their shader properties.");
                return;
            }
            propertyIndex = Mathf.Clamp(propertyIndex, 0, properties.Count - 1);
            propertyIndex = EditorGUILayout.Popup("Property", propertyIndex,
                properties.Select(p => $"{p.Description} ({p.Name}) [{p.Type}]".Replace("/", "∕")).ToArray());
            var property = properties[propertyIndex];
            using (new EditorGUILayout.HorizontalScope())
            {
                switch (property.Type)
                {
                    case ShaderPropertyType.Range:
                        floatValue = EditorGUILayout.Slider("Value", floatValue, property.Range.x, property.Range.y);
                        break;
                    case ShaderPropertyType.Int:
                        floatValue = EditorGUILayout.IntField("Value", Mathf.RoundToInt(floatValue));
                        break;
                    case ShaderPropertyType.Float:
                        floatValue = EditorGUILayout.FloatField("Value", floatValue);
                        break;
                    case ShaderPropertyType.Color:
                        colorValue = EditorGUILayout.ColorField(new GUIContent("Value"), colorValue, true, true, true);
                        break;
                    case ShaderPropertyType.Vector:
                        vectorValue = EditorGUILayout.Vector4Field("Value", vectorValue);
                        break;
                    case ShaderPropertyType.Texture:
                        textureValue = (Texture)EditorGUILayout.ObjectField("Value", textureValue, typeof(Texture), false);
                        break;
                }
                var has = editable.Count(m => m.shader != null && m.shader.FindPropertyIndex(property.Name) >= 0);
                if (GUILayout.Button($"Set on {has}", GUILayout.Width(80)) && Confirm("Set Material Property", $"Set {property.Name}",
                        editable.Where(m => m.shader != null && m.shader.FindPropertyIndex(property.Name) >= 0).ToList()))
                    Report("Set " + property.Name, SetProperty(editable, property, floatValue, colorValue, vectorValue, textureValue));
            }
        }
    }
}
