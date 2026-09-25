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
    public sealed class DreamParkPackageManifest : ScriptableObject
    {
        public const int CurrentSchemaVersion = 3;
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
    }

    [Serializable]
    public sealed class PackageRecipe
    {
        public string kind;
        public string playerAddress;
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
    }
}
