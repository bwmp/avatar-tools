using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Animations;
using VRC.SDK3.Avatars;
using VRC.SDK3.Avatars.Components;
using VRC.SDKBase.Validation;
using VRC.SDKBase.Validation.Performance;
using VRC.SDKBase.Validation.Performance.Stats;
using SDK3AvatarValidation = VRC.SDK3.Validation.AvatarValidation;

namespace AvatarTools.Editor
{
    public class QuestConversionHelper : EditorWindow
    {
        public class Report
        {
            public PerformanceRating Overall;
            public readonly List<(MessageType type, string text)> Lines = new List<(MessageType, string)>();
            public int Blockers => Lines.Count(l => l.type == MessageType.Error);
        }

        public class ConvertOptions
        {
            public Shader Shader;
            public Shader ParticleShader;
            public bool RemoveUnsupported = true;
            public bool ConvertConstraints = true;
        }

        // Removal order matters: components that [RequireComponent] another must go first.
        static readonly Type[] UnsupportedOnMobile =
        {
            typeof(VRCSpatialAudioSource), typeof(Joint), typeof(FlareLayer), typeof(Camera), typeof(Light),
            typeof(Cloth), typeof(AudioSource), typeof(Collider), typeof(Rigidbody)
        };

        const int PolygonHardLimit = 70000;

        VRCAvatarDescriptor avatar;
        VRCAvatarDescriptor converted;
        Report before, after;
        readonly ConvertOptions options = new ConvertOptions();
        readonly List<string> log = new List<string>();
        Vector2 scroll;

        [MenuItem(AvatarToolsUtil.MenuRoot + "Optimization/Quest Conversion Helper")]
        public static void Open()
        {
            var window = GetWindow<QuestConversionHelper>("Quest Conversion");
            window.avatar = AvatarToolsUtil.SelectedAvatar();
            window.before = window.avatar == null ? null : Analyze(window.avatar);
        }

        static string[] MobileShaderNames(bool particles)
        {
            return AvatarValidation.ShaderWhiteList.Where(n => n.Contains("/Particles/") == particles && Shader.Find(n) != null).ToArray();
        }

        static bool IsMobileShader(Shader shader)
        {
            return shader != null && AvatarValidation.ShaderWhiteList.Contains(shader.name);
        }

        static bool IsUnsupported(Component component)
        {
            if (component == null) return false;
            var type = component.GetType();
            return UnsupportedOnMobile.Any(t => t.IsAssignableFrom(type)) || type.Name == "ONSPAudioSource";
        }

        public static Report Analyze(VRCAvatarDescriptor target)
        {
            var report = new Report();
            var go = target.gameObject;
            var stats = new AvatarPerformanceStats(true);
            AvatarPerformance.CalculatePerformanceStats(go.name, go, stats, true);
            var poor = AvatarPerformanceStats.GetStatLevelForRating(PerformanceRating.Poor, true);
            report.Overall = stats.GetPerformanceRatingForCategory(AvatarPerformanceCategory.Overall);
            var lines = report.Lines;

            var worst = ((AvatarPerformanceCategory[])Enum.GetValues(typeof(AvatarPerformanceCategory)))
                .Where(c => c > AvatarPerformanceCategory.Overall && c < AvatarPerformanceCategory.AvatarPerformanceCategoryCount &&
                            report.Overall > PerformanceRating.Excellent && stats.GetPerformanceRatingForCategory(c) == report.Overall)
                .Select(AvatarPerformanceStats.GetPerformanceCategoryDisplayName).ToList();
            lines.Add((report.Overall == PerformanceRating.VeryPoor ? MessageType.Error : MessageType.Info,
                $"Mobile rating (excluding texture memory and download size): {report.Overall}" +
                (worst.Count > 0 ? $" due to {string.Join(", ", worst)}" : "") +
                (report.Overall == PerformanceRating.VeryPoor ? ". Very Poor avatars are hidden by default on Quest/Android and show a fallback." : ".")));
            var polys = stats.polyCount ?? 0;
            if (polys > PolygonHardLimit) lines.Add((MessageType.Error, $"Polygon count {polys} exceeds {PolygonHardLimit}."));
            else if (polys > poor.polyCount) lines.Add((MessageType.Warning, $"Polygon count {polys} is above the mobile Poor limit ({poor.polyCount})."));

            var pb = stats.physBone ?? default;
            var pbOver = new List<string>();
            if (pb.componentCount > poor.physBone.componentCount) pbOver.Add($"components {pb.componentCount}/{poor.physBone.componentCount}");
            if (pb.transformCount > poor.physBone.transformCount) pbOver.Add($"transforms {pb.transformCount}/{poor.physBone.transformCount}");
            if (pb.colliderCount > poor.physBone.colliderCount) pbOver.Add($"colliders {pb.colliderCount}/{poor.physBone.colliderCount}");
            if (pb.collisionCheckCount > poor.physBone.collisionCheckCount) pbOver.Add($"collision checks {pb.collisionCheckCount}/{poor.physBone.collisionCheckCount}");
            lines.Add(pbOver.Count > 0
                ? (MessageType.Error, "PhysBones exceed mobile Very Poor limits (" + string.Join(", ", pbOver) + "): all PhysBones are removed on mobile.")
                : (MessageType.Info, $"PhysBones: {pb.componentCount} components, {pb.transformCount} transforms, {pb.colliderCount} colliders, {pb.collisionCheckCount} collision checks (within mobile limits)."));
            var contacts = stats.contactCount ?? 0;
            lines.Add(contacts > poor.contactCount
                ? (MessageType.Error, $"Contacts {contacts} exceed the mobile Very Poor limit ({poor.contactCount}): all contacts are removed on mobile.")
                : (MessageType.Info, $"Contacts: {contacts} (limit {poor.contactCount})."));
            var constraints = stats.constraintsCount ?? 0;
            if (constraints > poor.constraintsCount)
                lines.Add((MessageType.Warning, $"Constraints {constraints} exceed the mobile Very Poor limit ({poor.constraintsCount})."));
            var particles = stats.particleSystemCount ?? 0;
            if (particles > poor.particleSystemCount)
                lines.Add((MessageType.Warning, $"Particle systems {particles} exceed the mobile Very Poor limit ({poor.particleSystemCount})."));

            var unsupported = go.GetComponentsInChildren<Component>(true).Where(IsUnsupported).ToList();
            var illegal = SDK3AvatarValidation.FindIllegalComponents(go).Where(c => c != null && !unsupported.Contains(c)).ToList();
            foreach (var group in unsupported.Concat(illegal).GroupBy(c => c.GetType().Name))
                lines.Add((MessageType.Error, $"{group.Count()} {group.Key} component(s) not supported on mobile avatars."));
            var unityConstraints = go.GetComponentsInChildren<IConstraint>(true).Length;
            if (unityConstraints > 0)
                lines.Add((MessageType.Error, $"{unityConstraints} Unity constraint(s): convert to VRC Constraints for mobile."));

            var badMaterials = go.GetComponentsInChildren<Renderer>(true).SelectMany(r => r.sharedMaterials)
                .Where(m => m != null && !IsMobileShader(m.shader)).Distinct().ToList();
            if (badMaterials.Count > 0)
                lines.Add((MessageType.Error, $"{badMaterials.Count} material(s) use non-mobile shaders: {string.Join(", ", badMaterials.Take(8).Select(m => m.name))}{(badMaterials.Count > 8 ? ", ..." : "")}"));
            var swaps = AvatarToolsUtil.AllControllers(target).SelectMany(AvatarToolsUtil.AllClips).Distinct()
                .SelectMany(c => AnimationUtility.GetObjectReferenceCurveBindings(c).SelectMany(b => AnimationUtility.GetObjectReferenceCurve(c, b)))
                .Select(k => k.value as Material).Where(m => m != null && !IsMobileShader(m.shader)).Distinct().Count();
            if (swaps > 0)
                lines.Add((MessageType.Warning, $"Animations swap to {swaps} material(s) with non-mobile shaders; give the Quest copy its own controllers/clips."));

            lines.Add(report.Overall <= PerformanceRating.Good
                ? (MessageType.Info, "Fallback: eligible (mobile Good or better). Mark it as fallback in the VRChat SDK builder when uploading.")
                : (MessageType.Warning, "Fallback: not eligible (needs mobile Good or better). Without a fallback, blocked viewers see a generic robot."));
            if (EditorUserBuildSettings.activeBuildTarget != BuildTarget.Android)
                lines.Add((MessageType.Info, "Build target is not Android: switch platforms before uploading; texture memory is reported for the current target."));
            return report;
        }

        public static VRCAvatarDescriptor Convert(VRCAvatarDescriptor source, ConvertOptions options, List<string> log)
        {
            if (options.Shader == null) throw new ArgumentException("A mobile shader is required.");
            Undo.IncrementCurrentGroup();
            var group = Undo.GetCurrentGroup();
            var original = source.transform;
            var copyObject = Instantiate(source.gameObject, original.parent);
            copyObject.name = source.gameObject.name + " (Quest)";
            copyObject.transform.SetSiblingIndex(original.GetSiblingIndex() + 1);
            copyObject.transform.localPosition = original.localPosition;
            copyObject.transform.localRotation = original.localRotation;
            copyObject.transform.localScale = original.localScale;
            Undo.RegisterCreatedObjectUndo(copyObject, "Create Quest Copy");
            var copy = copyObject.GetComponent<VRCAvatarDescriptor>();

            if (options.RemoveUnsupported)
            {
                void Remove(IEnumerable<Component> components)
                {
                    foreach (var component in components.Where(c => c != null).ToList())
                    {
                        log.Add($"Removed {component.GetType().Name} from {component.name}");
                        Undo.DestroyObjectImmediate(component);
                    }
                }
                Remove(SDK3AvatarValidation.FindIllegalComponents(copyObject));
                Remove(copyObject.GetComponentsInChildren<Component>(true).Where(c => c != null && c.GetType().Name == "ONSPAudioSource"));
                foreach (var type in UnsupportedOnMobile)
                    Remove(copyObject.GetComponentsInChildren(type, true));
            }

            if (options.ConvertConstraints)
            {
                var constraints = copyObject.GetComponentsInChildren<IConstraint>(true);
                if (constraints.Length > 0)
                {
                    AvatarDynamicsSetup.DoConvertUnityConstraints(constraints, copy, false);
                    var remaining = copyObject.GetComponentsInChildren<IConstraint>(true).Length;
                    log.Add($"Converted {constraints.Length - remaining} of {constraints.Length} Unity constraint(s) to VRC Constraints (animation clips were not modified).");
                }
            }

            var folder = AvatarToolsUtil.AvatarFolder(source, "Quest");
            var cache = new Dictionary<Material, Material>();
            foreach (var renderer in copyObject.GetComponentsInChildren<Renderer>(true))
            {
                var particle = renderer is ParticleSystemRenderer || renderer is TrailRenderer || renderer is LineRenderer;
                var shader = particle && options.ParticleShader != null ? options.ParticleShader : options.Shader;
                var materials = renderer.sharedMaterials;
                var changed = false;
                for (var i = 0; i < materials.Length; i++)
                {
                    var material = materials[i];
                    if (material == null || IsMobileShader(material.shader)) continue;
                    if (!cache.TryGetValue(material, out var mobile))
                    {
                        mobile = ConvertMaterial(material, shader);
                        AvatarToolsUtil.CreateAsset(mobile, folder, mobile.name, "mat");
                        cache[material] = mobile;
                        log.Add($"Material {material.name} -> {AssetDatabase.GetAssetPath(mobile)} ({shader.name})");
                    }
                    materials[i] = mobile;
                    changed = true;
                }
                if (!changed) continue;
                Undo.RecordObject(renderer, "Assign Mobile Materials");
                renderer.sharedMaterials = materials;
            }
            Undo.CollapseUndoOperations(group);
            AssetDatabase.SaveAssets();
            Selection.activeGameObject = copyObject;
            return copy;
        }

        public static Material ConvertMaterial(Material source, Shader shader)
        {
            var result = new Material(shader) { name = source.name + "_Quest" };
            for (var i = 0; i < shader.GetPropertyCount(); i++)
            {
                var name = shader.GetPropertyName(i);
                if (!source.HasProperty(name)) continue;
                switch (shader.GetPropertyType(i))
                {
                    case UnityEngine.Rendering.ShaderPropertyType.Texture:
                        result.SetTexture(name, source.GetTexture(name));
                        result.SetTextureScale(name, source.GetTextureScale(name));
                        result.SetTextureOffset(name, source.GetTextureOffset(name));
                        break;
                    case UnityEngine.Rendering.ShaderPropertyType.Color:
                        result.SetColor(name, source.GetColor(name));
                        break;
                    case UnityEngine.Rendering.ShaderPropertyType.Vector:
                        result.SetVector(name, source.GetVector(name));
                        break;
                    default:
                        result.SetFloat(name, source.GetFloat(name));
                        break;
                }
            }
            if (result.HasProperty("_MainTex") && result.GetTexture("_MainTex") == null && source.mainTexture != null)
            {
                result.mainTexture = source.mainTexture;
                result.mainTextureScale = source.mainTextureScale;
                result.mainTextureOffset = source.mainTextureOffset;
            }
            return result;
        }

        void OnGUI()
        {
            EditorGUI.BeginChangeCheck();
            avatar = AvatarToolsUtil.AvatarField(avatar);
            if (EditorGUI.EndChangeCheck())
            {
                before = avatar == null ? null : Analyze(avatar);
                converted = null;
                after = null;
                log.Clear();
            }
            if (avatar == null)
            {
                EditorGUILayout.HelpBox("Select an avatar with a VRCAvatarDescriptor.", MessageType.Info);
                return;
            }

            options.Shader = ShaderPopup("Mobile shader", options.Shader, MobileShaderNames(false), "VRChat/Mobile/Toon Lit");
            options.ParticleShader = ShaderPopup("Particle / trail shader", options.ParticleShader, MobileShaderNames(true), "VRChat/Mobile/Particles/Additive");
            options.RemoveUnsupported = EditorGUILayout.ToggleLeft(
                "Remove components unsupported on mobile (lights, cloth, cameras, audio sources, rigidbodies, joints, colliders, non-whitelisted scripts)", options.RemoveUnsupported);
            options.ConvertConstraints = EditorGUILayout.ToggleLeft("Convert Unity constraints to VRC Constraints", options.ConvertConstraints);

            using (new EditorGUI.DisabledScope(options.Shader == null))
            {
                if (GUILayout.Button("Create Quest Copy", GUILayout.Height(28)) &&
                    EditorUtility.DisplayDialog("Create Quest Copy",
                        $"Duplicate '{avatar.gameObject.name}' as '{avatar.gameObject.name} (Quest)' and on the copy only:\n\n" +
                        $"- create mobile material copies ({options.Shader.name}) in {AvatarToolsUtil.GeneratedRoot}/{AvatarToolsUtil.SafeName(avatar.gameObject.name)}/Quest\n" +
                        (options.RemoveUnsupported ? "- remove components unsupported on mobile\n" : "") +
                        (options.ConvertConstraints ? "- convert Unity constraints to VRC Constraints\n" : "") +
                        "\nThe original avatar, its meshes, materials and animators are not modified.", "Create", "Cancel"))
                {
                    log.Clear();
                    before = Analyze(avatar);
                    converted = Convert(avatar, options, log);
                    after = Analyze(converted);
                    GUIUtility.ExitGUI();
                }
            }

            scroll = EditorGUILayout.BeginScrollView(scroll);
            if (GUILayout.Button("Re-analyze", GUILayout.Width(100)))
            {
                before = Analyze(avatar);
                after = converted == null ? null : Analyze(converted);
            }
            DrawReport("Before: " + avatar.gameObject.name, before);
            if (converted != null)
            {
                DrawReport("After: " + converted.gameObject.name, after);
                if (log.Count > 0)
                {
                    EditorGUILayout.LabelField("Changes", EditorStyles.boldLabel);
                    foreach (var line in log) EditorGUILayout.LabelField(line, EditorStyles.wordWrappedMiniLabel);
                }
            }
            EditorGUILayout.EndScrollView();
        }

        static Shader ShaderPopup(string label, Shader current, string[] names, string preferred)
        {
            if (names.Length == 0)
            {
                EditorGUILayout.HelpBox("No VRChat mobile shaders found.", MessageType.Error);
                return null;
            }
            var index = Array.IndexOf(names, current != null ? current.name : preferred);
            index = EditorGUILayout.Popup(label, Mathf.Max(0, index), names);
            return Shader.Find(names[index]);
        }

        static void DrawReport(string title, Report report)
        {
            if (report == null) return;
            EditorGUILayout.Space();
            EditorGUILayout.LabelField($"{title}  ({report.Blockers} blocker(s))", EditorStyles.boldLabel);
            foreach (var (type, text) in report.Lines) EditorGUILayout.HelpBox(text, type);
        }
    }
}
