using UnityEngine;
using System.Threading.Tasks;
using System;

#if UNITY_EDITOR
using UnityEditor;
#endif

namespace DreamPark.Easy {
    public class EasyParticle : EasyEvent
    {
        public GameObject particleEffect;
        public bool copyRotation = false;
        public bool parent = false;
        public bool delayNextEvent = false;
        public Collider emitter;
        public override void OnEvent(object arg0 = null)
        {
            if (particleEffect != null) {
                // Check if the particleEffect is a prefab asset or already part of the scene (runtime-safe)
                GameObject particle;
                if (particleEffect.scene.IsValid() && particleEffect.scene.rootCount != 0)
                {
                    Debug.LogWarning("[EasyParticle] The assigned particleEffect is already in the scene, not a prefab asset.");
                    particle = particleEffect;
                    particle.GetComponent<ParticleSystem>().Play();
                } else {
                    particle = Instantiate(particleEffect);
                    if (emitter != null)
                    {
                        Bounds b = GetComponent<Collider>().bounds;
                        Vector3 bottomCenter = new Vector3(b.center.x, b.min.y, b.center.z);
                        particle.transform.position = bottomCenter;
                        float radius = Mathf.Max(b.extents.x, b.extents.z);
                        foreach (var ps in particle.GetComponentsInChildren<ParticleSystem>())
                        {
                            var shape = ps.shape;
                            shape.radius = radius;
                        }
                    }
                    particle.transform.position = transform.position;
                }
                if (copyRotation) {
                    particle.transform.rotation = transform.rotation;
                }
                if (parent) {
                    particle.transform.SetParent(transform,true);
                }
                if (delayNextEvent) {
                    Delay(particle);
                } else {
                    onEvent?.Invoke(arg0);
                }
            }
        }

        async void Delay(GameObject gameObject)
        {
            try {
                if (this == null || gameObject == null)
                    return;

                var ps = GetComponentInChildren<ParticleSystem>();
                while(ps != null && !ps.isPlaying)
                {
                    await Task.Yield();
                }
                if (ps == null)
                    return;
                var duration = ps.main.duration + ps.main.startLifetime.constantMax;

                while (ps.main.loop == true && !ps.isStopped) {
                    await Task.Yield();
                }

                await Task.Delay((int)(duration*1000));

                if (this == null || gameObject == null)
                    return;

                onEvent?.Invoke(null);
            } catch (Exception e) {
                Debug.LogError("[EasyParticle] Error: " + e.Message);
            }
        }
    }
}
