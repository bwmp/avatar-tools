using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor.Animations;
using UnityEngine;
using VRC.Dynamics;
using VRC.SDK3.Avatars.Components;
using VRC.SDKBase;
using AnimLayerType = VRC.SDK3.Avatars.Components.VRCAvatarDescriptor.AnimLayerType;

namespace AvatarTools.Editor
{
    internal static class AnalysisUtil
    {
        internal static readonly HashSet<string> BuiltInParameters = new HashSet<string>
        {
            "IsLocal", "PreviewMode", "Viseme", "Voice", "GestureLeft", "GestureRight", "GestureLeftWeight",
            "GestureRightWeight", "AngularY", "VelocityX", "VelocityY", "VelocityZ", "VelocityMagnitude", "Upright",
            "Grounded", "Seated", "AFK", "TrackingType", "VRMode", "MuteSelf", "InStation", "Earmuffs",
            "IsOnFriendsList", "AvatarVersion", "IsAnimatorEnabled", "ScaleModified", "ScaleFactor",
            "ScaleFactorInverse", "EyeHeightAsMeters", "EyeHeightAsPercent",
            "VRCEmote", "VRCFaceBlendH", "VRCFaceBlendV"
        };

        static readonly string[] PhysBoneSuffixes =
        {
            VRCPhysBoneBase.PARAM_ISGRABBED, VRCPhysBoneBase.PARAM_ISPOSED, VRCPhysBoneBase.PARAM_ANGLE,
            VRCPhysBoneBase.PARAM_STRETCH, VRCPhysBoneBase.PARAM_SQUISH
        };

        internal static List<(AnimLayerType type, AnimatorController controller)> PlayableLayers(VRCAvatarDescriptor avatar)
        {
            var result = new List<(AnimLayerType, AnimatorController)>();
            var layers = (avatar.baseAnimationLayers ?? Array.Empty<VRCAvatarDescriptor.CustomAnimLayer>())
                .Concat(avatar.specialAnimationLayers ?? Array.Empty<VRCAvatarDescriptor.CustomAnimLayer>());
            foreach (var layer in layers)
            {
                if (layer.isDefault || !(layer.animatorController is AnimatorController controller)) continue;
                if (result.Any(r => r.Item2 == controller)) continue;
                result.Add((layer.type, controller));
            }
            return result;
        }

        internal static IEnumerable<AnimatorStateMachine> AllMachines(AnimatorStateMachine machine)
        {
            yield return machine;
            foreach (var child in machine.stateMachines)
            foreach (var nested in AllMachines(child.stateMachine))
                yield return nested;
        }

        internal static IEnumerable<(AnimatorState state, string path)> StatesWithPaths(AnimatorStateMachine machine, string prefix)
        {
            foreach (var child in machine.states)
                yield return (child.state, prefix + child.state.name);
            foreach (var sub in machine.stateMachines)
            foreach (var nested in StatesWithPaths(sub.stateMachine, prefix + sub.stateMachine.name + "/"))
                yield return nested;
        }

        internal static IEnumerable<AnimatorTransitionBase> MachineTransitions(AnimatorStateMachine machine)
        {
            foreach (var t in machine.anyStateTransitions) yield return t;
            foreach (var t in machine.entryTransitions) yield return t;
            foreach (var child in machine.stateMachines)
            foreach (var t in machine.GetStateMachineTransitions(child.stateMachine))
                yield return t;
        }

        internal static HashSet<string> ControllerReferences(AnimatorController controller)
        {
            var names = new HashSet<string>();
            foreach (var layer in controller.layers)
            {
                if (layer.stateMachine == null) continue;
                foreach (var machine in AllMachines(layer.stateMachine))
                {
                    foreach (var t in MachineTransitions(machine)) AddConditions(names, t);
                    foreach (var b in machine.behaviours) AddBehaviour(names, b);
                    foreach (var child in machine.states)
                    {
                        var state = child.state;
                        foreach (var t in state.transitions) AddConditions(names, t);
                        foreach (var b in state.behaviours) AddBehaviour(names, b);
                        if (state.speedParameterActive) names.Add(state.speedParameter);
                        if (state.timeParameterActive) names.Add(state.timeParameter);
                        if (state.mirrorParameterActive) names.Add(state.mirrorParameter);
                        if (state.cycleOffsetParameterActive) names.Add(state.cycleOffsetParameter);
                        AddMotion(names, state.motion);
                        if (layer.syncedLayerIndex >= 0) AddMotion(names, layer.GetOverrideMotion(state));
                    }
                }
            }
            names.Remove("");
            names.Remove(null);
            return names;
        }

        internal static HashSet<string> ComponentParameters(VRCAvatarDescriptor avatar)
        {
            var names = new HashSet<string>();
            foreach (var bone in avatar.GetComponentsInChildren<VRCPhysBoneBase>(true))
            {
                if (string.IsNullOrEmpty(bone.parameter)) continue;
                foreach (var suffix in PhysBoneSuffixes) names.Add(bone.parameter + suffix);
            }
            foreach (var receiver in avatar.GetComponentsInChildren<ContactReceiver>(true))
                if (!string.IsNullOrEmpty(receiver.parameter)) names.Add(receiver.parameter);
            return names;
        }

        static void AddConditions(HashSet<string> names, AnimatorTransitionBase transition)
        {
            if (transition == null) return;
            foreach (var c in transition.conditions) names.Add(c.parameter);
        }

        static void AddBehaviour(HashSet<string> names, StateMachineBehaviour behaviour)
        {
            if (behaviour is VRC_AvatarParameterDriver driver)
            {
                foreach (var p in driver.parameters)
                {
                    names.Add(p.name);
                    if (p.type == VRC_AvatarParameterDriver.ChangeType.Copy) names.Add(p.source);
                }
            }
            else if (behaviour is VRC_AnimatorPlayAudio audio)
                names.Add(audio.ParameterName);
        }

        static void AddMotion(HashSet<string> names, Motion motion)
        {
            if (!(motion is BlendTree tree)) return;
            if (tree.blendType == BlendTreeType.Direct)
            {
                foreach (var child in tree.children) names.Add(child.directBlendParameter);
            }
            else
            {
                names.Add(tree.blendParameter);
                if (tree.blendType != BlendTreeType.Simple1D) names.Add(tree.blendParameterY);
            }
            foreach (var child in tree.children) AddMotion(names, child.motion);
        }
    }
}
