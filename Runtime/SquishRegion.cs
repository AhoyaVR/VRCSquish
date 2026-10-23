using UnityEngine;
using VRC.SDKBase;

namespace VRCSquish
{
    [AddComponentMenu("")]
    [DisallowMultipleComponent]
    public class SquishRegion : MonoBehaviour, IEditorOnly
    {
        public Vector3 size = new Vector3(0.1f, 0.1f, 0.08f);

        [Range(0.01f, 1f)]
        public float softness = 0.8f;

        public float depth = 0.06f;

        public Matrix4x4 RegionToWorld => Matrix4x4.TRS(transform.position, transform.rotation, Vector3.one);

        public Vector3 CameraLocalPosition => new Vector3(0f, 0f, -depth);

        public float OrthoHalfSize => Mathf.Max(size.x, size.y);

        public float FarPlane => (depth + size.z) * 2f + 0.05f;

        public const int MaxId = 2047;

        public static float NearPlane(int id, int regionIndex) => 0.001f + (id * SquishSetup.MaxRegions + regionIndex) * 0.0000002f;

        public float Distance(Vector3 local)
        {
            Vector3 s = new Vector3(Mathf.Max(size.x, 1e-4f), Mathf.Max(size.y, 1e-4f), Mathf.Max(size.z, 1e-4f));
            return new Vector3(local.x / s.x, local.y / s.y, local.z / s.z).magnitude;
        }

        public float Weight(Vector3 local)
        {
            return 1f - Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(1f - softness, 1f, Distance(local)));
        }
    }
}
