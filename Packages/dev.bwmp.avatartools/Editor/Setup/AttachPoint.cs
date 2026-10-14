using UnityEngine;

namespace AvatarTools.Editor
{
    public sealed class AttachPoint : ScriptableObject
    {
        public string accessoryKey;
        public string accessoryName;
        public string bone;

        public Vector3 localPosition;
        public Quaternion localRotation = Quaternion.identity;
        public Vector3 localScale = Vector3.one;

        public Vector3 alignedPosition;
        public Quaternion alignedRotation = Quaternion.identity;
        public Vector3 alignedScale = Vector3.one;

        public float boneLength;
        public float avatarHeight;
    }
}
