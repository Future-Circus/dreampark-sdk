// ─────────────────────────────────────────────────────────────────────
//  AudioEmitterBuilder.cs — the audio track. WAV in, ambient emitter prop out.
//
//  ⚠ PROVISIONAL. THIS DESIGN WAS NEVER CONFIRMED.
//
//  The spec's own audio section carries a warning on it: the message that
//  described the audio track was cut off mid-sentence, and everything below
//  the heading is a strawman written to be argued with. This file implements
//  that strawman faithfully and completely, so it can be tried and replaced,
//  but nobody has signed off on the shape. The thing most likely to be wrong
//  is that an audio clip should become a PROP at all rather than a field on
//  some containing attraction.
//
//  THE TWO PRESETS ARE TWO DIFFERENT RIGS, NOT ONE RIG WITH A FLAG
//
//  The spec asks for "looping ambience versus one-shot" and calls them field
//  settings on a component that exists. They are not, and the reason is worth
//  writing down because it is invisible from the inspector.
//
//  EasyAudio does NOT play the AudioSource on its own GameObject. Its OnEvent
//  calls AudioClip.PlaySFX (CoreExtensions.cs:255), which creates a brand-new
//  "TempAudio" GameObject, adds its own AudioSource and its own
//  RealisticRolloff, and plays that. With `loop` set it also parents TempAudio
//  to this transform and sets audioSource.loop = true. Two consequences:
//
//    1. THERE IS NO STOP CONDITION FOR A LOOP. EasyAudio.Update only tears the
//       source down when !isEnabled, and isEnabled is cleared only by
//       OnEventDisable. EasyInteraction's filter path can call OnEvent and
//       nothing else — CheckInteraction does `filter.onEvent.OnEvent(...)`, and
//       the only OnEventDisable it can reach is its OWN, via onlyDetectOnce.
//       So a looping EasyAudio driven by a trigger plays for the lifetime of
//       the scene: walk in once, walk to the far side of the park, and it is
//       still playing — inaudible only because RealisticRolloff's curve bottoms
//       out at maxDistance.
//
//    2. PlaySFX derives the temp source's distance from VOLUME, not from any
//       radius: `maxDistance = 10 + ((volume - 1) * 10)`, with volume clamped
//       to 0..1. So the audible distance you get through EasyAudio is
//       volume × 10 metres and CANNOT EXCEED 10 m, while the trigger sphere
//       controls only where playback starts.
//
//  So this file builds two rigs:
//
//    LOOPING  → no trigger, no EasyInteraction, no EasyAudio. The Anchor's own
//               AudioSource is the one you hear: playOnAwake, loop,
//               maxDistance = audibleRadius. RealisticRolloff bakes the curve
//               at Awake exactly as it does for the temp source. audibleRadius
//               then means what it says, there is no stop condition to be
//               missing because there is nothing to stop, and volume is a
//               loudness knob instead of a disguised distance knob.
//
//    ONE-SHOT → the EasyInteraction → EasyAudio chain the spec describes, with
//               onlyDetectOnce, because a one-shot has a natural end (the clip)
//               and PlaySFX's self-destruct handles it.
//
//  WHY THE TRIGGER GOES ON Anchor AND WHY useColliderBounds MUST BE OFF
//
//  PropTemplate.TryGetColliderFootprint walks GetComponentsInChildren
//  <Collider>() and GapFiller/FloorCutout consume the result. An 8 m audible
//  radius is an 8 m sphere collider, and with useColliderBounds on that
//  becomes a 16 × 16 m floor footprint for a prop that has no floor presence
//  at all — GapFiller would build a room-sized platform around a sound. So
//  the emit plan forces ColliderChoice.None (which is what makes
//  PropPrefabEmitter set useColliderBounds = false) and the real footprint is
//  written into customFootprintMeters by hand.
// ─────────────────────────────────────────────────────────────────────

#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace DreamPark.ConvertReady
{
    public static class AudioEmitterBuilder
    {
        // ── Load-type thresholds ────────────────────────────────────────
        //
        // Short clips pay a per-play decode they cannot afford, long clips pay
        // a resident-memory cost they cannot afford, and the cross-over is
        // about where these two numbers are. Same policy the Audio Optimizer
        // window applies; stated here so a converted clip does not arrive
        // already flagged by it.

        /// Under this, the decode cost per play beats the memory cost of
        /// holding raw samples. DecompressOnLoad.
        public const float DecompressUnderSeconds = 1f;

        /// Up to this, hold the compressed bytes and decode on the fly.
        /// Beyond it, stream off disk.
        public const float CompressedUnderSeconds = 15f;

        /// Vorbis quality for a converted clip. Unity's importer default of
        /// 1.0 is very nearly PCM-sized, which defeats the point of asking for
        /// Vorbis at all. 0.7 is a starting point, not a tuned value — the
        /// Audio Optimizer window owns per-clip quality and will happily
        /// re-plan this.
        public const float VorbisQuality = 0.7f;

        /// The emitter has no geometry, so its floor footprint is a point.
        /// Half a metre keeps the PropTemplate gizmo visible enough to grab in
        /// the scene view without claiming floor space.
        public const float EmitterFootprintMeters = 0.5f;

        /// Below this the trigger sphere is too small to be entered reliably.
        public const float MinAudibleRadius = 0.25f;

        /// PlaySFX's hard-coded relationship between EasyAudio.volume and the
        /// distance the sound actually carries: maxDistance = 10 + (v-1)*10,
        /// with v clamped to 0..1 — so 10 m is also the CEILING on the one-shot
        /// path. Read from CoreExtensions.PlaySFX; if that formula changes,
        /// this constant and the report lines below are what go stale.
        public const float PlaySfxMetresPerVolume = 10f;

        /// How far the trigger radius and the carried distance may disagree
        /// before it is worth a line in the report.
        public const float RadiusMatchToleranceMeters = 0.5f;

        /// The layer name the SDK uses for non-solid detection volumes.
        /// LevelObjectManager.RegisterLevelObject treats it as priority (it is
        /// also OptimizationSettings.ignoreLayers' only entry), and the physics
        /// matrix has Triggers ↔ Player enabled, so the rig still trips it.
        public const string TriggerLayerName = "Triggers";

        /// The layer EasyInteraction's filter matches against. Resolved at
        /// RUNTIME by LayerMask.NameToLayer inside CheckInteraction, so a
        /// project that has renamed it fails by never matching — silently, and
        /// only on device. Checked here so the report says so instead.
        public const string PlayerLayerName = "Player";

        // ── Entry point ─────────────────────────────────────────────────

        /// <summary>
        /// Convert one audio clip into a P_*.prefab carrying the canonical
        /// hierarchy with an emitter on the Anchor. Returns the saved prefab
        /// path, or null when nothing was written — every failure path appends
        /// to <paramref name="r"/> first. Never throws.
        /// </summary>
        public static string Build(string clipPath, ConversionPlan plan, ConversionResult r)
        {
            if (r == null) r = new ConversionResult { sourcePath = clipPath, ok = true };
            if (plan == null)
            {
                r.Failed("audio: no ConversionPlan");
                return null;
            }
            if (string.IsNullOrEmpty(clipPath))
            {
                r.Failed("audio: no source path");
                return null;
            }

            r.Skipped("the audio track is PROVISIONAL — its design was never confirmed. "
                    + "Check the emitter against what you actually wanted before building on it.");

            AudioSettings audio = plan.audio != null ? plan.audio : new AudioSettings();

            var clip = AssetDatabase.LoadAssetAtPath<AudioClip>(clipPath);
            if (clip == null)
            {
                r.Failed("audio: '" + clipPath + "' did not load as an AudioClip");
                return null;
            }

            float lengthSeconds = clip.length;
            int sourceChannels = clip.channels;

            r.Measured(string.Format(CultureInfo.InvariantCulture,
                "clip: {0:0.##} s, {1} Hz, {2}",
                lengthSeconds, clip.frequency,
                sourceChannels == 1 ? "mono" : sourceChannels + " channels"));

            // ── Import settings ────────────────────────────────────────
            if (audio.normalizeImportSettings)
            {
                NormalizeAudioImport(clipPath, lengthSeconds, sourceChannels, r);

                // The clip object is invalidated by SaveAndReimport.
                clip = AssetDatabase.LoadAssetAtPath<AudioClip>(clipPath);
                if (clip == null)
                {
                    r.Failed("audio: '" + clipPath + "' failed to reload after the import settings pass");
                    return null;
                }
            }
            else
            {
                r.Skipped("import settings left as authored — normalizeImportSettings is off");
            }

            float radius = Mathf.Max(MinAudibleRadius, audio.audibleRadius);

            // ── Hierarchy ──────────────────────────────────────────────
            //
            // Nothing here writes a rotation per frame, so NeedsMotionNode is
            // false and the Motion node is correctly absent.

            string propName = AssetClassifier.SanitizeAssetName(
                Path.GetFileNameWithoutExtension(clipPath));

            GameObject root = PropPrefabEmitter.BuildHierarchy(propName, false);
            if (root == null)
            {
                r.Failed("audio: could not build the prop hierarchy");
                return null;
            }

            try
            {
                Transform anchor = PropPrefabEmitter.FindAnchor(root);
                if (anchor == null)
                {
                    r.Failed("audio: hierarchy is malformed — expected an Anchor node");
                    return null;
                }

                // A half-built emitter must not reach disk. Emit keeps r.ok
                // false on its own, but it still WRITES the prefab, and
                // ConvertReadyExecutor.Track then adds that path to
                // LastRunCreatedAssets — so the report ends up showing a FAILED
                // line next to a successfully-written output path, and a
                // silent, un-triggerable prop appears in the creator's content
                // folder and in the prop browser.
                if (!BuildEmitter(anchor.gameObject, clip, audio, radius, r)) return null;

                r.Added("Visual node left empty on purpose — a sound has no geometry, and an empty "
                      + "Visual is where a creator drops a speaker or a sign later without "
                      + "restructuring the prop");

                // ── Emit ───────────────────────────────────────────────
                //
                // Forced onto a CLONE so the creator's plan object is not
                // rewritten under them. Two overrides, both mandatory for this
                // track:
                //   collider = None      → useColliderBounds off, so the
                //                          audible radius never becomes a
                //                          room-sized floor footprint
                //   affectsGapFiller off → a sound has no floor presence
                ConversionPlan emitPlan = plan;
                if (plan.collider != ColliderChoice.None || plan.affectsGapFiller)
                {
                    emitPlan = plan.Clone();
                    emitPlan.collider = ColliderChoice.None;
                    emitPlan.affectsGapFiller = false;

                    r.Added(string.Format(CultureInfo.InvariantCulture,
                        "footprint: useColliderBounds off and affectsGapFiller off — {0:0.##} m across is a "
                      + "hearing range, not a floor plan. GapFiller would otherwise build a {0:0.##} m "
                      + "platform around a sound.",
                        radius * 2f));
                }

                string savedPath = PropPrefabEmitter.Emit(root, propName, emitPlan, r);
                if (string.IsNullOrEmpty(savedPath))
                {
                    // Emit has already reported why.
                    return null;
                }

                WriteEmitterFootprint(savedPath, r);
                return savedPath;
            }
            catch (Exception e)
            {
                r.Failed("audio: " + e.Message);
                return null;
            }
            finally
            {
                if (root != null) DestroyWorkingRoot(root);
            }
        }

        // ── The emitter ─────────────────────────────────────────────────

        /// <summary>
        /// Turn the Anchor into an emitter. Returns false when the Anchor could
        /// not be rigged, in which case <paramref name="r"/> already carries the
        /// reason and the caller must not write a prefab.
        ///
        /// Two rigs, not one — see the header. The looping rig drives the
        /// Anchor's own AudioSource; the one-shot rig drives EasyAudio.
        /// </summary>
        private static bool BuildEmitter(GameObject anchor, AudioClip clip, AudioSettings audio,
                                         float radius, ConversionResult r)
        {
            return audio.looping
                ? BuildLoopingEmitter(anchor, clip, audio, radius, r)
                : BuildOneShotEmitter(anchor, clip, audio, radius, r);
        }

        /// <summary>
        /// Looping ambience. No trigger and no EasyEvent chain at all.
        ///
        /// The obvious build — sphere trigger → EasyInteraction → EasyAudio with
        /// loop on — has no stop condition anywhere in it (header, point 1), so
        /// the clip plays for the lifetime of the scene after one entry. Driving
        /// the Anchor's own AudioSource instead removes the question: an
        /// ambience that is always playing and falls off to nothing at
        /// audibleRadius is what a creator who ticked "looping" meant, and
        /// Unity virtualises out-of-range voices for free.
        /// </summary>
        private static bool BuildLoopingEmitter(GameObject anchor, AudioClip clip, AudioSettings audio,
                                                float radius, ConversionResult r)
        {
            // Re-running the converter over an Anchor that was previously built
            // as a one-shot must take the one-shot rig away, or the prop both
            // loops from Awake and fires a second copy on entry.
            StripOneShotRig(anchor);

            AudioSource source = Componentizer.DoComponent<AudioSource>(anchor, true);
            if (source == null)
            {
                r.Failed("audio: could not add the AudioSource to the Anchor node");
                return false;
            }

            source.clip = clip;
            source.playOnAwake = true;
            source.loop = true;
            source.spatialBlend = 1f;                 // fully 3D
            source.volume = Mathf.Clamp01(audio.volume);
            source.maxDistance = radius;

            // RealisticRolloff builds its curve between minDistance and
            // maxDistance, so minDistance is the "no falloff yet" bubble. A
            // quarter of the radius, capped at 1 m, keeps a large ambience
            // from being flat-loud across half its range.
            source.minDistance = Mathf.Min(1f, radius * 0.25f);

            AddRolloff(anchor, r);

            r.Added(string.Format(CultureInfo.InvariantCulture,
                "looping ambience: the Anchor's own AudioSource plays on awake and loops, audible to "
              + "{0:0.##} m, volume {1:0.##}. No trigger and no EasyAudio — EasyAudio's looping path has "
              + "no stop condition (nothing in a trigger-driven chain can call OnEventDisable), so a loop "
              + "started that way plays for the lifetime of the scene.",
                radius, Mathf.Clamp01(audio.volume)));

            r.Measured(string.Format(CultureInfo.InvariantCulture,
                "audible radius {0:0.##} m is the AudioSource's maxDistance, so it means exactly what it "
              + "says here. Volume is loudness and nothing else; the two are independent knobs on this "
              + "path, which is not true of the one-shot path.", radius));

            return true;
        }

        /// <summary>
        /// One-shot. Everything that makes noise, all on the Anchor.
        ///
        /// It has to be one GameObject: OnTriggerEnter is delivered to the
        /// object carrying the Collider, so EasyInteraction must share a node
        /// with the sphere; and RealisticRolloff reads
        /// GetComponents&lt;AudioSource&gt;() on its own GameObject, so the
        /// source must share a node with it.
        ///
        /// Component ORDER matters and is not cosmetic. EasyEvent chains
        /// itself by GetComponents&lt;EasyEvent&gt;() order: index 0 gets
        /// eventOnStart = true and every later one is wired below it. Add
        /// EasyAudio first and the chain starts by playing the clip on Start
        /// instead of waiting for the player.
        /// </summary>
        private static bool BuildOneShotEmitter(GameObject anchor, AudioClip clip, AudioSettings audio,
                                                float radius, ConversionResult r)
        {
            // ── Layer ──────────────────────────────────────────────────
            int triggerLayer = LayerMask.NameToLayer(TriggerLayerName);
            if (triggerLayer >= 0)
            {
                anchor.layer = triggerLayer;
                r.Added("Anchor on the '" + TriggerLayerName + "' layer — the physics matrix has it "
                      + "colliding with Player, and it is the layer the SDK already means by "
                      + "'not solid, just listening'");
            }
            else
            {
                // Not cosmetic. The Anchor keeps whatever layer it has, and
                // whether the player rig can trip the trigger at all now depends
                // on a physics-matrix row nobody looked at.
                r.Skipped("the '" + TriggerLayerName + "' layer does not exist in this project — the Anchor "
                        + "stays on layer '" + LayerMask.LayerToName(anchor.layer) + "', and the trigger "
                        + "will only fire if that layer is set to collide with Player in Project Settings → "
                        + "Physics.");
            }

            // ── Trigger volume ─────────────────────────────────────────
            SphereCollider sphere = Componentizer.DoComponent<SphereCollider>(anchor, true);
            if (sphere == null)
            {
                r.Failed("audio: could not add the SphereCollider to the Anchor node");
                return false;
            }
            sphere.isTrigger = true;
            sphere.radius = radius;
            sphere.center = Vector3.zero;

            r.Measured(string.Format(CultureInfo.InvariantCulture,
                "trigger: {0:0.##} m sphere on Anchor. It fires when the player enters — the player rig's "
              + "trackers carry Rigidbodies, which is what makes a static trigger receive OnTriggerEnter "
              + "at all.", radius));

            // ── Source ─────────────────────────────────────────────────
            AudioSource source = Componentizer.DoComponent<AudioSource>(anchor, true);
            if (source == null)
            {
                r.Failed("audio: could not add the AudioSource to the Anchor node");
                return false;
            }
            source.clip = clip;

            // Off deliberately. EasyAudio owns playback on this path, and a
            // source that also auto-starts means the clip plays twice, out of
            // phase, from two positions.
            source.playOnAwake = false;
            source.loop = false;
            source.spatialBlend = 1f;                 // fully 3D
            source.volume = Mathf.Clamp01(audio.volume);
            source.maxDistance = radius;
            source.minDistance = Mathf.Min(1f, radius * 0.25f);

            AddRolloff(anchor, r);

            // ── Detection, then playback ───────────────────────────────
            EasyInteraction interaction = Componentizer.DoComponent<EasyInteraction>(anchor, true);
            EasyAudio easyAudio = Componentizer.DoComponent<EasyAudio>(anchor, true);
            if (interaction == null || easyAudio == null)
            {
                r.Failed("audio: could not add EasyInteraction/EasyAudio to the Anchor node");
                return false;
            }

            easyAudio.audioClip = clip;
            easyAudio.multipleClips = false;
            easyAudio.loop = false;
            easyAudio.volume = audio.volume;

            // A converted emitter should sound identical on every trip through
            // the trigger. Pitch variation is a nice default for impact SFX
            // and wrong for an ambience loop, and this track cannot tell them
            // apart.
            easyAudio.pitch = 1f;
            easyAudio.pitchVariation = 0f;

            // Belt-and-braces on a rig that fires once: if anything else ever
            // drives this EasyAudio, it replaces its own last playback rather
            // than stacking a second copy on top of the first.
            easyAudio.replaceLastAudio = true;

            var filter = new EasyInteraction.InteractionFilter();
            filter.layers = new[] { PlayerLayerName };
            filter.tags = new string[0];
            filter.collisionEvent = EasyInteraction.CollisionEvent.ENTER;

            // Wired EXPLICITLY rather than left null. EasyInteraction's
            // fallback path is `filter.onEvent = belowEvent` guarded by a
            // Debug.Log that dereferences belowEvent.name — so a null chain
            // link throws a NullReferenceException inside the trigger callback
            // instead of failing quietly. An explicit reference cannot hit it.
            filter.onEvent = easyAudio;
            interaction.interactionFilters = new[] { filter };

            // THE defining field of the one-shot preset, per the spec. The clip
            // plays the first time the player enters and never again for that
            // spawned instance: EasyInteraction.CheckInteraction calls
            // OnEventDisable on itself when this is set, which clears
            // `detecting` and makes every later trigger callback return
            // immediately. Without it, a door chime re-fires every time the
            // player steps in and out of the sphere.
            interaction.onlyDetectOnce = true;
            interaction.continueToNextEvent = false;

            WireEventChain(interaction, easyAudio, r);

            r.Added(string.Format(CultureInfo.InvariantCulture,
                "EasyInteraction → EasyAudio, one-shot: plays the first time the player enters the {0:0.##} m "
              + "sphere and never again for that spawned instance (onlyDetectOnce). Turn onlyDetectOnce off "
              + "on the EasyInteraction if you want it to re-arm when the player leaves and comes back.",
                radius));

            ReportPlayerLayer(r);
            ReportCarriedDistance(audio, radius, r);

            r.Skipped("the AudioSource on Anchor is a TEMPLATE, not the source you hear — EasyAudio spawns "
                    + "its own 'TempAudio' object through AudioClip.PlaySFX. Delete EasyAudio and tick Play "
                    + "On Awake to drive this one directly.");

            return true;
        }

        /// <summary>
        /// RealisticRolloff is a BAKE component, not a live one: Awake rewrites
        /// every AudioSource on this object to a smoothed custom curve with
        /// doppler off and spread 60, and Start then destroys the component
        /// (DestroyOnStart defaults true). Leaving it on the prefab is the
        /// intended use — it is how the curve gets applied on a fresh instance,
        /// and it costs nothing after frame one.
        /// </summary>
        private static void AddRolloff(GameObject anchor, ConversionResult r)
        {
            RealisticRolloff rolloff = Componentizer.DoComponent<RealisticRolloff>(anchor, true);
            if (rolloff == null)
            {
                // Deliberately NOT a failure: the emitter still plays. What it
                // loses is the SDK's curve, so it falls back to Unity's
                // logarithmic rolloff and is louder for longer than every other
                // sound in the park. Worth a line, not worth throwing the
                // prefab away.
                r.Skipped("could not add RealisticRolloff to the Anchor — the emitter falls back to Unity's "
                        + "logarithmic rolloff, which is louder for longer than every other sound in the park");
                return;
            }

            rolloff.DestroyOnStart = true;
            r.Added("RealisticRolloff — replaces Unity's logarithmic falloff with the SDK's smoothed "
                  + "curve at Awake, then deletes itself at Start");
        }

        /// <summary>
        /// EasyInteraction resolves filter.layers with LayerMask.NameToLayer at
        /// runtime, inside CheckInteraction. A project without a "Player" layer
        /// therefore compares `other.layer == -1`, which nothing ever matches,
        /// and the emitter is silent with no error anywhere.
        /// </summary>
        private static void ReportPlayerLayer(ConversionResult r)
        {
            if (LayerMask.NameToLayer(PlayerLayerName) >= 0) return;

            r.Skipped("the '" + PlayerLayerName + "' layer does not exist in this project, and the "
                    + "EasyInteraction filter matches on that NAME at runtime — as written the filter can "
                    + "never match anything and the emitter will be silent. Restore the layer, or edit the "
                    + "filter's Layers entry on the saved prefab.");
        }

        /// <summary>
        /// The one-shot path's inherent mismatch, reported by DIRECTION.
        ///
        /// EasyAudio plays through PlaySFX, whose spawned source gets
        /// maxDistance = 10 + (volume-1)×10 with volume clamped to 0..1. So the
        /// carried distance is volume × 10 m and can never exceed 10 m, while
        /// the trigger sphere decides only where playback STARTS.
        ///
        /// Only one direction is a defect. A trigger TIGHTER than the carried
        /// distance is fine and is the common case — you walk in, the sound
        /// starts, and it fades out somewhere past the sphere. A trigger WIDER
        /// than the carried distance is the bug: playback starts at a range
        /// where the rolloff curve has already reached zero, so the creator
        /// gets nothing and has no way to tell that anything fired.
        ///
        /// Warning on both directions is why the old version of this line fired
        /// on the out-of-the-box preset (radius 8, volume 1 → carries 10), and a
        /// warning that fires on the default configuration is one creators learn
        /// to skip past — which costs the whole MEASURED/GUESSED/SKIPPED split
        /// its meaning.
        /// </summary>
        private static void ReportCarriedDistance(AudioSettings audio, float radius, ConversionResult r)
        {
            float volume = Mathf.Clamp01(audio.volume);
            float carriedMetres = volume * PlaySfxMetresPerVolume;

            if (radius <= carriedMetres + RadiusMatchToleranceMeters)
            {
                r.Measured(string.Format(CultureInfo.InvariantCulture,
                    "carry: at volume {0:0.##} the clip carries about {1:0.##} m (PlaySFX sets the spawned "
                  + "source's maxDistance to 10 + (volume-1)×10), and the trigger starts it at {2:0.##} m — "
                  + "so it starts inside its own audible range and fades out from there.",
                    volume, carriedMetres, radius));
                return;
            }

            string ceiling = radius > PlaySfxMetresPerVolume
                ? " Volume is clamped to 1 inside PlaySFX, so " + PlaySfxMetresPerVolume.ToString("0.##", CultureInfo.InvariantCulture)
                  + " m is the hard ceiling on this path — a radius bigger than that cannot be matched. Tick "
                  + "'looping' to drive the Anchor's AudioSource directly, where maxDistance is the radius."
                : " Set volume to " + (radius / PlaySfxMetresPerVolume).ToString("0.##", CultureInfo.InvariantCulture)
                  + " to make them agree.";

            r.Skipped(string.Format(CultureInfo.InvariantCulture,
                "the trigger is WIDER than the sound carries. EasyAudio plays through AudioClip.PlaySFX, "
              + "which sets the spawned source's maxDistance to 10 + (volume-1)×10 — so at volume {0:0.##} "
              + "the clip carries about {1:0.##} m, while the trigger starts it at {2:0.##} m. Playback "
              + "begins where the rolloff curve has already reached zero, so the player hears nothing.{3}",
                volume, carriedMetres, radius, ceiling));
        }

        /// <summary>
        /// Take the trigger-driven one-shot rig off an Anchor. Only the looping
        /// path calls this, and only so that re-running the converter with
        /// `looping` newly ticked cannot leave both rigs on one node.
        /// </summary>
        private static void StripOneShotRig(GameObject anchor)
        {
            // EasyInteraction before EasyAudio: EasyEvent's chain links are
            // rebuilt from GetComponents order, and removing the head first
            // leaves the tail as a well-formed one-component chain rather than
            // one holding a reference to a destroyed component.
            Componentizer.DoComponent<EasyInteraction>(anchor, false);
            Componentizer.DoComponent<EasyAudio>(anchor, false);
            Componentizer.DoComponent<SphereCollider>(anchor, false);
        }

        /// <summary>
        /// Make the two EasyEvents an actual chain.
        ///
        /// EasyEvent builds its own links in OnValidate, but only when the
        /// editor happens to run it — and it never runs from a script-driven
        /// AddComponent. Without the links, `eventOnStart` stays false on the
        /// EasyInteraction, its OnEvent never fires, `detecting` stays false,
        /// and the trigger callbacks return immediately. The prop is silent
        /// and nothing anywhere says why. So: set the fields ourselves, then
        /// hand OnValidate the job of wiring the persistent UnityEvent
        /// listener so the inspector agrees with what we built.
        /// </summary>
        private static void WireEventChain(EasyInteraction interaction, EasyAudio easyAudio, ConversionResult r)
        {
            interaction.aboveEvent = null;
            interaction.belowEvent = easyAudio;
            interaction.eventOnStart = true;      // starts detecting on Start

            easyAudio.aboveEvent = interaction;
            easyAudio.belowEvent = null;
            easyAudio.eventOnStart = false;       // waits to be told

            try
            {
                // Called on the CHILD link, not the parent. EasyEvent's
                // NeedsRebuild only notices a missing persistent listener by
                // looking UP at its own aboveEvent — so asking the
                // EasyInteraction (which has no aboveEvent) returns "nothing
                // to do" and the listener is never wired, leaving the
                // inspector showing a "Listeners: 0" warning on a prefab we
                // just built. Asking the EasyAudio rebuilds the whole chain.
                //
                // Functionally the listener is belt-and-braces: the trigger
                // fires EasyAudio directly through filter.onEvent. It exists
                // so the inspector tells the truth.
                easyAudio.OnValidate();
            }
            catch (Exception e)
            {
                Report(r, DecisionKind.Skipped,
                    "EasyEvent chain: the inspector's listener link was not rebuilt (" + e.Message
                  + "). The trigger still fires EasyAudio directly through the interaction filter.");
            }
        }

        // ── Import settings ─────────────────────────────────────────────

        /// <summary>
        /// Normalize a clip's import settings for 3D positional playback.
        /// Returns true when something changed and the asset was reimported.
        ///
        /// Forcing mono is the one that surprises people, so: a stereo clip
        /// played at spatialBlend 1 is collapsed by Unity anyway, badly — the
        /// two channels are summed after panning, so the stereo image becomes
        /// comb filtering rather than width — and it costs twice the memory to
        /// get there. Anything that genuinely wants stereo wants
        /// spatialBlend 0, which is not what this track builds.
        /// </summary>
        public static bool NormalizeAudioImport(string clipPath, float lengthSeconds,
                                                int sourceChannels, ConversionResult r)
        {
            var importer = AssetImporter.GetAtPath(clipPath) as AudioImporter;
            if (importer == null)
            {
                Report(r, DecisionKind.Skipped,
                    "no AudioImporter at '" + clipPath + "' — import settings left alone");
                return false;
            }

            var changes = new List<string>();

            // Guarded on the SOURCE, not on the flag. Unity's default for
            // forceToMono is false whatever the file's channel count, so
            // `if (!importer.forceToMono)` is true for every mono clip — which
            // forced a reimport that changed nothing and put "forced to mono"
            // in the report for a file that was always mono. In a tool whose
            // entire value is that its report is true, that is not cosmetic.
            if (sourceChannels > 1 && !importer.forceToMono)
            {
                importer.forceToMono = true;
                changes.Add("forced to mono (was " + sourceChannels + " channels; stereo spatializes badly "
                          + "and doubles the memory)");
            }

            AudioImporterSampleSettings settings = importer.defaultSampleSettings;
            bool settingsChanged = false;

            // Format and quality are checked SEPARATELY. A clip already
            // imported as Vorbis at Unity's importer default of 1.0 — which is
            // very nearly PCM-sized, and is the exact case VorbisQuality exists
            // to fix — used to keep quality 1.0 and be reported as needing no
            // compression change at all, because the quality write lived inside
            // the format branch.
            if (settings.compressionFormat != AudioCompressionFormat.Vorbis)
            {
                settings.compressionFormat = AudioCompressionFormat.Vorbis;
                settingsChanged = true;
                changes.Add("compression → Vorbis");
            }

            // Only ever LOWERED, the same way TexturePlaneBuilder only ever
            // lowers maxTextureSize: a creator who deliberately dropped a
            // background loop to 0.3 keeps it.
            if (settings.quality > VorbisQuality)
            {
                changes.Add("Vorbis quality "
                          + settings.quality.ToString("0.##", CultureInfo.InvariantCulture) + " → "
                          + VorbisQuality.ToString("0.##", CultureInfo.InvariantCulture));
                settings.quality = VorbisQuality;
                settingsChanged = true;
            }

            AudioClipLoadType wanted = LoadTypeFor(lengthSeconds);
            if (settings.loadType != wanted)
            {
                settings.loadType = wanted;
                settingsChanged = true;
                changes.Add("load type → " + wanted + " (" + LoadTypeReason(lengthSeconds) + ")");
            }

            if (settingsChanged) importer.defaultSampleSettings = settings;

            if (changes.Count == 0)
            {
                Report(r, DecisionKind.Skipped, "audio import settings already correct — nothing reimported");
                return false;
            }

            EditorUtility.SetDirty(importer);
            importer.SaveAndReimport();

            Report(r, DecisionKind.Added, "audio import: " + string.Join(", ", changes.ToArray()));
            return true;
        }

        /// <summary>
        /// Load type by clip length. Short clips decode once and live as raw
        /// samples; medium clips hold their compressed bytes; long clips
        /// stream so they never occupy memory proportional to their length.
        /// </summary>
        public static AudioClipLoadType LoadTypeFor(float lengthSeconds)
        {
            if (lengthSeconds < DecompressUnderSeconds) return AudioClipLoadType.DecompressOnLoad;
            if (lengthSeconds <= CompressedUnderSeconds) return AudioClipLoadType.CompressedInMemory;
            return AudioClipLoadType.Streaming;
        }

        private static string LoadTypeReason(float lengthSeconds)
        {
            if (lengthSeconds < DecompressUnderSeconds)
                return "under " + DecompressUnderSeconds.ToString("0.##", CultureInfo.InvariantCulture)
                     + " s, so the per-play decode costs more than the raw samples";
            if (lengthSeconds <= CompressedUnderSeconds)
                return "under " + CompressedUnderSeconds.ToString("0.##", CultureInfo.InvariantCulture)
                     + " s, so the compressed bytes are cheap to keep resident";
            return "over " + CompressedUnderSeconds.ToString("0.##", CultureInfo.InvariantCulture)
                 + " s, so it streams instead of sitting in memory";
        }

        // ── Post-save ───────────────────────────────────────────────────

        /// <summary>
        /// With useColliderBounds off, PropTemplate falls through to
        /// customFootprintMeters — and its field default is (1, 1), which is
        /// a metre of claimed floor for something that occupies none. Write
        /// the emitter's real footprint instead.
        /// </summary>
        private static void WriteEmitterFootprint(string prefabPath, ConversionResult r)
        {
            GameObject contents = null;
            try
            {
                contents = PrefabUtility.LoadPrefabContents(prefabPath);
                if (contents == null)
                {
                    Report(r, DecisionKind.Skipped,
                        "could not reopen " + prefabPath + " to set the emitter footprint — it keeps "
                      + "PropTemplate's 1 × 1 m default");
                    return;
                }

                var prop = contents.GetComponent<PropTemplate>();
                if (prop == null)
                {
                    Report(r, DecisionKind.Skipped, "no PropTemplate on the saved prefab — footprint not written");
                    return;
                }

                prop.customFootprintMeters = new Vector2(EmitterFootprintMeters, EmitterFootprintMeters);
                prop.footprintOffsetMeters = Vector2.zero;

                PrefabUtility.SaveAsPrefabAsset(contents, prefabPath);
                AssetDatabase.ImportAsset(prefabPath, ImportAssetOptions.ForceSynchronousImport);

                Report(r, DecisionKind.Measured, string.Format(CultureInfo.InvariantCulture,
                    "footprint: {0:0.##} × {0:0.##} m — an emitter is a point, not a room",
                    EmitterFootprintMeters));
            }
            catch (Exception e)
            {
                Report(r, DecisionKind.Skipped, "could not write the emitter footprint — " + e.Message);
            }
            finally
            {
                if (contents != null) PrefabUtility.UnloadPrefabContents(contents);
            }
        }

        // ── Internals ───────────────────────────────────────────────────

        private static void DestroyWorkingRoot(GameObject root)
        {
            if (EditorUtility.IsPersistent(root)) return;

            // Undo.DestroyObjectImmediate rather than DestroyImmediate: the
            // hierarchy was registered with Undo.RegisterCreatedObjectUndo,
            // and destroying it outside the undo system leaves the creator's
            // Cmd-Z pointing at an object that no longer exists.
            Undo.DestroyObjectImmediate(root);
        }

        private static void Report(ConversionResult r, DecisionKind kind, string message)
        {
            if (r == null) return;
            switch (kind)
            {
                case DecisionKind.Measured:  r.Measured(message);  break;
                case DecisionKind.Guessed:   r.Guessed(message);   break;
                case DecisionKind.Added:     r.Added(message);     break;
                case DecisionKind.Extracted: r.Extracted(message); break;
                case DecisionKind.Skipped:   r.Skipped(message);   break;
                default:                     r.Failed(message);    break;
            }
        }
    }
}
#endif
