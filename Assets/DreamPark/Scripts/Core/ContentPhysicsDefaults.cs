using UnityEngine;

namespace DreamPark
{
    /// <summary>
    /// Physics defaults for content: overlapping props must never rocket away.
    ///
    /// Unity pushes interpenetrating bodies apart at up to
    /// <c>Physics.defaultMaxDepenetrationVelocity</c> (10 m/s out of the box). In a park that
    /// is the classic "the gift exploded off the table" bug: two props authored a few
    /// millimetres into each other, a hand body pressed into a box, a kinematic body capsule
    /// walking through a pile. At 10 m/s a 150 g party hat crosses the room.
    ///
    /// THE RULE. At boot (before any scene or content loads) the PROCESS-WIDE defaults are
    /// capped:
    ///   • default max depenetration velocity ≤ <see cref="MaxDepenetration"/> (2 m/s);
    ///   • default max angular speed ≤ <see cref="MaxAngularSpeed"/> (14 rad/s; projects ship 7).
    /// A Rigidbody takes these defaults when it is CREATED, and neither value is serialized in
    /// prefabs, so every content body gets them — while a body a game configures explicitly
    /// (<c>rb.maxDepenetrationVelocity = …</c>, <c>rb.maxAngularVelocity = …</c>, or
    /// dp.net_body's max_push / max_spin) keeps exactly what the game set, because those
    /// writes happen after creation. Nothing else (mass, drag, solver) is touched.
    ///
    /// Runs in the app (DREAMPARKCORE) and in Editor play mode, so a creator testing in the
    /// SDK sees the physics the headset runs.
    /// </summary>
    public static class ContentPhysicsDefaults
    {
        public const float MaxDepenetration = 2f;
        public const float MaxAngularSpeed = 14f;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        public static void Apply()
        {
#if UNITY_EDITOR
            // In the Editor these setters write the project's DynamicsManager in memory and
            // would survive Play mode (and get saved into ProjectSettings). Put them back.
            float depen = Physics.defaultMaxDepenetrationVelocity;
            float spin = Physics.defaultMaxAngularSpeed;
            void Restore(UnityEditor.PlayModeStateChange s)
            {
                if (s != UnityEditor.PlayModeStateChange.EnteredEditMode) return;
                UnityEditor.EditorApplication.playModeStateChanged -= Restore;
                Physics.defaultMaxDepenetrationVelocity = depen;
                Physics.defaultMaxAngularSpeed = spin;
            }
            UnityEditor.EditorApplication.playModeStateChanged += Restore;
#endif
            if (Physics.defaultMaxDepenetrationVelocity > MaxDepenetration)
                Physics.defaultMaxDepenetrationVelocity = MaxDepenetration;
            if (Physics.defaultMaxAngularSpeed > MaxAngularSpeed)
                Physics.defaultMaxAngularSpeed = MaxAngularSpeed;
        }
    }
}
