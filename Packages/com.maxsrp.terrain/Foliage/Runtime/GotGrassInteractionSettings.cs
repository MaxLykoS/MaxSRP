using UnityEngine;

namespace MaxTerrain
{
    [CreateAssetMenu(menuName="MaxTerrain/GotGrass Interaction Settings", fileName="GotGrassInteraction")]
    public sealed class GotGrassInteractionSettings : ScriptableObject
    {
        [Header("Collider Contact")]
        public bool UseColliderContact = true;
        [Range(0,8)] public float ContactStrength = 2;
        [Tooltip("Time for immediate contact to fade after release, in seconds.")]
        [Min(.05f)] public float ContactRecoverySeconds = .75f;

        [Header("Moving Trail")]
        [Min(0)] public float Strength = 1;
        [Tooltip("Full side length of the particle quad, in metres, before speed and random size.")]
        [Min(.1f)] public float QuadWidth = .75f;
        [Tooltip("Distance in metres between particles along the collider path.")]
        [Min(.1f)] public float SpawnSpacing = 100f/256;
        [Tooltip("Speeds in m/s at which trail amplitude starts and reaches full strength.")]
        public Vector2 AmplitudeSpeedRange = new Vector2(.25f,5.5f);
        [Tooltip("Speeds in m/s at which the particle size ramp starts and ends.")]
        public Vector2 SizeSpeedRange = new Vector2(.25f,5.5f);
        public Vector2 LifetimeRange = new Vector2(3,7);
        public Vector2 FrequencyRange = new Vector2(3.5f,4.5f);
        public Vector2 SizeVariationRange = new Vector2(.67f,1.33f);
        [Range(0,1)] public float ReboundAmount = 1;
        [Tooltip("Envelope over normalized particle age. Default tangents are linear.")]
        public AnimationCurve Envelope = CreateEnvelope();

        [Header("Brush")]
        [Tooltip("Optional linear RG vector texture. If empty the procedural brush weights are used.")]
        public Texture2D BrushTexture;
        [Range(0,1)] public float ForwardWeight = .46f;
        [Range(0,1)] public float SideWeight = .54f;
        [Tooltip("Forward offset in metres at full speed (5.5 m/s), applied at birth.")]
        public float SpawnForwardOffset;

        internal const float EnvelopePeakAge = .020f;

        internal static AnimationCurve CreateEnvelope()
        {
            var keys = new[]
            {
                new Keyframe(0,0),new Keyframe(EnvelopePeakAge,1),new Keyframe(.162f,.676f),
                new Keyframe(.256f,.430f),new Keyframe(.385f,.230f),new Keyframe(.565f,.130f),
                new Keyframe(.756f,.058f),new Keyframe(1,0)
            };
            for (int i=1;i<keys.Length;i++)
            {
                float slope=(keys[i].value-keys[i-1].value)/(keys[i].time-keys[i-1].time);
                keys[i-1].outTangent=slope;
                keys[i].inTangent=slope;
            }
            return new AnimationCurve(keys);
        }
    }
}
