#if UNITY_EDITOR && !DREAMPARKCORE
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace DreamPark.PreUploadChecks.Checks
{
    // Physics, audio, particle and animation components sitting on the ROOT
    // GameObject of an attraction or prop prefab.
    //
    // WHY THIS BLOCKS
    //
    // The park loader never registers a template root as a LevelObject. That is
    // deliberate and it is not changing — LevelObjectManager.RegisterLevelObject
    // says so in its own comment:
    //
    //     "A LevelTemplate is a special case: it registers each of its children
    //      individually so they stay separately cullable, and so the template root
    //      itself (PropTemplate, GameArea, BuildModeObjectController) keeps working
    //      while its contents are switched off. A prop the player placed on its own
    //      is the same case - it has to stay grabbable in Build Mode."
    //
    // So it recurses PAST the root into the children. Every LevelObject snapshot is
    // taken with GetComponentsInChildren FROM THAT CHILD, and a child can never see
    // a component on its parent. The consequence is a hole exactly one GameObject
    // wide: anything the creator put on the root is outside the parking and culling
    // system entirely, in BOTH build mode and play mode.
    //
    // A ParticleSystem there is never stopped. An AudioSource there is never
    // silenced. An Animator there is never paused. A Rigidbody2D there is never
    // frozen. It all keeps running while the park is being built, while the
    // attraction is parked, and while the guest is 60 m away — and none of that is
    // visible in the Editor, because a hand-authored scene registers nothing with
    // LevelObjectManager at all. It fails silently, and only on device.
    //
    // TWO PARTIAL EXCEPTIONS, BOTH STILL WORTH REPORTING
    //
    //  - Rigidbody. RegisterTemplateRootBodies patches the root's own Rigidbodies
    //    into a separate templateRoots list, because a player-placed physics prop
    //    carries its body on the template root and used to fall through the floor
    //    the frame it spawned. But that list is only driven by the GLOBAL
    //    Enable/DisableAllLevelObjects edges — it is not part of per-object distance
    //    culling — and it reads GetComponents<Rigidbody>, so Rigidbody2D is not
    //    covered by it at all. A body on the root is still the wrong place to put
    //    one; it just fails less spectacularly than the rest of this list.
    //
    //  - Collider. LevelObjectManager leaves root colliders alone ON PURPOSE:
    //    "Build Mode raycasts against them to select and drag the prop; parking them
    //    would make a freshly-spawned prop unselectable." That makes a root collider
    //    permanent collision geometry that never stands down — real, solid, and
    //    un-parkable — which is why a non-trigger one blocks. See the trigger
    //    carve-out below.
    //
    // SCOPE — DELIBERATE, DECIDED BY THE PRODUCT OWNER, DO NOT WIDEN
    //
    // Only the concrete runtime types in RejectedKind below are rejected. This check
    // does NOT reject MonoBehaviours in general, and specifically ALLOWS LuaBehaviour
    // on the root.
    //
    // That is not an oversight and it is not a soft spot to be tightened later. The
    // SDK's own documented Dream Sequence format REQUIRES a LuaBehaviour on the root:
    // "The controller goes on the attraction ROOT — the GameObject carrying
    // AttractionTemplate / GameArea / MusicArea", and dp.attraction() resolves to
    // exactly that root scope. A check that rejected root MonoBehaviours would block
    // the SDK's own A_DreamSequence sample, every attraction authored from it, and
    // most shipped content — which is the failure mode PreUploadCheck.cs's policy
    // comment is about: "spend it on a finding that is wrong often enough to be
    // dismissed and it stops meaning anything."
    //
    // The rule a creator can act on is narrow and always true: physics, audio,
    // particles and animation belong on a CHILD of the root, inside the attraction
    // or prop. Script components belong wherever the author wants them.
    //
    // Light overlaps SunLightCheck for the directional case. That is fine — the two
    // findings say different things (this one is "it is on the root", that one is
    // "it lights the whole park"), they carry different subKeys, and each is
    // independently ignorable.
    //
    // WHY THERE IS NO AUTOMATIC FIX
    //
    // The obvious auto-fix is "reparent the component onto a new child", and it is
    // not safe for a single type on this list. Moving a Collider changes what the
    // physics engine collides against and breaks Build Mode's drag raycast. Moving
    // an AudioSource changes its spatialisation origin. Moving a Rigidbody changes
    // the centre of mass and orphans every joint and every script holding the
    // reference. Moving an Animator invalidates every animation path in its
    // controller, since those are relative to the Animator's own transform. Every
    // one of those produces a prefab that still compiles, still opens, and is
    // subtly wrong — which is the exact class of bug this suite exists to catch, so
    // authoring more of it as a "fix" would be self-defeating. Navigate plus a clear
    // instruction is the honest offer.
    //
    // A SECOND MECHANISM WAS PROPOSED AND ALSO REJECTED (2026-08-30)
    //
    // Not "move the component" — instead: create a new empty GameObject as the
    // prefab's new root, move the TEMPLATE component (PropTemplate/
    // AttractionTemplate/etc.) onto it, and reparent the entire ORIGINAL root
    // (runtime components unmoved relative to each other and to their own
    // GameObject) as a child of the new one. It sounded safe because nothing on the
    // old root moves relative to anything it was already relative to — only the
    // GameObject's depth in the hierarchy changes.
    //
    // It reopens the same class of bug via a different door. RegisterLevelObject
    // (LevelObjectManager.cs:962) only takes the "register children individually,
    // leave the root alone" branch when THIS GameObject carries PropTemplate /
    // LevelTemplate (obj.GetComponent<PropTemplate>(), :959 — same GameObject, not
    // GetComponentInParent). After this restructure the template lives on the NEW
    // root; the recursive call on the demoted OLD root finds no template on it and
    // falls into the ordinary LevelObject path instead — which snapshots and parks
    // whatever colliders sit on that GameObject via GetComponentsInChildren. That is
    // precisely the carve-out LevelObjectManager.cs:1022-1025 exists to prevent
    // ("Colliders on the root are deliberately left ALONE. Build Mode raycasts
    // against them to select and drag the prop; parking them would make a
    // freshly-spawned prop unselectable.") — the demoted root's collider now gets
    // parked like any other child collider, and a freshly-spawned prop becomes
    // unselectable in Build Mode again.
    //
    // Fixing that would mean also teaching RegisterLevelObject to recognize a
    // template on a PARENT, not just restructuring the prefab — a change to
    // LevelObjectManager itself, out of scope for a pre-upload-check fix action.
    // Not reinvestigated: whether Build Mode's own raycast/selection code (closed
    // source, DREAMPARKCORE-gated — not present in this SDK checkout) resolves by
    // literal root-ness or by walking up to the nearest template, and whether
    // PrefabUtility.SaveAsPrefabAsset preserves existing scene/nested-prefab
    // instances cleanly across a root-of-hierarchy restructure this large — both
    // moot once the LevelObjectManager regression alone rules it out.
    public sealed class RootComponentsCheck : IPreUploadCheck
    {
        public const string CheckId = "root-components";

        public string Id { get { return CheckId; } }
        public string DisplayName { get { return "Runtime components on a content root"; } }
        public CheckSeverity DefaultSeverity { get { return CheckSeverity.Blocking; } }

        // Load the prefab asset, GetComponents on one GameObject. No prefab contents
        // scene, no scene opening, no shader analysis, no dependency walk. There is
        // nothing here to defer to the gate.
        public bool RunsInAdvisoryScan { get { return true; } }

        public string Rationale
        {
            get
            {
                return "The park loader never registers an attraction or prop ROOT with the "
                     + "optimizer — it recurses into the children so the root's template, GameArea "
                     + "and Build Mode handle keep working. Physics, audio, particles and animation "
                     + "left on the root are therefore never parked, never culled and never "
                     + "silenced, in build mode or play mode. Move them onto a child.";
            }
        }

        public CheckResult Run(PreUploadCheckContext ctx)
        {
            var findings = new List<Finding>();
            int i = 0;

            foreach (var root in ctx.roots)
            {
                ctx.Progress((float)i / Mathf.Max(1, ctx.roots.Count),
                             $"Checking root components on {root.name}…");
                i++;

                // The player rig is priority-registered and explicitly never culled
                // ("the player rig must never be parked — the July 2026 Zombiez
                // bug"), so the hole this check is about does not exist for it: its
                // root components are supposed to keep running. Skip the root rather
                // than reporting a finding nobody should act on.
                if (root.kind == ContentRootKindPublic.Player) continue;

                // LoadAssetAtPath, not PrefabUtility.LoadPrefabContents. The contents
                // API opens a temporary scene and is where broken vendor prefabs
                // actually blow up (which is why SunLightCheck guards its call); this
                // only needs the asset's root GameObject, which is what
                // ContentRootScanner already loaded to classify it. A prefab carrying
                // missing scripts loads fine here and surfaces them as null entries in
                // the component array — handled below, not thrown.
                //
                // No try/catch on purpose: PreUploadCheckRunner already runs every
                // check inside one, and a check that throws is Errored and contributes
                // zero blocking findings by design.
                GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(root.assetPath);
                if (prefab == null) continue;

                var seenPerType = new Dictionary<string, int>(StringComparer.Ordinal);

                // ParticleSystem carries [RequireComponent(typeof(ParticleSystemRenderer))],
                // so Unity adds the renderer for you and a root particle effect would
                // otherwise raise TWO findings with the same cause and the same fix,
                // every single time. Report the system and stay quiet about its own
                // renderer. The renderer is still checked on its own, which is the
                // case worth a row: a ParticleSystemRenderer with no system beside it
                // is a leftover from a deleted effect and nothing will ever park it.
                bool hasParticleSystem = prefab.GetComponent<ParticleSystem>() != null;

                foreach (var component in prefab.GetComponents<Component>())
                {
                    // Null when the prefab carries a missing script reference. Not
                    // this check's problem, and not a reason to throw.
                    if (component == null) continue;

                    string kind = RejectedKind(component);
                    if (kind == null) continue;
                    if (kind == "particlesystemrenderer" && hasParticleSystem) continue;

                    // Two BoxColliders on one root are two separate findings, so the
                    // subKey has to discriminate them or one Ignore silently covers
                    // both. Component order within a prefab asset is stable, so an
                    // ordinal within the type is a stable key.
                    int ordinal;
                    seenPerType.TryGetValue(kind, out ordinal);
                    seenPerType[kind] = ordinal + 1;

                    string typeName = component.GetType().Name;
                    bool isTriggerVolume = IsTriggerVolume(component);

                    var finding = new Finding
                    {
                        checkId = CheckId,
                        // A trigger volume on the root is a common and mostly-harmless
                        // authoring habit: it detects, it does not move, and an
                        // un-parked detector is a nuisance rather than a defect. A
                        // NON-trigger collider on an un-parked root is real collision
                        // geometry the guest can walk into that never stands down.
                        severity = isTriggerVolume ? CheckSeverity.Warning : CheckSeverity.Blocking,
                        assetGuid = root.guid,
                        assetPath = root.assetPath,
                        subKey = kind + "#" + ordinal,
                        title = $"{root.KindLabel} '{root.name}' has a {typeName} on its root object"
                              + (isTriggerVolume ? "  (trigger volume)" : ""),
                        detail = Detail(root, typeName, isTriggerVolume),
                    };

                    string path = root.assetPath;
                    finding.fixes.Add(FixAction.Navigate("Select prefab", () =>
                    {
                        var asset = AssetDatabase.LoadMainAssetAtPath(path);
                        if (asset != null) { Selection.activeObject = asset; EditorGUIUtility.PingObject(asset); }
                    }));
                    finding.fixes.Add(FixAction.Navigate("Open in Prefab Mode", () =>
                    {
                        var asset = AssetDatabase.LoadMainAssetAtPath(path);
                        if (asset != null) AssetDatabase.OpenAsset(asset);
                    }));

                    findings.Add(finding);
                }
            }

            return CheckResult.From(CheckId, findings);
        }

        private static string Detail(ContentRootInfo root, string typeName, bool isTriggerVolume)
        {
            string kindWord = root.kind == ContentRootKindPublic.Prop ? "prop" : "attraction";

            string detail =
                $"The {typeName} is on '{root.name}' itself — the object carrying the "
              + $"{(root.kind == ContentRootKindPublic.Prop ? "PropTemplate" : "AttractionTemplate")}.\n\n"
              + $"When a park loads your {kindWord}, the loader hands each CHILD of this object to the "
              + "optimizer, which is what parks and restores components as the guest moves around and "
              + "while the park is being built. It deliberately skips the root itself, so that the "
              + "template, the GameArea and the Build Mode handle keep working while the contents are "
              + "switched off.\n\n"
              + "That means nothing on this root is ever parked, culled, stopped or silenced — it runs "
              + "from the moment the park spawns it until the guest leaves, including while they are "
              + "nowhere near it and while they are still placing things in Build Mode. It looks "
              + "correct in the Editor, because a scene you authored by hand has no optimizer in it at "
              + "all; it only misbehaves on a headset, in a real park.\n\n"
              + $"FIX: create a child GameObject inside '{root.name}' and put the {typeName} on that. "
              + "Anything one level down is managed normally. Scripts are fine on the root — this is "
              + "only about physics, audio, particles and animation.";

            if (isTriggerVolume)
            {
                detail +=
                    "\n\nThis one is reported as a warning rather than a blocker because it is a "
                  + "trigger: it detects the guest, it does not collide with them or move, so an "
                  + "un-parked trigger costs an overlap test rather than putting solid geometry in "
                  + "the room. Moving it onto a child is still the right shape.";
            }

            return detail;
        }

        // The rejected set, and the ONLY rejected set. Returns a stable slug used for
        // the subKey, or null for anything that is allowed on a root.
        //
        // The tests are `is`, not a type-name comparison, because every concrete
        // collider (BoxCollider, MeshCollider, CharacterController…) has to match the
        // one Collider clause. The clauses are mutually exclusive, so a component can
        // only ever produce one slug — but any future entry that DERIVES from one of
        // these has to go ABOVE its base clause, or the base claims it first and the
        // slug is wrong.
        private static string RejectedKind(Component c)
        {
            if (c is Rigidbody) return "rigidbody";
            if (c is Rigidbody2D) return "rigidbody2d";

            // Any subclass. BoxCollider, MeshCollider, CharacterController — all of
            // them are collision geometry the optimizer cannot reach.
            if (c is Collider) return "collider";
            if (c is Collider2D) return "collider2d";

            if (c is ParticleSystem) return "particlesystem";
            if (c is ParticleSystemRenderer) return "particlesystemrenderer";
            if (c is AudioSource) return "audiosource";
            if (c is Animator) return "animator";
            if (c is Animation) return "animation";
            if (c is Light) return "light";
            if (c is Camera) return "camera";

            // Everything else, MonoBehaviour very much included, is allowed here. See
            // the SCOPE note at the top of the file before changing that.
            return null;
        }

        // The trigger carve-out. Applied to Collider2D as well as Collider: it is the
        // same rule with the same reasoning — a 2D trigger detects and does not move —
        // and splitting them would mean an identical authoring habit blocks or warns
        // depending on which physics engine the creator happened to use.
        private static bool IsTriggerVolume(Component c)
        {
            var collider3d = c as Collider;
            if (collider3d != null) return collider3d.isTrigger;

            var collider2d = c as Collider2D;
            if (collider2d != null) return collider2d.isTrigger;

            return false;
        }
    }
}
#endif
