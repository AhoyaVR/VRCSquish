using System.Collections.Generic;
using UnityEngine;
using VRC.SDKBase;

namespace VRCSquish
{
    [AddComponentMenu("")]
    [DisallowMultipleComponent]
    public class SquishSetup : MonoBehaviour, IEditorOnly
    {
        [System.Serializable]
        public class Swap
        {
            public Renderer renderer;
            public int slot;
            public Material material;
        }

        public const int MaxRegions = 4;
        public const int DefaultLayers = (1 << 9) | (1 << 10);

        public List<Renderer> renderers = new List<Renderer>();

        public List<SquishRegion> regions = new List<SquishRegion>();

        public LayerMask layers = DefaultLayers;

        public int resolution = 256;

        public bool hideSelf = true;

        public int id;

        public List<Renderer> selfRenderers = new List<Renderer>();

        public string output = "Assets/VRCSquish/Generated";

        public List<Swap> swaps = new List<Swap>();

        public bool baked;
    }
}
