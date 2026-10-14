using UnityEngine;
using VRC.Dynamics;

namespace AvatarTools.Editor
{
    public sealed class PhysBonePreset : ScriptableObject
    {
        public VRCPhysBoneBase.Version version;
        public VRCPhysBoneBase.IntegrationType integrationType;
        public Vector3 endpointPosition;
        public VRCPhysBoneBase.MultiChildType multiChildType;
        public bool ignoreOtherPhysBones;

        public float pull;
        public AnimationCurve pullCurve;
        public float spring;
        public AnimationCurve springCurve;
        public float stiffness;
        public AnimationCurve stiffnessCurve;
        public float gravity;
        public AnimationCurve gravityCurve;
        public float gravityFalloff;
        public AnimationCurve gravityFalloffCurve;
        public VRCPhysBoneBase.ImmobileType immobileType;
        public float immobile;
        public AnimationCurve immobileCurve;

        public VRCPhysBoneBase.AdvancedBool allowCollision;
        public VRCPhysBoneBase.PermissionFilter collisionFilter;
        public float radius;
        public AnimationCurve radiusCurve;

        public VRCPhysBoneBase.LimitType limitType;
        public float maxAngleX;
        public AnimationCurve maxAngleXCurve;
        public float maxAngleZ;
        public AnimationCurve maxAngleZCurve;
        public Vector3 limitRotation;
        public AnimationCurve limitRotationXCurve;
        public AnimationCurve limitRotationYCurve;
        public AnimationCurve limitRotationZCurve;

        public VRCPhysBoneBase.AdvancedBool allowGrabbing;
        public VRCPhysBoneBase.PermissionFilter grabFilter;
        public VRCPhysBoneBase.AdvancedBool allowPosing;
        public VRCPhysBoneBase.PermissionFilter poseFilter;
        public bool snapToHand;
        public float grabMovement;

        public float maxStretch;
        public AnimationCurve maxStretchCurve;
        public float maxSquish;
        public AnimationCurve maxSquishCurve;
        public float stretchMotion;
        public AnimationCurve stretchMotionCurve;

        public bool isAnimated;
        public bool resetWhenDisabled;

        public void Capture(VRCPhysBoneBase source)
        {
            version = source.version;
            integrationType = source.integrationType;
            endpointPosition = source.endpointPosition;
            multiChildType = source.multiChildType;
            ignoreOtherPhysBones = source.ignoreOtherPhysBones;
            pull = source.pull;
            pullCurve = Copy(source.pullCurve);
            spring = source.spring;
            springCurve = Copy(source.springCurve);
            stiffness = source.stiffness;
            stiffnessCurve = Copy(source.stiffnessCurve);
            gravity = source.gravity;
            gravityCurve = Copy(source.gravityCurve);
            gravityFalloff = source.gravityFalloff;
            gravityFalloffCurve = Copy(source.gravityFalloffCurve);
            immobileType = source.immobileType;
            immobile = source.immobile;
            immobileCurve = Copy(source.immobileCurve);
            allowCollision = source.allowCollision;
            collisionFilter = source.collisionFilter;
            radius = source.radius;
            radiusCurve = Copy(source.radiusCurve);
            limitType = source.limitType;
            maxAngleX = source.maxAngleX;
            maxAngleXCurve = Copy(source.maxAngleXCurve);
            maxAngleZ = source.maxAngleZ;
            maxAngleZCurve = Copy(source.maxAngleZCurve);
            limitRotation = source.limitRotation;
            limitRotationXCurve = Copy(source.limitRotationXCurve);
            limitRotationYCurve = Copy(source.limitRotationYCurve);
            limitRotationZCurve = Copy(source.limitRotationZCurve);
            allowGrabbing = source.allowGrabbing;
            grabFilter = source.grabFilter;
            allowPosing = source.allowPosing;
            poseFilter = source.poseFilter;
            snapToHand = source.snapToHand;
            grabMovement = source.grabMovement;
            maxStretch = source.maxStretch;
            maxStretchCurve = Copy(source.maxStretchCurve);
            maxSquish = source.maxSquish;
            maxSquishCurve = Copy(source.maxSquishCurve);
            stretchMotion = source.stretchMotion;
            stretchMotionCurve = Copy(source.stretchMotionCurve);
            isAnimated = source.isAnimated;
            resetWhenDisabled = source.resetWhenDisabled;
        }

        public void ApplyTo(VRCPhysBoneBase target)
        {
            target.version = version;
            target.integrationType = integrationType;
            target.endpointPosition = endpointPosition;
            target.multiChildType = multiChildType;
            target.ignoreOtherPhysBones = ignoreOtherPhysBones;
            target.pull = pull;
            target.pullCurve = Copy(pullCurve);
            target.spring = spring;
            target.springCurve = Copy(springCurve);
            target.stiffness = stiffness;
            target.stiffnessCurve = Copy(stiffnessCurve);
            target.gravity = gravity;
            target.gravityCurve = Copy(gravityCurve);
            target.gravityFalloff = gravityFalloff;
            target.gravityFalloffCurve = Copy(gravityFalloffCurve);
            target.immobileType = immobileType;
            target.immobile = immobile;
            target.immobileCurve = Copy(immobileCurve);
            target.allowCollision = allowCollision;
            target.collisionFilter = collisionFilter;
            target.radius = radius;
            target.radiusCurve = Copy(radiusCurve);
            target.limitType = limitType;
            target.maxAngleX = maxAngleX;
            target.maxAngleXCurve = Copy(maxAngleXCurve);
            target.maxAngleZ = maxAngleZ;
            target.maxAngleZCurve = Copy(maxAngleZCurve);
            target.limitRotation = limitRotation;
            target.limitRotationXCurve = Copy(limitRotationXCurve);
            target.limitRotationYCurve = Copy(limitRotationYCurve);
            target.limitRotationZCurve = Copy(limitRotationZCurve);
            target.allowGrabbing = allowGrabbing;
            target.grabFilter = grabFilter;
            target.allowPosing = allowPosing;
            target.poseFilter = poseFilter;
            target.snapToHand = snapToHand;
            target.grabMovement = grabMovement;
            target.maxStretch = maxStretch;
            target.maxStretchCurve = Copy(maxStretchCurve);
            target.maxSquish = maxSquish;
            target.maxSquishCurve = Copy(maxSquishCurve);
            target.stretchMotion = stretchMotion;
            target.stretchMotionCurve = Copy(stretchMotionCurve);
            target.isAnimated = isAnimated;
            target.resetWhenDisabled = resetWhenDisabled;
        }

        static AnimationCurve Copy(AnimationCurve curve)
        {
            if (curve == null || curve.length == 0) return null;
            return new AnimationCurve(curve.keys) { preWrapMode = curve.preWrapMode, postWrapMode = curve.postWrapMode };
        }
    }
}
