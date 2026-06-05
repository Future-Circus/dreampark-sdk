using UnityEngine;
using UnityEngine.AI;

public class EasyNoise : EasyEvent
{
    public static int noiseSeed = 0;
    public float intensity = 1f;
    public float speed = 1f;
    public Vector3 positionFactor = Vector3.zero;
    public Vector3 rotationFactor = Vector3.zero;
    public Vector3 scaleFactor = Vector3.zero;
    public float velocityFactor = 0f;

    private Vector3 startPosition;
    private Quaternion startRotation;
    private Vector3 startScale;
    private Rigidbody rb;

    public override void Awake()
    {
        base.Awake();
        noiseSeed++;
        rb = GetComponentInParent<Rigidbody>();
    }
    public override void Start()
    {
        base.Start();
        startPosition = transform.localPosition;
        startRotation = transform.localRotation;
        startScale = transform.localScale;
    }

    public override void OnEvent(object arg0 = null)
    {
        isEnabled = true;
        onEvent?.Invoke(null);
    }

    // Update is called once per frame
    void Update()
    {
        if (isEnabled) {
            float _intensity = intensity;
            float _speed = speed;
            if (rb != null) {
                _intensity += velocityFactor * (rb.linearVelocity.magnitude + rb.angularVelocity.magnitude);
            }
            float noiseX = Mathf.PerlinNoise( noiseSeed + Time.time * _speed, 0) - 0.5f;
            float noiseY = Mathf.PerlinNoise( noiseSeed + Time.time * _speed, 1) - 0.5f;
            float noiseZ = Mathf.PerlinNoise( noiseSeed + Time.time * _speed, 2) - 0.5f;
            
            if (positionFactor != Vector3.zero) {
                transform.localPosition = startPosition + new Vector3(noiseX * _intensity * positionFactor.x, noiseY * _intensity * positionFactor.y, noiseZ * _intensity * positionFactor.z);
            }
            if (rotationFactor != Vector3.zero) {
                transform.localRotation = startRotation * Quaternion.Euler(noiseX * _intensity * rotationFactor.x, noiseY * _intensity * rotationFactor.y, noiseZ * _intensity * rotationFactor.z);
            }
            if (scaleFactor != Vector3.one) {
                transform.localScale = startScale + new Vector3(noiseX * _intensity * scaleFactor.x, noiseY * _intensity * scaleFactor.y, noiseZ * _intensity * scaleFactor.z);
            }
        }
    }
}
