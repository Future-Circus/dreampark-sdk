namespace DreamPark.Easy
{
    using UnityEngine;

    public class EasyUV : EasyEvent
    {
        public Renderer renderer;
        private Material material;
        private Mesh mesh;
        private Vector2[] baseUVs;
        public string uvName = "_baseTex";
        public bool scrollUV = false;
        [ShowIf("scrollUV")] public Vector2 uvScrollSpeed = Vector2.zero;
        public bool rotateUV = false;
        [ShowIf("rotateUV")] public float uvRotationSpeed = 0.05f;
        public bool scaleUV = false;
        [ShowIf("scaleUV")] public Vector2 uvScale = Vector2.one;
        public bool useVelocity = false;
        [ShowIf("useVelocity")] public float velocityFactor = 0f;
        [ShowIf("useVelocity")] public float velocityScale = 0f;
        private Vector2 uvOffsetStart;
        private float uvRotationStart;
        private Vector2 uvScaleStart;
        private Vector3 prevPosition;
        private float angle = 0f;
        public override void Start() {
            base.Start();
            if (renderer == null) {
                renderer = GetComponent<Renderer>();
            }
            if (material == null) {
                material = renderer.material;
            }
            if (mesh == null) {
                mesh = renderer.GetComponent<MeshFilter>().mesh;
            }
            baseUVs = mesh.uv;
            uvOffsetStart = material.GetTextureOffset(uvName);
            uvScaleStart = material.GetTextureScale(uvName);
            prevPosition = transform.position;
        }
        private float CalculateVelocityFactor(float speed) {
            if (!useVelocity) {
                return speed;
            }
            float velocity = (transform.position - prevPosition).magnitude;
            prevPosition = transform.position;
            return speed * (1f-velocityFactor) + (velocity * velocityFactor * velocityScale);
        }
        public void Update()
        {
            if (!isEnabled) {
                return;
            }

            if (material != null) {
                if (scrollUV) {
                    material.SetTextureOffset(uvName, uvOffsetStart + new Vector2(CalculateVelocityFactor(uvScrollSpeed.x), CalculateVelocityFactor(uvScrollSpeed.y)) * Time.time);
                }
                if (rotateUV) {
                    angle += CalculateVelocityFactor(uvRotationSpeed) * Time.deltaTime;
                    float cos = Mathf.Cos(angle);
                    float sin = Mathf.Sin(angle);

                    Vector2 center = new Vector2(0.5f, 0.5f);
                    Vector2[] rotatedUVs = new Vector2[baseUVs.Length];

                    for (int i = 0; i < baseUVs.Length; i++)
                    {
                        Vector2 uv = baseUVs[i] - center;
                        rotatedUVs[i] = new Vector2(
                            uv.x * cos - uv.y * sin,
                            uv.x * sin + uv.y * cos
                        ) + center;
                    }
                    mesh.uv = rotatedUVs;
                }
                if (scaleUV) {
                    material.SetTextureScale(uvName, uvScaleStart + new Vector2(CalculateVelocityFactor(uvScale.x), CalculateVelocityFactor(uvScale.y)) * Time.time);
                }
            }
        }
    }

}
