using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using VRC.SDK3.Avatars.Components;
using VRC.SDKBase.Validation.Performance;
using VRC.SDKBase.Validation.Performance.Stats;

namespace AvatarTools.Editor
{
    public class TextureAuditor : EditorWindow
    {
        public class TextureInfo
        {
            public Texture Texture;
            public TextureImporter Importer;
            public string Path;
            public long Bytes;
            public readonly List<string> Users = new List<string>();
            public bool ReadOnly => Importer == null;
        }

        static readonly int[] MaxSizes = { 32, 64, 128, 256, 512, 1024, 2048, 4096, 8192 };
        const float BytesPerMegabyte = 1024f * 1024f;

        VRCAvatarDescriptor avatar;
        List<TextureInfo> textures = new List<TextureInfo>();
        readonly HashSet<Texture> selected = new HashSet<Texture>();
        Vector2 scroll;
        bool applyMaxSize = true, applyCompression, applyCrunch, applyMipmaps, applyStreaming;
        int importMaxSize = 2048, crunchQuality = 50;
        TextureImporterCompression compression = TextureImporterCompression.Compressed;

        [MenuItem(AvatarToolsUtil.MenuRoot + "Optimization/Texture Auditor")]
        public static void Open()
        {
            var window = GetWindow<TextureAuditor>("Texture Auditor");
            window.avatar = AvatarToolsUtil.SelectedAvatar();
            window.Refresh();
        }

        public static List<TextureInfo> Gather(GameObject root, VRCAvatarDescriptor descriptor = null)
        {
            var byTexture = new Dictionary<Texture, TextureInfo>();
            void AddMaterial(Material material, string user)
            {
                if (material == null) return;
                foreach (var property in material.GetTexturePropertyNames())
                {
                    if (!material.HasProperty(property)) continue;
                    var texture = material.GetTexture(property);
                    if (texture == null) continue;
                    if (!byTexture.TryGetValue(texture, out var info))
                    {
                        var path = AssetDatabase.GetAssetPath(texture);
                        var importer = AssetImporter.GetAtPath(path) as TextureImporter;
                        var editable = importer != null && path.StartsWith("Assets/", StringComparison.Ordinal);
                        byTexture[texture] = info = new TextureInfo
                        {
                            Texture = texture, Path = path, Importer = editable ? importer : null, Bytes = EstimateBytes(texture)
                        };
                    }
                    var label = user == null ? material.name : $"{material.name} ({user})";
                    if (!info.Users.Contains(label)) info.Users.Add(label);
                }
            }
            foreach (var renderer in root.GetComponentsInChildren<Renderer>(true))
            foreach (var material in renderer.sharedMaterials)
                AddMaterial(material, null);
            if (descriptor != null)
                foreach (var clip in AvatarToolsUtil.AllControllers(descriptor).SelectMany(AvatarToolsUtil.AllClips).Distinct())
                foreach (var binding in AnimationUtility.GetObjectReferenceCurveBindings(clip))
                foreach (var key in AnimationUtility.GetObjectReferenceCurve(clip, binding))
                    AddMaterial(key.value as Material, "animated in " + clip.name);
            return byTexture.Values.OrderByDescending(t => t.Bytes).ToList();
        }

        public static long EstimateBytes(Texture texture)
        {
            int width = texture.width, height = texture.height, depth = 1, faces = 1, mips = Mathf.Max(1, texture.mipmapCount);
            var format = texture.graphicsFormat;
            switch (texture)
            {
                case Cubemap _: faces = 6; break;
                case Texture2DArray array: faces = array.depth; break;
                case CubemapArray cubeArray: faces = cubeArray.cubemapCount * 6; break;
                case Texture3D volume: depth = volume.depth; break;
                case RenderTexture rt:
                    mips = rt.useMipMap ? mips : 1;
                    faces = rt.dimension == UnityEngine.Rendering.TextureDimension.Cube ? 6 : Mathf.Max(1, rt.volumeDepth);
                    var colour = MipChainBytes(rt.graphicsFormat, width, height, 1, mips) * faces * Mathf.Max(1, rt.antiAliasing);
                    var depthBytes = (long)width * height * (rt.depth >= 24 ? 4 : rt.depth / 8) * Mathf.Max(1, rt.antiAliasing);
                    return colour + depthBytes;
            }
            return MipChainBytes(format, width, height, depth, mips) * faces;
        }

        static long MipChainBytes(GraphicsFormat format, int width, int height, int depth, int mips)
        {
            if (format == GraphicsFormat.None) return 0;
            long blockBytes = GraphicsFormatUtility.GetBlockSize(format);
            int blockWidth = (int)GraphicsFormatUtility.GetBlockWidth(format), blockHeight = (int)GraphicsFormatUtility.GetBlockHeight(format);
            long total = 0;
            for (var mip = 0; mip < mips; mip++)
            {
                int w = Mathf.Max(1, width >> mip), h = Mathf.Max(1, height >> mip), d = Mathf.Max(1, depth >> mip);
                total += (long)((w + blockWidth - 1) / blockWidth) * ((h + blockHeight - 1) / blockHeight) * d * blockBytes;
            }
            return total;
        }

        public static float TotalMegabytes(IEnumerable<TextureInfo> textures)
        {
            return textures.Sum(t => t.Bytes) / BytesPerMegabyte;
        }

        public static PerformanceRating RateTextureMemory(float megabytes, bool mobile)
        {
            foreach (var rating in new[] { PerformanceRating.Excellent, PerformanceRating.Good, PerformanceRating.Medium, PerformanceRating.Poor })
                if (megabytes <= AvatarPerformanceStats.GetStatLevelForRating(rating, mobile).textureMegabytes)
                    return rating;
            return PerformanceRating.VeryPoor;
        }

        public static int ApplyImportSettings(IEnumerable<TextureImporter> importers, Action<TextureImporter> change)
        {
            var list = importers.Where(i => i != null).Distinct().ToList();
            if (list.Count == 0) return 0;
            AssetDatabase.StartAssetEditing();
            try
            {
                foreach (var importer in list)
                {
                    change(importer);
                    EditorUtility.SetDirty(importer);
                    importer.SaveAndReimport();
                }
            }
            finally
            {
                AssetDatabase.StopAssetEditing();
            }
            return list.Count;
        }

        void Refresh()
        {
            textures = avatar == null ? new List<TextureInfo>() : Gather(avatar.gameObject, avatar);
            selected.RemoveWhere(t => textures.All(i => i.Texture != t));
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
            if (GUILayout.Button("Rescan")) Refresh();
            DrawSummary();
            DrawTable();
            DrawBulkActions();
        }

        void DrawSummary()
        {
            var total = TotalMegabytes(textures);
            var pc = RateTextureMemory(total, false);
            var mobile = RateTextureMemory(total, true);
            EditorGUILayout.LabelField($"{textures.Count} textures, estimated VRAM {total:0.0} MB", EditorStyles.boldLabel);
            EditorGUILayout.LabelField($"PC: {pc} (Poor limit {AvatarPerformanceStats.GetStatLevelForRating(PerformanceRating.Poor, false).textureMegabytes} MB)   " +
                                       $"Mobile: {mobile} (Poor limit {AvatarPerformanceStats.GetStatLevelForRating(PerformanceRating.Poor, true).textureMegabytes} MB)");
            EditorGUILayout.HelpBox($"VRAM is computed from the imported format, dimensions and mip count for the active build target ({EditorUserBuildSettings.activeBuildTarget}). " +
                                    "Crunched textures are counted at their decompressed GPU size.", MessageType.None);
        }

        void DrawTable()
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Select All Editable", GUILayout.Width(130)))
                    foreach (var t in textures.Where(t => !t.ReadOnly)) selected.Add(t.Texture);
                if (GUILayout.Button("Select None", GUILayout.Width(90))) selected.Clear();
            }
            scroll = EditorGUILayout.BeginScrollView(scroll);
            foreach (var info in textures)
            {
                using (new EditorGUILayout.HorizontalScope(EditorStyles.helpBox))
                {
                    using (new EditorGUI.DisabledScope(info.ReadOnly))
                    {
                        var isSelected = selected.Contains(info.Texture);
                        if (EditorGUILayout.Toggle(isSelected, GUILayout.Width(16)) != isSelected)
                        {
                            if (isSelected) selected.Remove(info.Texture);
                            else selected.Add(info.Texture);
                        }
                    }
                    var thumbRect = GUILayoutUtility.GetRect(40, 40, GUILayout.Width(40), GUILayout.Height(40));
                    var preview = info.Texture is Texture2D ? info.Texture : AssetPreview.GetMiniThumbnail(info.Texture);
                    if (preview != null) GUI.DrawTexture(thumbRect, preview, ScaleMode.ScaleToFit);
                    using (new EditorGUILayout.VerticalScope())
                    {
                        using (new EditorGUILayout.HorizontalScope())
                        {
                            if (GUILayout.Button(info.Texture.name, EditorStyles.linkLabel)) EditorGUIUtility.PingObject(info.Texture);
                            GUILayout.FlexibleSpace();
                            GUILayout.Label($"{info.Bytes / BytesPerMegabyte:0.00} MB", EditorStyles.boldLabel);
                        }
                        GUILayout.Label(Describe(info), EditorStyles.miniLabel);
                        var users = string.Join(", ", info.Users);
                        GUILayout.Label(new GUIContent("Used by: " + users, users), EditorStyles.miniLabel);
                    }
                }
            }
            EditorGUILayout.EndScrollView();
        }

        static string Describe(TextureInfo info)
        {
            var texture = info.Texture;
            var parts = new List<string> { $"{texture.width}x{texture.height}", texture.graphicsFormat.ToString(), $"mips {texture.mipmapCount}" };
            if (info.Importer != null)
            {
                parts.Add("max " + info.Importer.maxTextureSize);
                parts.Add(info.Importer.textureCompression.ToString());
                if (info.Importer.crunchedCompression) parts.Add($"crunch {info.Importer.compressionQuality}");
                if (info.Importer.streamingMipmaps) parts.Add("streaming");
            }
            else parts.Add(texture is RenderTexture ? "RenderTexture (read-only)" : "read-only");
            return string.Join("  |  ", parts);
        }

        void DrawBulkActions()
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Bulk import settings", EditorStyles.boldLabel);
            using (new EditorGUILayout.HorizontalScope())
            {
                applyMaxSize = EditorGUILayout.ToggleLeft("Max size", applyMaxSize, GUILayout.Width(120));
                importMaxSize = EditorGUILayout.IntPopup(importMaxSize, MaxSizes.Select(s => s.ToString()).ToArray(), MaxSizes);
            }
            using (new EditorGUILayout.HorizontalScope())
            {
                applyCompression = EditorGUILayout.ToggleLeft("Compression", applyCompression, GUILayout.Width(120));
                compression = (TextureImporterCompression)EditorGUILayout.EnumPopup(compression);
            }
            using (new EditorGUILayout.HorizontalScope())
            {
                applyCrunch = EditorGUILayout.ToggleLeft("Enable crunch", applyCrunch, GUILayout.Width(120));
                crunchQuality = EditorGUILayout.IntSlider(crunchQuality, 0, 100);
            }
            applyMipmaps = EditorGUILayout.ToggleLeft("Enable mipmaps", applyMipmaps);
            applyStreaming = EditorGUILayout.ToggleLeft("Enable streaming mipmaps", applyStreaming);

            var targets = textures.Where(t => selected.Contains(t.Texture) && !t.ReadOnly).ToList();
            var anyAction = applyMaxSize || applyCompression || applyCrunch || applyMipmaps || applyStreaming;
            using (new EditorGUI.DisabledScope(targets.Count == 0 || !anyAction))
            {
                if (!GUILayout.Button($"Apply to {targets.Count} Selected Texture(s)", GUILayout.Height(28))) return;
            }
            var changes = new List<string>();
            if (applyMaxSize) changes.Add("max size " + importMaxSize);
            if (applyCompression) changes.Add("compression " + compression);
            if (applyCrunch) changes.Add($"crunch on (quality {crunchQuality})");
            if (applyMipmaps) changes.Add("mipmaps on");
            if (applyStreaming) changes.Add("streaming mipmaps on");
            var names = string.Join("\n", targets.Take(15).Select(t => t.Path));
            if (targets.Count > 15) names += $"\n... and {targets.Count - 15} more";
            if (!EditorUtility.DisplayDialog("Apply Texture Import Settings",
                    $"Change {string.Join(", ", changes)} on:\n\n{names}\n\nThis edits the textures' import settings (shared by every material using them).", "Apply", "Cancel"))
                return;
            ApplyImportSettings(targets.Select(t => t.Importer), importer =>
            {
                if (applyMaxSize) importer.maxTextureSize = importMaxSize;
                if (applyCompression) importer.textureCompression = compression;
                if (applyCrunch)
                {
                    importer.crunchedCompression = true;
                    importer.compressionQuality = crunchQuality;
                }
                if (applyMipmaps) importer.mipmapEnabled = true;
                if (applyStreaming)
                {
                    importer.mipmapEnabled = true;
                    importer.streamingMipmaps = true;
                }
            });
            Refresh();
            GUIUtility.ExitGUI();
        }
    }
}
