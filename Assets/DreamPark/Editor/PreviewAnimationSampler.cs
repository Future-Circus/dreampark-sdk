#if UNITY_EDITOR && !DREAMPARKCORE
using System;
using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace DreamPark
{
    // Finds the animated entities inside a prefab and freezes them on a
    // chosen clip + frame so the preview renderer photographs a pose the
    // creator picked instead of the authored bind pose.
    //
    // Why this exists: a rigged character's bind pose is a T-pose, and a
    // machine's idle prop is usually modelled mid-nothing. Both make for a
    // dead preview tile. Being able to say "this character, Run, frame 14"
    // is the difference between a catalogue of T-poses and a catalogue that
    // sells the attraction.
    //
    // Two hard constraints shape the design:
    //
    //   1. A clip's curve paths are relative to the GameObject that owns the
    //      Animator/Animation, so a clip can only be sampled onto THAT
    //      object. Hence "entity" = one such owner, addressed by hierarchy
    //      path, and poses are stored per entity rather than per prefab.
    //
    //   2. Poses must compose. A prefab can hold several instances of the
    //      SAME nested character prefab, each frozen on a different clip.
    //      Sampling goes through AnimationMode in a single batch for all
    //      entities — the batching is load-bearing; see the Sampling
    //      section for why anything else either reverts sibling poses or
    //      silently fails on controller rigs.
    public static class PreviewAnimationSampler
    {
        // sourceLabel values — also used by the dedup rules below, so they
        // live in one place.
        private const string kSourceController = "Controller";
        private const string kSourceModel = "Model clips";
        private const string kSourceLegacy = "Animation";

        // Unity mints a hidden "__preview__Foo" clip next to imported ones;
        // it's an editor implementation detail and must never be offered.
        private const string kHiddenClipPrefix = "__preview__";

        // Fallback frame rate for clips that somehow report 0 fps, so frame
        // math can't divide by zero or produce a single-frame timeline.
        private const float kFallbackFrameRate = 60f;

        // ── Discovery ───────────────────────────────────────────────────

        // One animated GameObject inside the prefab, plus every clip that
        // could reasonably be sampled onto it.
        public sealed class AnimatedEntity
        {
            // Hierarchy path relative to the prefab root; "" = the root.
            // This is the key stored in AnimationPose.targetPath, and it is
            // unique across the returned list.
            public string path = string.Empty;

            // What the panel is titled. Names the top-level child of the
            // attraction that contains this rig ("Robot"), not the rig node
            // the Animator happens to sit on ("AnimSource") — the former is
            // what a creator recognises. Suffixed with the owner's name only
            // when one top-level child contains more than one entity.
            public string label = string.Empty;

            // The Animator/Animation owner's own GameObject name.
            public string shortName = string.Empty;

            // Full hierarchy path, shown under the title so two similar
            // entities are always tellable apart.
            public string pathLabel = string.Empty;

            // Where the clips came from: an Animator controller, a legacy
            // Animation component, or the imported model asset.
            public string sourceLabel = string.Empty;

            public List<AnimationClip> clips = new List<AnimationClip>();

            // Popup labels, index-aligned with `clips`. Disambiguated with
            // the owning asset name when two clips share a name.
            public string[] clipLabels = Array.Empty<string>();
        }

        // Walks the prefab for Animator and legacy Animation components and
        // returns one entry per independently poseable GameObject. Entities
        // with no reachable clips are omitted entirely — the UI keys "is this
        // prefab animated?" off an empty result.
        //
        // "Independently poseable" is the load-bearing word. A rigged
        // character routinely has an Animator on the model root AND another
        // further down the rig; both reach the same imported FBX, so listing
        // both would show the same clip menu twice and give a creator two
        // panels fighting over one skeleton. The passes below exist to make
        // that impossible.
        public static List<AnimatedEntity> Discover(GameObject prefabRoot)
        {
            var entities = new List<AnimatedEntity>();
            if (prefabRoot == null) return entities;

            Transform root = prefabRoot.transform;
            var byPath = new Dictionary<string, AnimatedEntity>();

            void Merge(Transform owner, List<AnimationClip> clips, string source)
            {
                if (owner == null || clips == null || clips.Count == 0) return;

                string path = RelativePath(root, owner);
                if (!byPath.TryGetValue(path, out var entity))
                {
                    entity = new AnimatedEntity
                    {
                        path = path,
                        shortName = owner.name,
                        pathLabel = string.IsNullOrEmpty(path) ? "(prefab root)" : path,
                        sourceLabel = source,
                    };
                    byPath[path] = entity;
                    entities.Add(entity);
                }

                for (int i = 0; i < clips.Count; i++)
                {
                    var c = clips[i];
                    if (c == null) continue;
                    if (!entity.clips.Contains(c)) entity.clips.Add(c);
                }
            }

            var animators = prefabRoot.GetComponentsInChildren<Animator>(true);

            // Pass 1 — Animators carrying their own controller. These name
            // their clips explicitly, so they're genuine animation sources
            // wherever they sit in the hierarchy.
            var controllerClips = new List<AnimationClip>[animators.Length];
            for (int i = 0; i < animators.Length; i++)
            {
                controllerClips[i] = animators[i] != null
                    ? ClipsFromController(animators[i])
                    : new List<AnimationClip>();

                if (controllerClips[i].Count > 0)
                    Merge(animators[i].transform, controllerClips[i], kSourceController);
            }

            // Pass 2 — controller-less Animators fall back to the clips
            // imported with their model. Only the OUTERMOST such Animator in
            // a branch qualifies: a nested one resolves to the same FBX and
            // would duplicate the panel.
            for (int i = 0; i < animators.Length; i++)
            {
                if (animators[i] == null || controllerClips[i].Count > 0) continue;
                if (HasAnimatorAncestor(animators[i].transform, root)) continue;

                var fromModel = ClipsFromModelAssets(animators[i]);
                if (fromModel.Count > 0)
                    Merge(animators[i].transform, fromModel, kSourceModel);
            }

            // Pass 3 — legacy Animation components always carry explicit
            // clips, so they're never ambiguous.
            var legacy = prefabRoot.GetComponentsInChildren<Animation>(true);
            for (int i = 0; i < legacy.Length; i++)
            {
                if (legacy[i] == null) continue;
                Merge(legacy[i].transform, ClipsFromLegacyAnimation(legacy[i]), kSourceLegacy);
            }

            DropShadowedModelFallbacks(entities);
            DropDuplicateDescendants(entities);
            DropInvisibleAndRedundantEntities(prefabRoot, entities);
            AssignLabels(entities, prefabRoot.name);

            for (int i = 0; i < entities.Count; i++)
                entities[i].clipLabels = BuildClipLabels(entities[i].clips);

            return entities;
        }

        // True when the prefab has anything worth showing animation controls
        // for. Cheap enough to call from OnGUI-adjacent code paths, but the
        // Preview Editor caches the full Discover() result anyway.
        public static bool HasAnimatedEntities(GameObject prefabRoot)
            => Discover(prefabRoot).Count > 0;

        // Is there an Animator anywhere between this transform and the prefab
        // root (inclusive of the root, exclusive of the transform itself)?
        private static bool HasAnimatorAncestor(Transform t, Transform root)
        {
            if (t == null) return false;
            Transform p = t.parent;
            while (p != null)
            {
                if (p.GetComponent<Animator>() != null) return true;
                if (p == root) break;
                p = p.parent;
            }
            return false;
        }

        // A controller-less Animator offering model clips is usually the
        // imported rig node of a character whose real animation source is a
        // controller Animator elsewhere on the same object — commonly a
        // SIBLING retarget node (the "T-Pose"/"AnimSource" pattern), which
        // the ancestor-only rule in pass 2 can't see. Both resolve to the
        // same FBX clips, so listing both gives the creator two panels
        // fighting over one skeleton. A model-fallback entity is dropped
        // when a controller/legacy entity in the same branch of the prefab
        // offers any of the same clip assets.
        //
        // "Same branch" = one is the other's ancestor, or they live under
        // the same top-level child of the prefab. The scoping matters: three
        // zombies instanced from the same nested prefab all reference the
        // same FBX clips, and cross-branch dropping would collapse them into
        // one panel when they must stay individually poseable.
        private static void DropShadowedModelFallbacks(List<AnimatedEntity> entities)
        {
            for (int i = entities.Count - 1; i >= 0; i--)
            {
                if (entities[i].sourceLabel != kSourceModel) continue;

                for (int j = 0; j < entities.Count; j++)
                {
                    if (i == j || entities[j].sourceLabel == kSourceModel) continue;
                    if (!SameBranch(entities[j].path, entities[i].path)) continue;
                    if (!ClipsIntersect(entities[j].clips, entities[i].clips)) continue;

                    entities.RemoveAt(i);
                    break;
                }
            }
        }

        private static bool SameBranch(string a, string b)
        {
            if (IsAncestorPath(a, b) || IsAncestorPath(b, a)) return true;
            return FirstSegment(a) == FirstSegment(b);
        }

        private static string FirstSegment(string path)
        {
            if (string.IsNullOrEmpty(path)) return string.Empty;
            int slash = path.IndexOf('/');
            return slash < 0 ? path : path.Substring(0, slash);
        }

        private static bool ClipsIntersect(List<AnimationClip> a, List<AnimationClip> b)
        {
            for (int i = 0; i < b.Count; i++)
                if (b[i] != null && a.Contains(b[i])) return true;
            return false;
        }

        // The source-based rules above can't catch every rig layout — some
        // asset packs ship a character as TWO controller-bearing Animators
        // side by side (a "T-Pose" node holding the mesh and an "AnimSource"
        // node holding the takes), which reads as two legitimate entities.
        // The ground truth is visual: which Renderers can this entity
        // actually move when sampled? That's the renderers in its own
        // subtree, plus every SkinnedMeshRenderer anywhere in the prefab
        // whose bones live under it.
        //
        //   1. An entity that can't move ANY renderer is pure panel noise —
        //      posing it can never change a pixel of the preview. Drop it.
        //   2. Two same-branch entities offering the same clips AND moving
        //      overlapping renderers are one rig listed twice. Keep the one
        //      that moves more of it.
        //
        // The overlap requirement is what keeps this safe for the case it
        // must NOT touch: two identical doors under one group, each with its
        // own Animator and the same controller, move disjoint renderer sets
        // and both stay individually poseable.
        private static void DropInvisibleAndRedundantEntities(GameObject prefabRoot, List<AnimatedEntity> entities)
        {
            if (entities.Count == 0) return;

            var allSkinned = prefabRoot.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            var affected = new List<HashSet<Renderer>>(entities.Count);

            for (int i = 0; i < entities.Count; i++)
            {
                var set = new HashSet<Renderer>();
                GameObject target = ResolveTarget(prefabRoot, entities[i].path);
                if (target != null)
                {
                    Transform t = target.transform;

                    var own = target.GetComponentsInChildren<Renderer>(true);
                    for (int r = 0; r < own.Length; r++)
                        if (own[r] != null) set.Add(own[r]);

                    for (int s = 0; s < allSkinned.Length; s++)
                    {
                        var smr = allSkinned[s];
                        if (smr == null || set.Contains(smr)) continue;
                        if (DrivesBonesUnder(smr, t)) set.Add(smr);
                    }
                }
                affected.Add(set);
            }

            // Rule 1 — can't move anything visible.
            for (int i = entities.Count - 1; i >= 0; i--)
            {
                if (affected[i].Count > 0) continue;
                entities.RemoveAt(i);
                affected.RemoveAt(i);
            }

            // Rule 2 — same rig listed twice.
            for (int i = entities.Count - 1; i >= 0; i--)
            {
                for (int j = 0; j < entities.Count; j++)
                {
                    if (i == j) continue;
                    if (!SameBranch(entities[j].path, entities[i].path)) continue;
                    if (!ClipsIntersect(entities[j].clips, entities[i].clips)) continue;
                    if (!RenderersIntersect(affected[j], affected[i])) continue;
                    if (!Prefer(entities[j], affected[j], j, entities[i], affected[i], i)) continue;

                    entities.RemoveAt(i);
                    affected.RemoveAt(i);
                    break;
                }
            }
        }

        // True when the skinned mesh's skeleton (root bone or any bone) sits
        // under `t` — i.e. sampling an animation onto `t` moves this mesh
        // even though the renderer component itself lives elsewhere.
        private static bool DrivesBonesUnder(SkinnedMeshRenderer smr, Transform t)
        {
            if (IsSelfOrDescendantOf(smr.rootBone, t)) return true;

            var bones = smr.bones;
            if (bones != null)
            {
                for (int i = 0; i < bones.Length; i++)
                    if (IsSelfOrDescendantOf(bones[i], t)) return true;
            }
            return false;
        }

        private static bool IsSelfOrDescendantOf(Transform node, Transform ancestor)
        {
            while (node != null)
            {
                if (node == ancestor) return true;
                node = node.parent;
            }
            return false;
        }

        private static bool RenderersIntersect(HashSet<Renderer> a, HashSet<Renderer> b)
        {
            foreach (var r in b)
                if (a.Contains(r)) return true;
            return false;
        }

        // Which of two same-rig duplicates survives: the one that moves more
        // renderers, then the one offering more clips, then the outer one,
        // then discovery order. Returns true when `keep` beats `drop`.
        private static bool Prefer(
            AnimatedEntity keep, HashSet<Renderer> keepAffected, int keepIndex,
            AnimatedEntity drop, HashSet<Renderer> dropAffected, int dropIndex)
        {
            if (keepAffected.Count != dropAffected.Count) return keepAffected.Count > dropAffected.Count;
            if (keep.clips.Count != drop.clips.Count) return keep.clips.Count > drop.clips.Count;
            int keepDepth = PathDepth(keep.path);
            int dropDepth = PathDepth(drop.path);
            if (keepDepth != dropDepth) return keepDepth < dropDepth;
            return keepIndex < dropIndex;
        }

        private static int PathDepth(string path)
        {
            if (string.IsNullOrEmpty(path)) return 0;
            int depth = 1;
            for (int i = 0; i < path.Length; i++)
                if (path[i] == '/') depth++;
            return depth;
        }

        // Safety net for anything the pass rules miss: an entity nested under
        // another whose clip list is exactly the same is the same rig found
        // twice. Keep the outer one — it's the object a creator points at.
        private static void DropDuplicateDescendants(List<AnimatedEntity> entities)
        {
            for (int i = entities.Count - 1; i >= 0; i--)
            {
                for (int j = 0; j < entities.Count; j++)
                {
                    if (i == j) continue;
                    if (!IsAncestorPath(entities[j].path, entities[i].path)) continue;
                    if (!SameClips(entities[j].clips, entities[i].clips)) continue;

                    entities.RemoveAt(i);
                    break;
                }
            }
        }

        private static bool IsAncestorPath(string ancestor, string descendant)
        {
            if (ancestor == descendant) return false;
            if (string.IsNullOrEmpty(ancestor)) return true;   // the root contains everything
            return descendant.StartsWith(ancestor + "/", StringComparison.Ordinal);
        }

        private static bool SameClips(List<AnimationClip> a, List<AnimationClip> b)
        {
            if (a.Count != b.Count) return false;
            for (int i = 0; i < b.Count; i++)
                if (!a.Contains(b[i])) return false;
            return true;
        }

        // Titles each panel after the top-level child of the prefab that
        // contains it, falling back to the prefab's own name for an entity on
        // the root. Only when one top-level child holds several entities does
        // the owner's name get appended, so the common case stays a single
        // recognisable word.
        private static void AssignLabels(List<AnimatedEntity> entities, string rootName)
        {
            var groupCounts = new Dictionary<string, int>();
            var groups = new string[entities.Count];

            for (int i = 0; i < entities.Count; i++)
            {
                string path = entities[i].path;
                string group;
                if (string.IsNullOrEmpty(path))
                {
                    group = rootName;
                }
                else
                {
                    int slash = path.IndexOf('/');
                    group = slash < 0 ? path : path.Substring(0, slash);
                }

                groups[i] = group;
                groupCounts.TryGetValue(group, out int n);
                groupCounts[group] = n + 1;
            }

            for (int i = 0; i < entities.Count; i++)
            {
                entities[i].label = groupCounts[groups[i]] > 1 && entities[i].shortName != groups[i]
                    ? $"{groups[i]} › {entities[i].shortName}"
                    : groups[i];
            }
        }

        private static List<AnimationClip> ClipsFromController(Animator animator)
        {
            var list = new List<AnimationClip>();
            try
            {
                var controller = animator.runtimeAnimatorController;
                if (controller != null) AddClips(list, controller.animationClips);
            }
            catch (Exception e)
            {
                // A controller can be broken/missing on a partially imported
                // asset; that's not worth failing discovery over.
                Debug.LogWarning($"[PreviewAnimation] Could not read clips from the controller on '{animator.name}': {e.Message}");
            }
            return list;
        }

        private static List<AnimationClip> ClipsFromLegacyAnimation(Animation anim)
        {
            var list = new List<AnimationClip>();

            // Read the serialized clip list rather than enumerating
            // AnimationStates: states only exist once the component has been
            // initialised at runtime, and this runs on a prefab in edit mode.
            if (anim.clip != null) list.Add(anim.clip);
            try
            {
                AddClips(list, AnimationUtility.GetAnimationClips(anim.gameObject));
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[PreviewAnimation] Could not read legacy clips on '{anim.name}': {e.Message}");
            }

            return list;
        }

        // Clips imported alongside the model an Animator drives — its avatar
        // asset and the meshes under it. Used only when the Animator has no
        // usable controller.
        private static List<AnimationClip> ClipsFromModelAssets(Animator animator)
        {
            var result = new List<AnimationClip>();
            var assetPaths = new List<string>();

            void Consider(UnityEngine.Object obj)
            {
                if (obj == null) return;
                string p = AssetDatabase.GetAssetPath(obj);
                if (string.IsNullOrEmpty(p)) return;
                if (!assetPaths.Contains(p)) assetPaths.Add(p);
            }

            Consider(animator.avatar);

            var skinned = animator.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            for (int i = 0; i < skinned.Length; i++)
                if (skinned[i] != null) Consider(skinned[i].sharedMesh);

            var filters = animator.GetComponentsInChildren<MeshFilter>(true);
            for (int i = 0; i < filters.Length; i++)
                if (filters[i] != null) Consider(filters[i].sharedMesh);

            for (int i = 0; i < assetPaths.Count; i++)
                AddClips(result, ClipsInAsset(assetPaths[i]));

            return result;
        }

        // Every AnimationClip an asset exposes: the asset itself for a
        // standalone .anim, or the imported sub-assets for an FBX.
        private static List<AnimationClip> ClipsInAsset(string assetPath)
        {
            var found = new List<AnimationClip>();
            if (string.IsNullOrEmpty(assetPath)) return found;

            try
            {
                if (AssetDatabase.LoadMainAssetAtPath(assetPath) is AnimationClip main && IsSelectable(main))
                    found.Add(main);

                var reps = AssetDatabase.LoadAllAssetRepresentationsAtPath(assetPath);
                for (int i = 0; i < reps.Length; i++)
                {
                    if (reps[i] is AnimationClip c && IsSelectable(c) && !found.Contains(c))
                        found.Add(c);
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[PreviewAnimation] Could not read clips from '{assetPath}': {e.Message}");
            }

            return found;
        }

        private static bool IsSelectable(AnimationClip clip)
            => clip != null && !clip.name.StartsWith(kHiddenClipPrefix, StringComparison.Ordinal);

        private static void AddClips(List<AnimationClip> into, IReadOnlyList<AnimationClip> clips)
        {
            if (clips == null) return;
            for (int i = 0; i < clips.Count; i++)
            {
                var c = clips[i];
                if (!IsSelectable(c)) continue;
                if (!into.Contains(c)) into.Add(c);
            }
        }

        // Clip names collide constantly across FBX files ("Take 001", "Idle").
        // Only pay the disambiguation cost where there's an actual collision,
        // so the common case stays a clean one-word dropdown.
        private static string[] BuildClipLabels(List<AnimationClip> clips)
        {
            var labels = new string[clips.Count];
            for (int i = 0; i < clips.Count; i++)
            {
                var clip = clips[i];
                if (clip == null) { labels[i] = "<missing>"; continue; }

                bool collides = false;
                for (int j = 0; j < clips.Count; j++)
                {
                    if (j == i || clips[j] == null) continue;
                    if (clips[j].name == clip.name) { collides = true; break; }
                }

                if (!collides)
                {
                    labels[i] = EscapeForPopup(clip.name);
                    continue;
                }

                string owner = System.IO.Path.GetFileNameWithoutExtension(AssetDatabase.GetAssetPath(clip));
                labels[i] = string.IsNullOrEmpty(owner)
                    ? EscapeForPopup($"{clip.name} ({i})")
                    : EscapeForPopup($"{clip.name} ({owner})");
            }
            return labels;
        }

        // A '/' in an EditorGUILayout.Popup label silently turns the entry
        // into a submenu, which would hide clips whose names contain slashes.
        private static string EscapeForPopup(string label)
            => string.IsNullOrEmpty(label) ? "<unnamed>" : label.Replace("/", "∕");

        // ── Clip references ─────────────────────────────────────────────

        // Builds the persisted reference for a clip: GUID names the asset,
        // local file id picks the clip inside it, name is the fallback.
        public static void FillClipRef(ref AnimationPose pose, AnimationClip clip)
        {
            if (clip == null)
            {
                pose.clipGuid = string.Empty;
                pose.clipFileId = 0;
                pose.clipName = string.Empty;
                return;
            }

            pose.clipName = clip.name;
            if (AssetDatabase.TryGetGUIDAndLocalFileIdentifier(clip, out string guid, out long localId))
            {
                pose.clipGuid = guid;
                pose.clipFileId = localId;
            }
            else
            {
                // An in-memory clip (a runtime-built override, say) has no
                // GUID, so there'd be nothing to write down and nothing to
                // resolve on the next batch. Say so rather than saving a
                // pose that silently renders as the bind pose.
                pose.clipGuid = string.Empty;
                pose.clipFileId = 0;
                Debug.LogWarning(
                    $"[PreviewAnimation] '{clip.name}' isn't a saved asset, so it can't be used as a " +
                    "preview pose. Save the clip into the project and pick it again.");
            }
        }

        public static AnimationClip ResolveClip(AnimationPose pose)
        {
            if (!pose.HasClip) return null;

            string assetPath = AssetDatabase.GUIDToAssetPath(pose.clipGuid);
            if (string.IsNullOrEmpty(assetPath)) return null;

            var clips = ClipsInAsset(assetPath);

            if (pose.clipFileId != 0)
            {
                for (int i = 0; i < clips.Count; i++)
                {
                    if (AssetDatabase.TryGetGUIDAndLocalFileIdentifier(clips[i], out _, out long id)
                        && id == pose.clipFileId)
                        return clips[i];
                }
            }

            // Re-importing an FBX can renumber local file ids, which would
            // orphan every saved pose in the package. Matching by name second
            // keeps the override alive across a re-export.
            if (!string.IsNullOrEmpty(pose.clipName))
            {
                for (int i = 0; i < clips.Count; i++)
                    if (clips[i].name == pose.clipName) return clips[i];
            }

            return null;
        }

        // Index of the pose's clip within an entity's clip list, or -1.
        // Compares by resolved object first, then by name, so a dropdown
        // still highlights the right row after a re-import.
        public static int IndexOfClip(AnimatedEntity entity, AnimationPose pose)
        {
            if (entity == null || !pose.HasClip) return -1;

            var resolved = ResolveClip(pose);
            if (resolved != null)
            {
                int direct = entity.clips.IndexOf(resolved);
                if (direct >= 0) return direct;
            }

            if (!string.IsNullOrEmpty(pose.clipName))
            {
                for (int i = 0; i < entity.clips.Count; i++)
                    if (entity.clips[i] != null && entity.clips[i].name == pose.clipName) return i;
            }

            return -1;
        }

        // ── Frame math ──────────────────────────────────────────────────
        // Creators think in frames; the file stores normalized time. These
        // are the only two places that conversion happens.

        public static float FrameRateOf(AnimationClip clip)
            => clip != null && clip.frameRate > 0.01f ? clip.frameRate : kFallbackFrameRate;

        // Number of frame INTERVALS in the clip. The timeline therefore runs
        // 0..FrameCountOf inclusive, matching how Unity's Animation window
        // numbers a 1-second 30fps clip as frames 0 through 30.
        public static int FrameCountOf(AnimationClip clip)
        {
            if (clip == null) return 0;
            return Mathf.Max(1, Mathf.RoundToInt(clip.length * FrameRateOf(clip)));
        }

        public static int FrameFromNormalized(AnimationClip clip, float normalized)
        {
            int frames = FrameCountOf(clip);
            if (frames <= 0) return 0;
            return Mathf.Clamp(Mathf.RoundToInt(Mathf.Clamp01(normalized) * frames), 0, frames);
        }

        public static float NormalizedFromFrame(AnimationClip clip, int frame)
        {
            int frames = FrameCountOf(clip);
            if (frames <= 0) return 0f;
            return Mathf.Clamp01((float)Mathf.Clamp(frame, 0, frames) / frames);
        }

        // ── Sampling ────────────────────────────────────────────────────
        //
        // AnimationMode — the machinery the Animation window itself uses —
        // is the only editor-side sampler that reliably drives every rig
        // type (humanoid retarget, generic, legacy) on an edit-mode
        // instance. (A per-Animator PlayableGraph evaluation was tried and
        // silently writes nothing on some controller rigs in edit mode.)
        //
        // Its trap is batch semantics: each BeginSampling starts a fresh
        // snapshot and REVERTS the modifications of the previous batch. Put
        // one entity per batch and only the last entity stays posed — the
        // "every zombie snaps to the last clip I touched" bug. So ALL
        // entities are sampled inside ONE BeginSampling/EndSampling batch,
        // where samples compose, and the batch stays live (PoseScope) until
        // the caller has rendered.

        // Owns the AnimationMode session this sampler started. Dispose it
        // AFTER rendering but BEFORE destroying the posed instance:
        // StopAnimationMode reverts the recorded modifications, and
        // reverting onto destroyed objects produces phantom Console errors.
        public struct PoseScope : IDisposable
        {
            private bool _ownsAnimationMode;

            internal PoseScope(bool ownsAnimationMode)
            {
                _ownsAnimationMode = ownsAnimationMode;
            }

            public void Dispose()
            {
                if (!_ownsAnimationMode) return;
                _ownsAnimationMode = false;
                try
                {
                    AnimationMode.StopAnimationMode();
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[PreviewAnimation] Failed to leave animation mode: {e.Message}");
                }
            }
        }

        // Freezes every entity named in `settings` on its chosen clip/frame.
        // Returns a scope the caller must dispose after rendering. A settings
        // value with no poses is a fast no-op that never touches animation
        // mode — that's what keeps default previews byte-identical to the
        // pre-animation renderer.
        public static PoseScope ApplyPoses(GameObject instanceRoot, PreviewSettings settings)
        {
            if (instanceRoot == null || !settings.HasAnyPose) return default;

            // Resolve everything up front — entering animation mode for a
            // set of poses that all turn out to be dangling references would
            // be pure editor churn.
            var jobs = new List<(GameObject target, AnimationClip clip, float time)>();
            var poses = settings.animationPoses;

            for (int i = 0; i < poses.Count; i++)
            {
                var pose = poses[i].Sanitized();
                if (!pose.HasClip) continue;

                GameObject target = ResolveTarget(instanceRoot, pose.targetPath);
                if (target == null)
                {
                    Debug.LogWarning(
                        $"[PreviewAnimation] '{instanceRoot.name}' has a saved pose for '{pose.targetPath}', " +
                        "but no such child exists any more — rendering that part unposed.");
                    continue;
                }

                AnimationClip clip = ResolveClip(pose);
                if (clip == null)
                {
                    Debug.LogWarning(
                        $"[PreviewAnimation] Could not resolve clip '{pose.clipName}' for '{instanceRoot.name}' " +
                        "— rendering that part unposed.");
                    continue;
                }

                jobs.Add((target, clip, Mathf.Clamp01(pose.normalizedTime) * clip.length));
            }

            if (jobs.Count == 0) return default;

            PrepareSkinnedRenderers(instanceRoot);

            // Never hijack an animation mode somebody else started (the
            // Animation window's preview, most likely) — stopping it on the
            // way out would yank the rug from under them.
            bool ownsMode = false;
            if (!AnimationMode.InAnimationMode())
            {
                try
                {
                    AnimationMode.StartAnimationMode();
                    ownsMode = AnimationMode.InAnimationMode();
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[PreviewAnimation] Could not enter animation mode ({e.Message}); using direct sampling.");
                    ownsMode = false;
                }
            }

            if (ownsMode)
            {
                // ONE batch for every entity — see the header comment.
                try
                {
                    AnimationMode.BeginSampling();
                    for (int i = 0; i < jobs.Count; i++)
                    {
                        try
                        {
                            AnimationMode.SampleAnimationClip(jobs[i].target, jobs[i].clip, jobs[i].time);
                        }
                        catch (Exception e)
                        {
                            Debug.LogWarning(
                                $"[PreviewAnimation] Could not sample '{jobs[i].clip.name}' onto " +
                                $"'{jobs[i].target.name}': {e.Message}");
                        }
                    }
                }
                finally
                {
                    try { AnimationMode.EndSampling(); }
                    catch (Exception) { /* batch already unwound */ }
                }
                return new PoseScope(true);
            }

            // Animation mode unavailable: evaluate curves directly. Composes
            // fine across objects; humanoid clips may not retarget, which is
            // still better than a T-pose for everything else.
            for (int i = 0; i < jobs.Count; i++)
            {
                try
                {
                    jobs[i].clip.SampleAnimation(jobs[i].target, jobs[i].time);
                }
                catch (Exception e)
                {
                    Debug.LogWarning(
                        $"[PreviewAnimation] Could not sample '{jobs[i].clip.name}' onto " +
                        $"'{jobs[i].target.name}': {e.Message}");
                }
            }
            return default;
        }

        // A posed skeleton routinely pushes vertices outside the bounds the
        // importer baked for the bind pose. Left alone, the skinned mesh gets
        // frustum-culled or badly framed. updateWhenOffscreen makes Unity
        // recompute bounds from the actual posed vertices at render time.
        //
        // Only ever called on a prefab that IS being posed, so unposed
        // previews keep their exact historical framing.
        private static void PrepareSkinnedRenderers(GameObject root)
        {
            var skinned = root.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            for (int i = 0; i < skinned.Length; i++)
            {
                if (skinned[i] == null) continue;
                skinned[i].updateWhenOffscreen = true;
            }
        }

        // ── Hierarchy paths ─────────────────────────────────────────────

        public static GameObject ResolveTarget(GameObject instanceRoot, string path)
        {
            if (instanceRoot == null) return null;
            if (string.IsNullOrEmpty(path)) return instanceRoot;

            Transform t = instanceRoot.transform.Find(path);
            return t != null ? t.gameObject : null;
        }

        // Path from `root` down to `t`, slash-separated, "" when they're the
        // same object. Matches the addressing Transform.Find expects, and is
        // stable across instantiation (unlike sibling indices, which the park
        // spawner reorders — see the NetId notes in CLAUDE.md).
        public static string RelativePath(Transform root, Transform t)
        {
            if (t == null || root == null || t == root) return string.Empty;

            var sb = new StringBuilder(t.name);
            Transform p = t.parent;
            while (p != null && p != root)
            {
                sb.Insert(0, '/');
                sb.Insert(0, p.name);
                p = p.parent;
            }
            return sb.ToString();
        }
    }
}
#endif
