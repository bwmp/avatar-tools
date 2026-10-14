using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using VRC.SDKBase.Validation.Performance;
using VRC.SDKBase.Validation.Performance.Stats;

namespace AvatarTools.Editor
{
    public class PerformanceRankPreview : EditorWindow
    {
        public class Report
        {
            public AvatarPerformanceStats Pc, Mobile;
            public float TextureMegabytes;
        }

        static readonly AvatarPerformanceCategory[] Categories = ((AvatarPerformanceCategory[])Enum.GetValues(typeof(AvatarPerformanceCategory)))
            .Where(c => c != AvatarPerformanceCategory.None && c != AvatarPerformanceCategory.Overall &&
                        c != AvatarPerformanceCategory.DownloadSize && c != AvatarPerformanceCategory.UncompressedSize &&
                        c != AvatarPerformanceCategory.AvatarPerformanceCategoryCount)
            .ToArray();

        const double RecalculateDelay = 0.75;

        VRCAvatarDescriptor avatar;
        Report report;
        bool autoRecalculate = true, showAll;
        double pendingSince = -1;
        Vector2 scroll;

        [MenuItem(AvatarToolsUtil.MenuRoot + "Optimization/Performance Rank Preview")]
        public static void Open()
        {
            var window = GetWindow<PerformanceRankPreview>("Performance Rank");
            window.avatar = AvatarToolsUtil.SelectedAvatar();
            window.Recalculate();
        }

        public static Report Calculate(GameObject root, VRCAvatarDescriptor descriptor = null)
        {
            var textureMegabytes = TextureAuditor.TotalMegabytes(TextureAuditor.Gather(root, descriptor));
            return new Report
            {
                Pc = CalculateFor(root, false, textureMegabytes),
                Mobile = CalculateFor(root, true, textureMegabytes),
                TextureMegabytes = textureMegabytes
            };
        }

        static AvatarPerformanceStats CalculateFor(GameObject root, bool mobile, float textureMegabytes)
        {
            var stats = new AvatarPerformanceStats(mobile);
            AvatarPerformance.CalculatePerformanceStats(root.name, root, stats, mobile);
            if (stats.textureMegabytes == null)
            {
                stats.textureMegabytes = textureMegabytes;
                stats.CalculateAllPerformanceRatings(mobile);
            }
            return stats;
        }

        public static string FormatValue(AvatarPerformanceStats stats, AvatarPerformanceCategory category)
        {
            string N(int? v) => v?.ToString() ?? "-";
            string B(bool? v) => v == null ? "-" : v.Value ? "Yes" : "No";
            var pb = stats.physBone;
            switch (category)
            {
                case AvatarPerformanceCategory.PolyCount: return N(stats.polyCount);
                case AvatarPerformanceCategory.AABB: return stats.aabb == null ? "-" : stats.aabb.Value.size.ToString("0.00");
                case AvatarPerformanceCategory.SkinnedMeshCount: return N(stats.skinnedMeshCount);
                case AvatarPerformanceCategory.MeshCount: return N(stats.meshCount);
                case AvatarPerformanceCategory.MaterialCount: return N(stats.materialCount);
                case AvatarPerformanceCategory.PhysBoneComponentCount: return N(pb?.componentCount);
                case AvatarPerformanceCategory.PhysBoneTransformCount: return N(pb?.transformCount);
                case AvatarPerformanceCategory.PhysBoneColliderCount: return N(pb?.colliderCount);
                case AvatarPerformanceCategory.PhysBoneCollisionCheckCount: return N(pb?.collisionCheckCount);
                case AvatarPerformanceCategory.ContactCount: return N(stats.contactCount);
                case AvatarPerformanceCategory.AnimatorCount: return N(stats.animatorCount);
                case AvatarPerformanceCategory.BoneCount: return N(stats.boneCount);
                case AvatarPerformanceCategory.LightCount: return N(stats.lightCount);
                case AvatarPerformanceCategory.ParticleSystemCount: return N(stats.particleSystemCount);
                case AvatarPerformanceCategory.ParticleTotalCount: return N(stats.particleTotalCount);
                case AvatarPerformanceCategory.ParticleMaxMeshPolyCount: return N(stats.particleMaxMeshPolyCount);
                case AvatarPerformanceCategory.ParticleTrailsEnabled: return B(stats.particleTrailsEnabled);
                case AvatarPerformanceCategory.ParticleCollisionEnabled: return B(stats.particleCollisionEnabled);
                case AvatarPerformanceCategory.TrailRendererCount: return N(stats.trailRendererCount);
                case AvatarPerformanceCategory.LineRendererCount: return N(stats.lineRendererCount);
                case AvatarPerformanceCategory.ClothCount: return N(stats.clothCount);
                case AvatarPerformanceCategory.ClothMaxVertices: return N(stats.clothMaxVertices);
                case AvatarPerformanceCategory.PhysicsColliderCount: return N(stats.physicsColliderCount);
                case AvatarPerformanceCategory.PhysicsRigidbodyCount: return N(stats.physicsRigidbodyCount);
                case AvatarPerformanceCategory.AudioSourceCount: return N(stats.audioSourceCount);
                case AvatarPerformanceCategory.TextureMegabytes: return stats.textureMegabytes == null ? "-" : stats.textureMegabytes.Value.ToString("0.0") + " MB";
                case AvatarPerformanceCategory.ConstraintsCount: return N(stats.constraintsCount);
                case AvatarPerformanceCategory.ConstraintDepth: return N(stats.constraintDepth);
                case AvatarPerformanceCategory.RaycastCount: return N(stats.raycastCount);
                default: return "-";
            }
        }

        public static string Suggestion(AvatarPerformanceCategory category)
        {
            switch (category)
            {
                case AvatarPerformanceCategory.PolyCount: return "Decimate meshes or delete hidden geometry; remove unused meshes.";
                case AvatarPerformanceCategory.AABB: return "Bounds too large: look for stray objects, particles or oversized renderer bounds.";
                case AvatarPerformanceCategory.SkinnedMeshCount:
                case AvatarPerformanceCategory.MeshCount: return "Merge meshes -> Mesh Merger.";
                case AvatarPerformanceCategory.MaterialCount: return "Reduce material slots: atlas textures, merge submeshes sharing a material (Mesh Merger).";
                case AvatarPerformanceCategory.PhysBoneComponentCount: return "Combine PhysBone chains under shared roots; remove PhysBones on small details.";
                case AvatarPerformanceCategory.PhysBoneTransformCount: return "Use fewer bones per chain (ignore transforms / end bones).";
                case AvatarPerformanceCategory.PhysBoneColliderCount:
                case AvatarPerformanceCategory.PhysBoneCollisionCheckCount: return "Reduce PhysBone colliders and the chains they collide with.";
                case AvatarPerformanceCategory.ContactCount: return "Remove unused contact senders/receivers.";
                case AvatarPerformanceCategory.AnimatorCount: return "Remove extra Animator components below the avatar root.";
                case AvatarPerformanceCategory.BoneCount: return "Remove unused bones -> Unused Blendshape / Bone Remover.";
                case AvatarPerformanceCategory.LightCount: return "Remove lights.";
                case AvatarPerformanceCategory.ParticleSystemCount:
                case AvatarPerformanceCategory.ParticleTotalCount:
                case AvatarPerformanceCategory.ParticleMaxMeshPolyCount: return "Reduce particle systems, max particles and particle mesh complexity.";
                case AvatarPerformanceCategory.ParticleTrailsEnabled:
                case AvatarPerformanceCategory.ParticleCollisionEnabled: return "Disable particle trails / collision modules.";
                case AvatarPerformanceCategory.TrailRendererCount:
                case AvatarPerformanceCategory.LineRendererCount: return "Remove trail / line renderers.";
                case AvatarPerformanceCategory.ClothCount:
                case AvatarPerformanceCategory.ClothMaxVertices: return "Replace Cloth with PhysBones.";
                case AvatarPerformanceCategory.PhysicsColliderCount:
                case AvatarPerformanceCategory.PhysicsRigidbodyCount: return "Remove Unity physics colliders / rigidbodies.";
                case AvatarPerformanceCategory.AudioSourceCount: return "Remove audio sources.";
                case AvatarPerformanceCategory.TextureMegabytes: return "Texture memory -> Texture Auditor (lower max size, compress, crunch).";
                case AvatarPerformanceCategory.ConstraintsCount:
                case AvatarPerformanceCategory.ConstraintDepth: return "Reduce constraints and constraint chains.";
                case AvatarPerformanceCategory.RaycastCount: return "Remove unused VRC Raycast components.";
                default: return "";
            }
        }

        static Action ToolFor(AvatarPerformanceCategory category)
        {
            switch (category)
            {
                case AvatarPerformanceCategory.SkinnedMeshCount:
                case AvatarPerformanceCategory.MeshCount:
                case AvatarPerformanceCategory.MaterialCount: return MeshMerger.Open;
                case AvatarPerformanceCategory.BoneCount: return UnusedBlendshapeBoneRemover.Open;
                case AvatarPerformanceCategory.TextureMegabytes: return TextureAuditor.Open;
                default: return null;
            }
        }

        void OnEnable()
        {
            EditorApplication.hierarchyChanged += MarkDirty;
            Undo.undoRedoPerformed += MarkDirty;
            EditorApplication.update += Tick;
        }

        void OnDisable()
        {
            EditorApplication.hierarchyChanged -= MarkDirty;
            Undo.undoRedoPerformed -= MarkDirty;
            EditorApplication.update -= Tick;
        }

        void MarkDirty()
        {
            if (autoRecalculate) pendingSince = EditorApplication.timeSinceStartup;
        }

        void Tick()
        {
            if (pendingSince < 0 || EditorApplication.timeSinceStartup - pendingSince < RecalculateDelay) return;
            pendingSince = -1;
            Recalculate();
            Repaint();
        }

        void Recalculate()
        {
            report = avatar == null ? null : Calculate(avatar.gameObject, avatar);
        }

        void OnGUI()
        {
            EditorGUI.BeginChangeCheck();
            avatar = AvatarToolsUtil.AvatarField(avatar);
            if (EditorGUI.EndChangeCheck()) Recalculate();
            if (avatar == null)
            {
                EditorGUILayout.HelpBox("Select an avatar with a VRCAvatarDescriptor.", MessageType.Info);
                return;
            }
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Recalculate", GUILayout.Height(24))) Recalculate();
                autoRecalculate = GUILayout.Toggle(autoRecalculate, "Auto (on hierarchy change)", GUILayout.ExpandWidth(false));
                showAll = GUILayout.Toggle(showAll, "Show all categories", GUILayout.ExpandWidth(false));
            }
            if (report == null) return;

            var pcOverall = report.Pc.GetPerformanceRatingForCategory(AvatarPerformanceCategory.Overall);
            var mobileOverall = report.Mobile.GetPerformanceRatingForCategory(AvatarPerformanceCategory.Overall);
            using (new EditorGUILayout.HorizontalScope())
            {
                RatingLabel("PC: " + pcOverall, pcOverall, EditorStyles.boldLabel);
                RatingLabel("Quest / Mobile: " + mobileOverall, mobileOverall, EditorStyles.boldLabel);
            }
            EditorGUILayout.HelpBox("Texture memory is this tool's estimate (see Texture Auditor); download size is only known after a build. " +
                                    "Highlighted rows decide the overall rank.", MessageType.None);

            scroll = EditorGUILayout.BeginScrollView(scroll);
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Label("Category", EditorStyles.miniBoldLabel, GUILayout.Width(190));
                GUILayout.Label("PC", EditorStyles.miniBoldLabel, GUILayout.Width(150));
                GUILayout.Label("Mobile", EditorStyles.miniBoldLabel, GUILayout.Width(150));
                GUILayout.Label("Suggestion", EditorStyles.miniBoldLabel);
            }
            foreach (var category in Categories)
            {
                var pc = report.Pc.GetPerformanceRatingForCategory(category);
                var mobile = report.Mobile.GetPerformanceRatingForCategory(category);
                var decisivePc = pc == pcOverall && pc > PerformanceRating.Excellent;
                var decisiveMobile = mobile == mobileOverall && mobile > PerformanceRating.Excellent;
                var bad = pc >= PerformanceRating.Medium || mobile >= PerformanceRating.Medium;
                if (!showAll && !bad && !decisivePc && !decisiveMobile) continue;
                var rowStyle = decisivePc || decisiveMobile ? EditorStyles.helpBox : GUIStyle.none;
                using (new EditorGUILayout.HorizontalScope(rowStyle))
                {
                    GUILayout.Label(AvatarPerformanceStats.GetPerformanceCategoryDisplayName(category),
                        decisivePc || decisiveMobile ? EditorStyles.boldLabel : EditorStyles.label, GUILayout.Width(190));
                    RatingLabel($"{FormatValue(report.Pc, category)}  {pc}", pc, EditorStyles.label, GUILayout.Width(150));
                    RatingLabel($"{FormatValue(report.Mobile, category)}  {mobile}", mobile, EditorStyles.label, GUILayout.Width(150));
                    if (bad || decisivePc || decisiveMobile)
                    {
                        GUILayout.Label(Suggestion(category), EditorStyles.wordWrappedMiniLabel);
                        var tool = ToolFor(category);
                        if (tool != null && GUILayout.Button("Open", GUILayout.Width(50))) tool();
                    }
                    else GUILayout.FlexibleSpace();
                }
            }
            EditorGUILayout.EndScrollView();
        }

        static void RatingLabel(string text, PerformanceRating rating, GUIStyle style, params GUILayoutOption[] options)
        {
            var previous = GUI.contentColor;
            GUI.contentColor = RatingColor(rating);
            GUILayout.Label(text, style, options);
            GUI.contentColor = previous;
        }

        public static Color RatingColor(PerformanceRating rating)
        {
            switch (rating)
            {
                case PerformanceRating.Excellent: return new Color(0.45f, 0.85f, 0.45f);
                case PerformanceRating.Good: return new Color(0.6f, 0.85f, 0.4f);
                case PerformanceRating.Medium: return new Color(0.95f, 0.8f, 0.3f);
                case PerformanceRating.Poor: return new Color(1f, 0.55f, 0.25f);
                case PerformanceRating.VeryPoor: return new Color(1f, 0.35f, 0.35f);
                default: return GUI.contentColor;
            }
        }
    }
}
