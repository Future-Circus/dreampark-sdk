using System;
using System.Collections.Generic;
using UnityEngine;

namespace DreamPark
{
    /// <summary>
    /// Catalog-scoped, addressable runtime recipe for the package modes.
    /// An absent recipe is different from a present recipe with no stages.
    /// The editor organizer's GUIDs are deliberately not serialized here.
    /// </summary>
    public sealed class DreamParkPackageManifest : ScriptableObject, ISerializationCallbackReceiver
    {
        // Schema 4 adds layout-frame envelopes and the Arena's pinned frame per
        // occurrence. Schema 5 adds the optional PackageRecipe.phonePlayerAddress.
        // Readers accept 3 through 5; a schema-3 manifest has no frames, and a
        // manifest before 5 has no Phone Player (phones get no rig).
        public const int CurrentSchemaVersion = 5;
        public const int MinimumSupportedSchemaVersion = 3;

        public static bool IsSupportedSchema(int version)
            => version >= MinimumSupportedSchemaVersion && version <= CurrentSchemaVersion;
        public const string AssetName = "DreamPark Package Manifest";

        public int schemaVersion = CurrentSchemaVersion;
        public string contentId;
        // Deterministic digest of the compiled package recipes. Layout caches and
        // delivery planners use it to reject metadata from an older package without
        // loading a prefab merely to discover that the recipe changed.
        public string packageRevision;
        public PackageRecipe arena;
        public PackageRecipe adventure;
        public PackageRecipe sequence;

        public static string AddressFor(string id) => id + "/Assets/" + AssetName;

        public PackageRecipe RecipeFor(string kind)
        {
            if (string.Equals(kind, "arena", StringComparison.OrdinalIgnoreCase)) return arena;
            if (string.Equals(kind, "adventure", StringComparison.OrdinalIgnoreCase)) return adventure;
            if (string.Equals(kind, "sequence", StringComparison.OrdinalIgnoreCase)) return sequence;
            return null;
        }

        // Unity cannot serialize a null [Serializable] field: an absent recipe is saved
        // as an EMPTY one (kind "", no occurrences) and comes back non-null on load. A
        // compiled recipe always has its kind, so an empty kind means "absent". Restore
        // null on every load, so every reader (the uploader's Web packages JSON, Core's
        // package selection and Adventure → Sequence → Arena fallback) sees an absent
        // package as absent. Without this, a title with no Adventure sent Web an empty
        // Adventure, which Web rejects ("adventure.attractions must not be empty").
        void ISerializationCallbackReceiver.OnBeforeSerialize() { }

        void ISerializationCallbackReceiver.OnAfterDeserialize()
        {
            if (IsAbsent(arena)) arena = null;
            if (IsAbsent(adventure)) adventure = null;
            if (IsAbsent(sequence)) sequence = null;
        }

        public static bool IsAbsent(PackageRecipe recipe) => recipe == null || string.IsNullOrEmpty(recipe.kind);
    }

    [Serializable]
    public sealed class PackageRecipe
    {
        public string kind;
        public string playerAddress;
        // Schema v5: the optional Phone Player rig ({id}/{prefab name}), shared
        // by every package like playerAddress. Empty = phone users get no rig
        // (they still get the Game Manager). Headsets always use playerAddress.
        public string phonePlayerAddress;
        public string gameManagerAddress;
        public string sequenceDefinitionAddress;

        // Schema v3: optional envelope for a package whose spatial root is not
        // represented by an occurrence. Sequence uses this for its generated
        // Start/Overlay root so planners do not have to load the definition.
        public bool hasPackingMetadata;
        public Vector2 authoredFootprintMeters;
        public Vector2 safeFootprintMeters;
        public Vector2 shrinkFootprintMeters;
        public Vector2 growFootprintMeters;
        public Vector2 essentialShrinkFootprintMeters;
        public Vector2 shrinkScale = Vector2.one;
        public Vector2 growScale = Vector2.one;
        public Vector2 essentialShrinkScale = Vector2.one;
        public float safeAreaInset;
        public bool hasEssentialProps;
        public List<PackageGroupOccurrence> groups = new List<PackageGroupOccurrence>();
        public List<PackageOccurrence> occurrences = new List<PackageOccurrence>();
        public List<ArenaSizeBucket> arenaBuckets = new List<ArenaSizeBucket>();
    }

    /// <summary>
    /// One rotation-normalized whole-foot Arena size. Occurrence ids are in
    /// creator priority order; the consumer chooses the largest compatible
    /// occupied bucket and then its first occurrence.
    /// </summary>
    [Serializable]
    public sealed class ArenaSizeBucket
    {
        public int widthFeet;
        public int lengthFeet;
        public List<string> occurrenceIds = new List<string>();
    }

    [Serializable]
    public sealed class PackageGroupOccurrence
    {
        public string occurrenceId;
        public string name;
        public int order;
    }

    [Serializable]
    public sealed class PackageOccurrence
    {
        public string occurrenceId;
        public string resourceAddress;
        public string role;
        public bool required;
        public int order;
        public string groupOccurrenceId;
        public int widthFeet;
        public int lengthFeet;

        // Schema v3: the complete, lightweight placement envelope copied from the
        // prefab's editor bake. Older manifests leave hasPackingMetadata false and
        // runtimes may retain their legacy prefab-inspection fallback.
        public bool hasPackingMetadata;
        public Vector2 authoredFootprintMeters;
        public Vector2 safeFootprintMeters;
        public Vector2 shrinkFootprintMeters;
        public Vector2 growFootprintMeters;
        public Vector2 essentialShrinkFootprintMeters;
        public Vector2 shrinkScale = Vector2.one;
        public Vector2 growScale = Vector2.one;
        public Vector2 essentialShrinkScale = Vector2.one;
        public float safeAreaInset;
        public bool hasEssentialProps;

        // Schema v4: the layout-frame ladder (largest -> smallest authored area,
        // Base included) when the prefab has frames; empty otherwise. frameId pins
        // one frame (Arena); empty lets the runtime ladder choose any frame.
        public string frameId;
        public List<PackageFrameEnvelope> frames = new List<PackageFrameEnvelope>();
    }

    /// <summary>
    /// One layout frame's baked range. Scales are relative to this frame's own
    /// authored footprint, not the Base attraction's.
    /// </summary>
    [Serializable]
    public sealed class PackageFrameEnvelope
    {
        public string frameId;
        public string kind;   // "base" | "custom" | "minimum"
        public string name;
        public Vector2 authoredFootprintMeters;
        public Vector2 shrinkFootprintMeters;
        public Vector2 growFootprintMeters;
        public Vector2 shrinkScale = Vector2.one;
        public Vector2 growScale = Vector2.one;
    }
}
