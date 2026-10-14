using System;
using System.Collections.Generic;
using System.Linq;
using Unity.Collections;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using VRC.SDK3.Avatars.Components;

namespace AvatarTools.Editor
{
    public class MeshMerger : EditorWindow
    {
        public class Options
        {
            public string Name = "Merged Mesh";
            public bool MergeSameMaterial = true;
            public bool DeleteSources;
        }

        public class Result
        {
            public SkinnedMeshRenderer Renderer;
            public Mesh Mesh;
            public readonly List<string> Warnings = new List<string>();
            public readonly List<string> SourcePaths = new List<string>();
            public readonly HashSet<string> AmbiguousBlendshapes = new HashSet<string>();
        }

        public class RewriteReport
        {
            public int Rewritten;
            public readonly List<string> Skipped = new List<string>();
            public readonly HashSet<AnimationClip> Clips = new HashSet<AnimationClip>();
            public int OtherBindings;
        }

        internal struct Frame
        {
            public float Weight;
            public Vector3[] Vertices, Normals, Tangents;
        }

        class Source
        {
            public Renderer Renderer;
            public Mesh Mesh;
            public bool Rigid;
            public Matrix4x4 ToTarget;
            public int[] BoneMap;
            public int RigidBone = -1;
            public int VertexOffset;
        }

        VRCAvatarDescriptor avatar;
        readonly Dictionary<Renderer, bool> picked = new Dictionary<Renderer, bool>();
        bool includeMeshRenderers;
        readonly Options options = new Options();
        Result lastResult;
        RewriteReport lastRewrite;
        Vector2 scroll;

        [MenuItem(AvatarToolsUtil.MenuRoot + "Optimization/Mesh Merger")]
        public static void Open()
        {
            var window = GetWindow<MeshMerger>("Mesh Merger");
            window.avatar = AvatarToolsUtil.SelectedAvatar();
        }

        static Mesh MeshOf(Renderer renderer)
        {
            if (renderer is SkinnedMeshRenderer smr) return smr.sharedMesh;
            var filter = renderer.GetComponent<MeshFilter>();
            return renderer is MeshRenderer && filter != null ? filter.sharedMesh : null;
        }

        static bool ApproximatelyEqual(Matrix4x4 a, Matrix4x4 b)
        {
            for (var i = 0; i < 16; i++)
                if (Mathf.Abs(a[i] - b[i]) > 1e-4f) return false;
            return true;
        }

        internal static List<Frame> ReadFrames(Mesh mesh, int shape)
        {
            var frames = new List<Frame>();
            for (var f = 0; f < mesh.GetBlendShapeFrameCount(shape); f++)
            {
                var frame = new Frame
                {
                    Weight = mesh.GetBlendShapeFrameWeight(shape, f),
                    Vertices = new Vector3[mesh.vertexCount], Normals = new Vector3[mesh.vertexCount], Tangents = new Vector3[mesh.vertexCount]
                };
                mesh.GetBlendShapeFrameVertices(shape, f, frame.Vertices, frame.Normals, frame.Tangents);
                frames.Add(frame);
            }
            return frames;
        }

        internal static void SampleFrames(List<Frame> frames, float weight, int index, out Vector3 vertex, out Vector3 normal, out Vector3 tangent)
        {
            vertex = normal = tangent = Vector3.zero;
            if (frames.Count == 0 || weight == 0) return;
            var first = frames[0];
            if (weight <= first.Weight || frames.Count == 1)
            {
                var t = first.Weight == 0 ? 1 : weight / first.Weight;
                vertex = first.Vertices[index] * t;
                normal = first.Normals[index] * t;
                tangent = first.Tangents[index] * t;
                return;
            }
            for (var f = 0; f < frames.Count - 1; f++)
            {
                var a = frames[f];
                var b = frames[f + 1];
                if (weight > b.Weight && f + 1 < frames.Count - 1) continue;
                var t = Mathf.Clamp01((weight - a.Weight) / (b.Weight - a.Weight));
                vertex = Vector3.Lerp(a.Vertices[index], b.Vertices[index], t);
                normal = Vector3.Lerp(a.Normals[index], b.Normals[index], t);
                tangent = Vector3.Lerp(a.Tangents[index], b.Tangents[index], t);
                return;
            }
        }

        public static Result Merge(VRCAvatarDescriptor avatar, IList<Renderer> renderers, Options options)
        {
            var result = new Result();
            var root = avatar.transform;
            var targetToWorld = root.localToWorldMatrix;
            var worldToTarget = targetToWorld.inverse;
            var bones = new List<Transform>();
            var bindPoses = new List<Matrix4x4>();

            int BoneIndex(Transform bone, Matrix4x4 bind)
            {
                for (var i = 0; i < bones.Count; i++)
                    if (bones[i] == bone && ApproximatelyEqual(bindPoses[i], bind)) return i;
                bones.Add(bone);
                bindPoses.Add(bind);
                return bones.Count - 1;
            }

            var sources = new List<Source>();
            var vertexCount = 0;
            foreach (var renderer in renderers)
            {
                var mesh = MeshOf(renderer);
                if (mesh == null)
                {
                    result.Warnings.Add($"Skipped {renderer.name}: no mesh.");
                    continue;
                }
                var smr = renderer as SkinnedMeshRenderer;
                var rigid = smr == null || smr.bones.Length == 0 || mesh.GetBonesPerVertex().Length == 0;
                var source = new Source
                {
                    Renderer = renderer, Mesh = mesh, VertexOffset = vertexCount, Rigid = rigid,
                    ToTarget = worldToTarget * (rigid ? renderer.transform.localToWorldMatrix : SkinnedMeshToWorld(smr, mesh))
                };
                var fromTarget = source.ToTarget.inverse;
                if (source.Rigid)
                    source.RigidBone = BoneIndex(renderer.transform, fromTarget);
                else
                {
                    var sourceBones = smr.bones;
                    var sourceBinds = mesh.bindposes;
                    source.BoneMap = new int[sourceBones.Length];
                    var missing = 0;
                    for (var i = 0; i < sourceBones.Length; i++)
                    {
                        if (sourceBones[i] == null || i >= sourceBinds.Length)
                        {
                            missing++;
                            var fallback = smr.rootBone != null ? smr.rootBone : renderer.transform;
                            source.BoneMap[i] = BoneIndex(fallback, fallback.worldToLocalMatrix * targetToWorld);
                            continue;
                        }
                        source.BoneMap[i] = BoneIndex(sourceBones[i], sourceBinds[i] * fromTarget);
                    }
                    if (missing > 0) result.Warnings.Add($"{renderer.name}: {missing} missing bone(s) were bound to {(smr.rootBone != null ? smr.rootBone.name : renderer.name)}.");
                }
                if (renderer.sharedMaterials.Length > mesh.subMeshCount)
                    result.Warnings.Add($"{renderer.name}: {renderer.sharedMaterials.Length - mesh.subMeshCount} extra material slot(s) beyond the submesh count were dropped.");
                sources.Add(source);
                vertexCount += mesh.vertexCount;
                result.SourcePaths.Add(AvatarToolsUtil.GetPath(root, renderer.transform));
            }
            if (sources.Count == 0) throw new InvalidOperationException("No renderers with meshes to merge.");

            var merged = new Mesh { name = options.Name, indexFormat = vertexCount > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16 };
            WriteVertices(merged, sources, vertexCount);
            var materials = WriteSubmeshes(merged, sources, options.MergeSameMaterial);
            WriteBoneWeights(merged, sources, vertexCount);
            merged.bindposes = bindPoses.ToArray();
            var weights = WriteBlendshapes(merged, sources, vertexCount, result);
            merged.RecalculateBounds();

            Undo.IncrementCurrentGroup();
            var group = Undo.GetCurrentGroup();
            AvatarToolsUtil.CreateAsset(merged, AvatarToolsUtil.AvatarFolder(avatar, "Meshes"), options.Name, "asset");
            var first = sources[0].Renderer;
            var go = new GameObject(options.Name) { layer = first.gameObject.layer, tag = first.gameObject.tag };
            go.transform.SetParent(root, false);
            Undo.RegisterCreatedObjectUndo(go, "Merge Meshes");
            var target = go.AddComponent<SkinnedMeshRenderer>();
            target.sharedMesh = merged;
            target.bones = bones.ToArray();
            target.sharedMaterials = materials;
            var firstSkinned = sources.Select(s => s.Renderer).OfType<SkinnedMeshRenderer>().FirstOrDefault();
            target.rootBone = firstSkinned != null && firstSkinned.rootBone != null ? firstSkinned.rootBone : first.transform;
            CopyRendererSettings(first, target);
            if (firstSkinned != null)
            {
                target.quality = firstSkinned.quality;
                target.updateWhenOffscreen = firstSkinned.updateWhenOffscreen;
                target.skinnedMotionVectors = firstSkinned.skinnedMotionVectors;
            }
            target.localBounds = CombinedBounds(sources, target.rootBone);
            for (var i = 0; i < weights.Count; i++) target.SetBlendShapeWeight(i, weights[i]);
            result.Renderer = target;
            result.Mesh = merged;

            var boneSet = new HashSet<Transform>(bones);
            foreach (var source in sources)
            {
                var usedAsBone = boneSet.Contains(source.Renderer.transform);
                if (usedAsBone && !options.DeleteSources)
                    result.Warnings.Add($"{source.Renderer.name} is now a bone of the merged mesh, so only its renderer was disabled; disabled renderers still count toward performance stats.");
                RetireSource(source.Renderer, options.DeleteSources, usedAsBone);
            }
            Undo.CollapseUndoOperations(group);
            AssetDatabase.SaveAssets();
            return result;
        }

        // Skinned vertices ignore the renderer transform; placing the mesh where its bones put it keeps
        // bind poses identical across meshes that share a skeleton, so their bones deduplicate.
        static Matrix4x4 SkinnedMeshToWorld(SkinnedMeshRenderer smr, Mesh mesh)
        {
            var bones = smr.bones;
            var binds = mesh.bindposes;
            var anchor = smr.rootBone != null ? Array.IndexOf(bones, smr.rootBone) : -1;
            if (anchor < 0 || anchor >= binds.Length) anchor = Array.FindIndex(bones, b => b != null);
            if (anchor < 0 || anchor >= binds.Length) return smr.transform.localToWorldMatrix;
            return bones[anchor].localToWorldMatrix * binds[anchor];
        }

        static void CopyRendererSettings(Renderer from, Renderer to)
        {
            to.shadowCastingMode = from.shadowCastingMode;
            to.receiveShadows = from.receiveShadows;
            to.lightProbeUsage = from.lightProbeUsage;
            to.reflectionProbeUsage = from.reflectionProbeUsage;
            to.probeAnchor = from.probeAnchor;
            to.allowOcclusionWhenDynamic = from.allowOcclusionWhenDynamic;
            to.motionVectorGenerationMode = from.motionVectorGenerationMode;
        }

        static Bounds CombinedBounds(List<Source> sources, Transform space)
        {
            var bounds = new Bounds();
            var initialised = false;
            foreach (var source in sources)
            {
                var world = source.Renderer.bounds;
                for (var corner = 0; corner < 8; corner++)
                {
                    var point = world.center + Vector3.Scale(world.extents, new Vector3((corner & 1) == 0 ? -1 : 1, (corner & 2) == 0 ? -1 : 1, (corner & 4) == 0 ? -1 : 1));
                    var local = space.InverseTransformPoint(point);
                    if (!initialised)
                    {
                        bounds = new Bounds(local, Vector3.zero);
                        initialised = true;
                    }
                    else bounds.Encapsulate(local);
                }
            }
            return bounds;
        }

        static void RetireSource(Renderer renderer, bool delete, bool usedAsBone)
        {
            var go = renderer.gameObject;
            var standalone = !usedAsBone && go.transform.childCount == 0 &&
                             go.GetComponents<Component>().All(c => c is Transform || c is Renderer || c is MeshFilter);
            if (delete)
            {
                if (standalone)
                {
                    Undo.DestroyObjectImmediate(go);
                    return;
                }
                var filter = renderer.GetComponent<MeshFilter>();
                Undo.DestroyObjectImmediate(renderer);
                if (filter != null) Undo.DestroyObjectImmediate(filter);
                return;
            }
            if (standalone)
            {
                Undo.RecordObject(go, "Disable Merged Source");
                go.SetActive(false);
                go.tag = "EditorOnly";
            }
            else
            {
                Undo.RecordObject(renderer, "Disable Merged Source");
                renderer.enabled = false;
            }
        }

        static void WriteVertices(Mesh merged, List<Source> sources, int vertexCount)
        {
            bool Any(VertexAttribute attribute) => sources.Any(s => s.Mesh.HasVertexAttribute(attribute));
            bool hasNormals = Any(VertexAttribute.Normal), hasTangents = Any(VertexAttribute.Tangent), hasColors = Any(VertexAttribute.Color);
            var uvDimensions = Enumerable.Range(0, 4).Select(c => sources.Max(s =>
                s.Mesh.HasVertexAttribute(VertexAttribute.TexCoord0 + c) ? s.Mesh.GetVertexAttributeDimension(VertexAttribute.TexCoord0 + c) : 0)).ToArray();

            var positions = new List<Vector3>(vertexCount);
            var normals = new List<Vector3>(vertexCount);
            var tangents = new List<Vector4>(vertexCount);
            var colors = new List<Color>(vertexCount);
            var uvs = Enumerable.Range(0, 4).Select(_ => new List<Vector4>(vertexCount)).ToArray();
            var buffer3 = new List<Vector3>();
            var buffer4 = new List<Vector4>();
            var bufferColor = new List<Color>();
            foreach (var source in sources)
            {
                var mesh = source.Mesh;
                var count = mesh.vertexCount;
                var normalMatrix = source.ToTarget.inverse.transpose;
                var handedness = source.ToTarget.determinant < 0 ? -1f : 1f;
                mesh.GetVertices(buffer3);
                positions.AddRange(buffer3.Select(p => source.ToTarget.MultiplyPoint3x4(p)));
                if (hasNormals)
                {
                    mesh.GetNormals(buffer3);
                    if (buffer3.Count == count) normals.AddRange(buffer3.Select(n => normalMatrix.MultiplyVector(n).normalized));
                    else normals.AddRange(Enumerable.Repeat(Vector3.up, count));
                }
                if (hasTangents)
                {
                    mesh.GetTangents(buffer4);
                    if (buffer4.Count == count)
                        tangents.AddRange(buffer4.Select(t =>
                        {
                            var v = source.ToTarget.MultiplyVector(new Vector3(t.x, t.y, t.z)).normalized;
                            return new Vector4(v.x, v.y, v.z, t.w * handedness);
                        }));
                    else tangents.AddRange(Enumerable.Repeat(new Vector4(1, 0, 0, 1), count));
                }
                if (hasColors)
                {
                    mesh.GetColors(bufferColor);
                    colors.AddRange(bufferColor.Count == count ? bufferColor : Enumerable.Repeat(Color.white, count));
                }
                for (var channel = 0; channel < 4; channel++)
                {
                    if (uvDimensions[channel] == 0) continue;
                    mesh.GetUVs(channel, buffer4);
                    uvs[channel].AddRange(buffer4.Count == count ? buffer4 : Enumerable.Repeat(Vector4.zero, count));
                }
            }
            merged.SetVertices(positions);
            if (hasNormals) merged.SetNormals(normals);
            if (hasTangents) merged.SetTangents(tangents);
            if (hasColors) merged.SetColors(colors);
            for (var channel = 0; channel < 4; channel++)
            {
                switch (uvDimensions[channel])
                {
                    case 0: break;
                    case 1:
                    case 2: merged.SetUVs(channel, uvs[channel].Select(v => (Vector2)v).ToList()); break;
                    case 3: merged.SetUVs(channel, uvs[channel].Select(v => (Vector3)v).ToList()); break;
                    default: merged.SetUVs(channel, uvs[channel]); break;
                }
            }
        }

        static Material[] WriteSubmeshes(Mesh merged, List<Source> sources, bool mergeSameMaterial)
        {
            var groups = new List<(Material material, MeshTopology topology, List<int> indices)>();
            foreach (var source in sources)
            {
                var mesh = source.Mesh;
                var materials = source.Renderer.sharedMaterials;
                var flip = source.ToTarget.determinant < 0;
                for (var sub = 0; sub < mesh.subMeshCount; sub++)
                {
                    var material = sub < materials.Length ? materials[sub] : null;
                    var topology = mesh.GetTopology(sub);
                    var indices = mesh.GetIndices(sub);
                    for (var i = 0; i < indices.Length; i++) indices[i] += source.VertexOffset;
                    if (flip && topology == MeshTopology.Triangles)
                        for (var i = 0; i + 2 < indices.Length; i += 3)
                            (indices[i + 1], indices[i + 2]) = (indices[i + 2], indices[i + 1]);
                    var index = mergeSameMaterial && material != null
                        ? groups.FindIndex(g => g.material == material && g.topology == topology)
                        : -1;
                    if (index < 0)
                    {
                        groups.Add((material, topology, new List<int>()));
                        index = groups.Count - 1;
                    }
                    groups[index].indices.AddRange(indices);
                }
            }
            merged.subMeshCount = groups.Count;
            for (var i = 0; i < groups.Count; i++)
                merged.SetIndices(groups[i].indices, groups[i].topology, i);
            return groups.Select(g => g.material).ToArray();
        }

        static void WriteBoneWeights(Mesh merged, List<Source> sources, int vertexCount)
        {
            var perVertex = new List<byte>(vertexCount);
            var weights = new List<BoneWeight1>(vertexCount * 4);
            var scratch = new List<BoneWeight1>();
            foreach (var source in sources)
            {
                if (source.Rigid)
                {
                    for (var v = 0; v < source.Mesh.vertexCount; v++)
                    {
                        perVertex.Add(1);
                        weights.Add(new BoneWeight1 { boneIndex = source.RigidBone, weight = 1 });
                    }
                    continue;
                }
                var sourcePerVertex = source.Mesh.GetBonesPerVertex();
                var sourceWeights = source.Mesh.GetAllBoneWeights();
                var cursor = 0;
                for (var v = 0; v < sourcePerVertex.Length; v++)
                {
                    scratch.Clear();
                    for (var k = 0; k < sourcePerVertex[v]; k++)
                    {
                        var weight = sourceWeights[cursor++];
                        if (weight.weight <= 0 || weight.boneIndex >= source.BoneMap.Length) continue;
                        var mapped = source.BoneMap[weight.boneIndex];
                        var existing = scratch.FindIndex(w => w.boneIndex == mapped);
                        if (existing >= 0)
                            scratch[existing] = new BoneWeight1 { boneIndex = mapped, weight = scratch[existing].weight + weight.weight };
                        else scratch.Add(new BoneWeight1 { boneIndex = mapped, weight = weight.weight });
                    }
                    if (scratch.Count == 0) scratch.Add(new BoneWeight1 { boneIndex = source.BoneMap.Length > 0 ? source.BoneMap[0] : 0, weight = 1 });
                    scratch.Sort((a, b) => b.weight.CompareTo(a.weight));
                    perVertex.Add((byte)scratch.Count);
                    weights.AddRange(scratch);
                }
            }
            using (var nativePerVertex = new NativeArray<byte>(perVertex.ToArray(), Allocator.Temp))
            using (var nativeWeights = new NativeArray<BoneWeight1>(weights.ToArray(), Allocator.Temp))
                merged.SetBoneWeights(nativePerVertex, nativeWeights);
        }

        static List<float> WriteBlendshapes(Mesh merged, List<Source> sources, int vertexCount, Result result)
        {
            var names = new List<string>();
            var owners = new Dictionary<string, List<Source>>();
            foreach (var source in sources)
            for (var s = 0; s < source.Mesh.blendShapeCount; s++)
            {
                var name = source.Mesh.GetBlendShapeName(s);
                if (!owners.TryGetValue(name, out var list))
                {
                    owners[name] = list = new List<Source>();
                    names.Add(name);
                }
                list.Add(source);
            }
            var weights = new List<float>();
            var deltaVertices = new Vector3[vertexCount];
            var deltaNormals = new Vector3[vertexCount];
            var deltaTangents = new Vector3[vertexCount];
            foreach (var name in names)
            {
                var list = owners[name];
                if (list.Count > 1) result.AmbiguousBlendshapes.Add(name);
                var framesBySource = list.ToDictionary(s => s, s => ReadFrames(s.Mesh, s.Mesh.GetBlendShapeIndex(name)));
                var reference = framesBySource[list[0]];
                if (list.Skip(1).Any(s => !framesBySource[s].Select(f => f.Weight).SequenceEqual(reference.Select(f => f.Weight))))
                    result.Warnings.Add($"Blendshape '{name}' has different frame weights between meshes; resampled to the first mesh's frames.");
                foreach (var frame in reference)
                {
                    Array.Clear(deltaVertices, 0, vertexCount);
                    Array.Clear(deltaNormals, 0, vertexCount);
                    Array.Clear(deltaTangents, 0, vertexCount);
                    foreach (var source in list)
                    {
                        var frames = framesBySource[source];
                        var normalMatrix = source.ToTarget.inverse.transpose;
                        for (var v = 0; v < source.Mesh.vertexCount; v++)
                        {
                            SampleFrames(frames, frame.Weight, v, out var dv, out var dn, out var dt);
                            deltaVertices[source.VertexOffset + v] = source.ToTarget.MultiplyVector(dv);
                            deltaNormals[source.VertexOffset + v] = normalMatrix.MultiplyVector(dn);
                            deltaTangents[source.VertexOffset + v] = source.ToTarget.MultiplyVector(dt);
                        }
                    }
                    merged.AddBlendShapeFrame(name, frame.Weight, deltaVertices, deltaNormals, deltaTangents);
                }
                var ownerWeights = list.Select(s => s.Renderer is SkinnedMeshRenderer smr ? smr.GetBlendShapeWeight(s.Mesh.GetBlendShapeIndex(name)) : 0f).ToList();
                if (ownerWeights.Distinct().Count() > 1)
                    result.Warnings.Add($"Blendshape '{name}' had different weights on the merged meshes; using {ownerWeights[0]} from {list[0].Renderer.name}.");
                weights.Add(ownerWeights[0]);
            }
            return weights;
        }

        public static RewriteReport RewriteBlendshapeBindings(VRCAvatarDescriptor avatar, Result result)
        {
            var report = new RewriteReport();
            var newPath = AvatarToolsUtil.GetPath(avatar.transform, result.Renderer.transform);
            var sourcePaths = new HashSet<string>(result.SourcePaths);
            foreach (var clip in AvatarToolsUtil.AllControllers(avatar).SelectMany(AvatarToolsUtil.AllClips).Distinct())
            {
                var editable = AssetDatabase.GetAssetPath(clip).StartsWith("Assets/", StringComparison.Ordinal);
                var bindings = AnimationUtility.GetCurveBindings(clip);
                foreach (var binding in bindings)
                {
                    if (!sourcePaths.Contains(binding.path)) continue;
                    if (binding.type != typeof(SkinnedMeshRenderer) || !binding.propertyName.StartsWith("blendShape.", StringComparison.Ordinal))
                    {
                        report.OtherBindings++;
                        continue;
                    }
                    var shape = binding.propertyName.Substring("blendShape.".Length);
                    var moved = binding;
                    moved.path = newPath;
                    string reason = null;
                    if (!editable) reason = "clip is read-only";
                    else if (result.AmbiguousBlendshapes.Contains(shape)) reason = "name exists on several merged meshes";
                    else if (result.Mesh.GetBlendShapeIndex(shape) < 0) reason = "blendshape not on the merged mesh";
                    else if (AnimationUtility.GetEditorCurve(clip, moved) != null) reason = "clip already animates it on the merged mesh";
                    if (reason != null)
                    {
                        report.Skipped.Add($"{clip.name}: {binding.path} {shape} ({reason})");
                        continue;
                    }
                    Undo.RecordObject(clip, "Rewrite Blendshape Bindings");
                    var curve = AnimationUtility.GetEditorCurve(clip, binding);
                    AnimationUtility.SetEditorCurve(clip, moved, curve);
                    AnimationUtility.SetEditorCurve(clip, binding, null);
                    EditorUtility.SetDirty(clip);
                    report.Rewritten++;
                    report.Clips.Add(clip);
                }
                foreach (var binding in AnimationUtility.GetObjectReferenceCurveBindings(clip))
                    if (sourcePaths.Contains(binding.path)) report.OtherBindings++;
            }
            AssetDatabase.SaveAssets();
            return report;
        }

        void OnGUI()
        {
            EditorGUI.BeginChangeCheck();
            avatar = AvatarToolsUtil.AvatarField(avatar);
            if (EditorGUI.EndChangeCheck())
            {
                picked.Clear();
                lastResult = null;
                lastRewrite = null;
            }
            if (avatar == null)
            {
                EditorGUILayout.HelpBox("Select an avatar with a VRCAvatarDescriptor.", MessageType.Info);
                return;
            }
            includeMeshRenderers = EditorGUILayout.ToggleLeft("Include MeshRenderers (merged as rigidly skinned to their transform)", includeMeshRenderers);
            var candidates = avatar.GetComponentsInChildren<Renderer>(true)
                .Where(r => (r is SkinnedMeshRenderer || includeMeshRenderers && r is MeshRenderer) && MeshOf(r) != null).ToList();

            scroll = EditorGUILayout.BeginScrollView(scroll, GUILayout.MaxHeight(260));
            foreach (var renderer in candidates)
            {
                var mesh = MeshOf(renderer);
                picked.TryGetValue(renderer, out var on);
                var label = $"{AvatarToolsUtil.GetPath(avatar.transform, renderer.transform)}   ({mesh.vertexCount} verts, {mesh.subMeshCount} submeshes, {mesh.blendShapeCount} blendshapes{(renderer.gameObject.activeInHierarchy && renderer.enabled ? "" : ", disabled")})";
                picked[renderer] = EditorGUILayout.ToggleLeft(label, on);
            }
            EditorGUILayout.EndScrollView();

            var selection = candidates.Where(r => picked.TryGetValue(r, out var on) && on).ToList();
            options.Name = EditorGUILayout.TextField("Merged object name", options.Name);
            options.MergeSameMaterial = EditorGUILayout.Toggle("Merge submeshes sharing a material", options.MergeSameMaterial);
            options.DeleteSources = EditorGUILayout.Toggle("Delete sources (instead of disabling)", options.DeleteSources);

            if (selection.Count > 0)
            {
                var totalVertices = selection.Sum(r => MeshOf(r).vertexCount);
                var shapes = selection.SelectMany(r => Enumerable.Range(0, MeshOf(r).blendShapeCount).Select(i => MeshOf(r).GetBlendShapeName(i))).ToList();
                var duplicates = shapes.GroupBy(n => n).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
                EditorGUILayout.HelpBox($"Result: {totalVertices} vertices, {shapes.Distinct().Count()} blendshapes." +
                                        (duplicates.Count > 0 ? $"\nShared blendshape names (will drive all merged parts together): {string.Join(", ", duplicates.Take(10))}{(duplicates.Count > 10 ? "..." : "")}" : "") +
                                        "\nAnimations targeting the old renderers (paths, blendshapes, toggles) need remapping afterwards.", MessageType.Info);
            }
            using (new EditorGUI.DisabledScope(selection.Count < 2 || string.IsNullOrWhiteSpace(options.Name)))
            {
                if (GUILayout.Button($"Merge {selection.Count} Renderers", GUILayout.Height(28)) && ConfirmMerge(selection))
                {
                    lastResult = Merge(avatar, selection, options);
                    lastRewrite = null;
                    picked.Clear();
                    Selection.activeGameObject = lastResult.Renderer.gameObject;
                    GUIUtility.ExitGUI();
                }
            }
            DrawResult();
        }

        bool ConfirmMerge(List<Renderer> selection)
        {
            var list = string.Join("\n", selection.Select(r => AvatarToolsUtil.GetPath(avatar.transform, r.transform)));
            return EditorUtility.DisplayDialog("Merge Meshes",
                $"Merge into new object '{options.Name}':\n\n{list}\n\nA new mesh asset is written to {AvatarToolsUtil.GeneratedRoot}/{AvatarToolsUtil.SafeName(avatar.gameObject.name)}/Meshes. " +
                $"Sources will be {(options.DeleteSources ? "deleted" : "disabled")}. Source mesh assets are not modified.", "Merge", "Cancel");
        }

        void DrawResult()
        {
            if (lastResult == null || lastResult.Renderer == null) return;
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Last merge", EditorStyles.boldLabel);
            EditorGUILayout.ObjectField("Renderer", lastResult.Renderer, typeof(SkinnedMeshRenderer), true);
            EditorGUILayout.ObjectField("Mesh", lastResult.Mesh, typeof(Mesh), false);
            foreach (var warning in lastResult.Warnings) EditorGUILayout.HelpBox(warning, MessageType.Warning);
            if (lastRewrite == null)
            {
                if (GUILayout.Button("Rewrite Blendshape Animation Bindings to Merged Mesh") &&
                    EditorUtility.DisplayDialog("Rewrite Animation Bindings",
                        $"Move blendShape curves in this avatar's controller clips from\n{string.Join("\n", lastResult.SourcePaths)}\nto {AvatarToolsUtil.GetPath(avatar.transform, lastResult.Renderer.transform)}.\n\n" +
                        "Blendshape names present on several merged meshes are skipped. Clips are edited in place (Undo supported).", "Rewrite", "Cancel"))
                    lastRewrite = RewriteBlendshapeBindings(avatar, lastResult);
                return;
            }
            EditorGUILayout.HelpBox($"Rewrote {lastRewrite.Rewritten} curve(s) in {lastRewrite.Clips.Count} clip(s). " +
                                    $"{lastRewrite.OtherBindings} non-blendshape binding(s) still target the old paths.", MessageType.Info);
            foreach (var skipped in lastRewrite.Skipped.Take(20)) EditorGUILayout.LabelField(skipped, EditorStyles.miniLabel);
            if (lastRewrite.Skipped.Count > 20) EditorGUILayout.LabelField($"... and {lastRewrite.Skipped.Count - 20} more skipped", EditorStyles.miniLabel);
        }
    }
}
