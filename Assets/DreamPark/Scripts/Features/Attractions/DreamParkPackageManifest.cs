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

        // Playlists (Docs/PLAYLISTS.md, Oct 2026). ADDITIVE: schemaVersion above
        // stays 5 because every shipped app refuses a manifest newer than its own
        // CurrentSchemaVersion; old apps skip these unknown fields. 0 = the title
        // has no playlists (and the lists are empty); see PackagePlaylist.SupportedSchema.
        public int playlistsSchemaVersion;
        public List<PlaylistKind> playlistKinds = new List<PlaylistKind>();
        public List<PackagePlaylist> playlists = new List<PackagePlaylist>();
        /// § 15.1 the title's `campaigns` / `flags` as JSON array TEXT ("" = none). Old apps ignore them.
        public string playlistCampaignsJson = "";
        public string playlistFlagsJson = "";
        /// § 6.0a the title's playlist LAYOUT: "" / "tour" = each playlist run plans its own arc (default);
        /// "club" = one stable layout of all the title's attractions per space, every playlist routes into it
        /// (DreamSports). Old apps ignore it.
        public string playlistsLayout = "";
        /// repair-oct20 CONTRACT § 2: the title's DEFAULT GOAL BLOCKS, compiled from
        /// `Assets/Content/<Title>/.dreampark-goals/<attractionId>.<kind>.lua` as a JSON object TEXT
        /// {"<attractionId>.<kind>": "<lua>"} ("" = none). Shipped so they work offline; the backend copy wins online.
        public string playlistGoalsJson = "";
        public bool IsClub => string.Equals(playlistsLayout, "club", StringComparison.OrdinalIgnoreCase);
        /// WAYS TO PLAY (ways-to-play contract §3, Oct 25 2026): the title's director scripts
        /// (`Content/<Title>/Runners/<id>.lua.txt`, header fields from its `DreamPark.runner{}`), filled by
        /// DreamParkPackageCompiler. ADDITIVE — old apps ignore it; empty = the built-in default runner (RunnerDefaults).
        /// The same scripts are also addressable TextAssets at "{gameId}/Runners/{runnerId}" (RunnerHost.LoadRunnerAsync).
        public List<RunnerRecord> runners = new List<RunnerRecord>();
        /// WAYS TO PLAY (Oct 26 2026): levels only a runner loads (prefabs under Content/&lt;Title&gt;/Runners/ at
        /// "{gameId}/Runners/Levels/{name}", plus the Sequence's Start / Game Over levels). ADDITIVE — old apps ignore it.
        public List<PackageOccurrence> runnerLevels = new List<PackageOccurrence>();
        // One occurrence (full packing envelope) per distinct stop/alternate prefab
        // of every shipped playlist, plus the shared Player/Game Manager slots, so
        // a packer treats a playlist like any recipe. Stops point in by occurrenceId.
        // kind "playlist"; null when the title has no playlists.
        public PackageRecipe playlist;

        public bool HasPlaylists => PackagePlaylist.SupportedSchema(playlistsSchemaVersion)
            && playlists != null && playlists.Count > 0;

        /// <summary>The title's playlist words; "Adventure/Adventures" when it declares none.</summary>
        public List<PlaylistKind> PlaylistKindsOrDefault()
            => playlistKinds != null && playlistKinds.Count > 0
                ? playlistKinds : new List<PlaylistKind> { PlaylistKind.Default() };

        public PackagePlaylist FindPlaylist(string id)
        {
            if (!HasPlaylists || string.IsNullOrEmpty(id)) return null;
            foreach (PackagePlaylist playlist in playlists)
                if (playlist != null && string.Equals(playlist.id, id, StringComparison.Ordinal)) return playlist;
            return null;
        }

        public PlaylistKind KindFor(PackagePlaylist playlist)
        {
            List<PlaylistKind> kinds = PlaylistKindsOrDefault();
            foreach (PlaylistKind kind in kinds)
                if (kind != null && playlist != null
                    && string.Equals(kind.id, playlist.kind, StringComparison.OrdinalIgnoreCase)) return kind;
            return kinds[0];
        }

        public static string AddressFor(string id) => id + "/Assets/" + AssetName;

        public PackageRecipe RecipeFor(string kind)
        {
            if (string.Equals(kind, "arena", StringComparison.OrdinalIgnoreCase)) return arena;
            if (string.Equals(kind, "adventure", StringComparison.OrdinalIgnoreCase)) return adventure;
            if (string.Equals(kind, "sequence", StringComparison.OrdinalIgnoreCase)) return sequence;
            if (string.Equals(kind, "playlist", StringComparison.OrdinalIgnoreCase)) return playlist;
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
            if (IsAbsent(playlist)) playlist = null;
            if (playlistKinds == null) playlistKinds = new List<PlaylistKind>();
            if (playlists == null) playlists = new List<PackagePlaylist>();
            if (runners == null) runners = new List<RunnerRecord>();
            if (runnerLevels == null) runnerLevels = new List<PackageOccurrence>();
            // The title's default goal blocks join the runtime library as the manifest loads (thread-safe registry).
            if (!string.IsNullOrEmpty(playlistGoalsJson) && !string.IsNullOrEmpty(contentId))
                PlaylistGoalLibrary.RegisterManifest(contentId, playlistGoalsJson);
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
        /// Playlist BONUS content (optional, never a runner stop): every device labels it "Bonus" (core PlaylistBonusLabel).
        public bool bonus;
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
        /// True ground footprint (Base) at 1× / at shrink (zero = not measured). Packers reserve max(declared, ground).
        public Vector2 groundFootprintMeters;
        public Vector2 groundShrinkFootprintMeters;
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

        /// ENTRIES / EXITS (Oct 19 2026): the Base layout's portals (AttractionTemplate.GetPortals("base")), so planners
        /// turn each attraction's real entry toward the player's approach. hasPortals false (older manifests) = the
        /// SDK default, one entry in the middle of −Z. Each frame envelope carries its own (frames may override).
        public bool hasPortals;
        public List<PackagePortal> portals = new List<PackagePortal>();
    }

    /// <summary>One entry / exit: AttractionPortal's kind (0 Entry, 1 Exit, 2 Both) and 0–1 perimeter position
    /// (0 = middle of −Z, 0.25 = middle of −X, 0.5 = middle of +Z, 0.75 = middle of +X).</summary>
    [Serializable]
    public sealed class PackagePortal
    {
        public int kind;
        public float position;
        public bool IsEntry => kind == 0 || kind == 2;
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
        /// True ground footprint at 1× / at shrink (zero = not measured: the declared sizes stand).
        public Vector2 groundFootprintMeters;
        public Vector2 groundShrinkFootprintMeters;
        /// This frame's effective entries / exits (its override, else Base's); meaningful when the occurrence hasPortals.
        public List<PackagePortal> portals = new List<PackagePortal>();
    }

    // ── Playlists (Docs/PLAYLISTS.md) ────────────────────────────────────────
    // The SAME classes deserialize the authored .dreampark-playlists.json and
    // ship in the manifest; the compiler fills the "compiled" fields.

    /// <summary>A title's word for a kind of playlist ("World", "Mission", "Arena").</summary>
    [Serializable]
    public sealed class PlaylistKind
    {
        public string id;
        public string singular;
        public string plural;

        public static PlaylistKind Default()
            => new PlaylistKind { id = "adventure", singular = "Adventure", plural = "Adventures" };
    }

    /// <summary>An authored, complete experience with an arc (start → climax → ending).</summary>
    [Serializable]
    public sealed class PackagePlaylist
    {
        public const int CurrentSchemaVersion = 1;
        public static bool SupportedSchema(int version) => version >= 1 && version <= CurrentSchemaVersion;

        public static readonly string[] Beats = { "opening", "rising", "climax", "finale" };
        public static readonly string[] Roles = { "start", "stage", "miniboss", "boss", "end" };
        public static readonly string[] Objectives = { "reach_exit", "defeat_boss", "score", "collect", "survive", "time" };
        // "players" (PLAYLISTS.md § 11.3): seated where most players stand when its leg starts.
        public static readonly string[] Placements = { "entrance", "biggest", "outdoor", "far", "any", "players" };
        public static readonly string[] Transitions = { "walk_on", "swap_in_place" };
        public static readonly string[] Owners = { "title", "platform", "user" };
        // `placings` (§ 7.8): points per finishing place across a run's races (CrashCourse cups).
        public static readonly string[] Scorings = { "", "sum", "best", "elimination", "placings" };
        public static readonly string[] Advances = { "on_complete", "on_timer" };

        public string id;
        // Cross-title by design: "title" (shipped in titleId's package),
        // "platform" (curated on Web, may mix titles), "user" (reserved).
        public string owner = "title";
        public string titleId;
        public string kind;
        public string name;
        public string pitch;
        public string art;
        public List<string> tags = new List<string>();
        public List<string> badges = new List<string>();
        public PlaylistFit fit = new PlaylistFit();
        public PlaylistRules rules = new PlaylistRules();
        public List<PlaylistLeg> arc = new List<PlaylistLeg>();
        /// PLAYLIST-PLAN § 3 (pl-sets, Oct 21): the ATTRACTION POOL — each attraction with the challenge it offers and its
        /// options. A record with a pool and no arc is a SETS playlist: its `lua` decides what is out (run.show). An arc[]
        /// record still plays (its rounds are converted to run.show per round on load). Old apps ignore it.
        public List<PlaylistStop> pool = new List<PlaylistStop>();

        // Chapters & unlocks (PROPOSAL, Oct 18 2026, PLAYLISTS.md § 2.1; field names follow the critters-story
        // `requires`/`sets` gates and playlist-next's `continueWith`). All optional; empty = today's behaviour
        // (always open, no suggested next). Old apps ignore them.
        /// The suggested next playlist id(s) of this title, best first.
        public List<string> continueWith = new List<string>();
        /// Unlock gate: every ref must hold — "playlist:<id>" (finished), "badge:<id>" (earned), "flag:<id>" (set). Empty = always open.
        public List<string> requires = new List<string>();
        /// Story flags this playlist sets when it is completed ("flag ids", no prefix).
        public List<string> sets = new List<string>();
        /// Optional chapter label for the campaign map ("Chapter 1: The Meadow").
        public string chapter;
        /// § 15.1 gates at FULL fidelity, as JSON text ("" = none): `requires` / `personal` entry arrays (strings,
        /// {badge}, {flag}, {flag, is}, {playlist}, {min, of}), `sets` {flags, badges} and `window` {from, until, label}.
        /// `requires` / `sets` above are the Studio's projection ("playlist:/badge:/flag:" refs, flag ids); these
        /// text fields win when present (ContentPlaylistStore.AuthoringAsText fills them from the authored file).
        public string requiresJson, setsJson, windowJson, personalJson;
        /// NO BONUS (§ 6.0, Oct 19 2026): true = no round of this playlist puts bonus in its spare rooms. Without it the
        /// planner still leaves them empty by default for PvP, duels, races and short scored rounds.
        public bool noBonus;
        /// WHOLE-SPACE ARENA (Oct 19 2026, LaserTag PvP Arena): the playlist turns the whole space into ONE arena. The
        /// rooms its rounds fill from `bonusLevels` are part of that arena, not side content: no "Bonus" label, and the
        /// title runs them as one game (e.g. one venue-wide round director and scoreboard).
        public bool wholeSpaceArena;

        // repair-oct20 (CONTRACT § 3 / § 5): the playlist's behaviour + presentation, carried as TEXT so every field the
        // SDK sends round-trips (JsonUtility cannot hold free-form objects). All optional, "" = none; old apps ignore them.
        /// The playlist's own Lua (`lua`): return { on_start, on_stop_result, choose_next, on_finale }.
        public string lua;
        /// `dreamie` {story, beats, react, avoid} / `timing` {totalMin, rounds{legId: min}, mode} / `story` / `layout` /
        /// `campaign` as JSON text.
        public string dreamieJson, timingJson, storyJson, layoutJson, campaignJson;
        /// The backend record's revision / provenance (CONTRACT § 5): rev, source (authored|generated|edited), generatedBy.
        public string rev, source, generatedBy;
        /// The authored playlist object VERBATIM (JSON text): every field, known or not, passes through (portal, Studio,
        /// the backend import) even when this struct has no slot for it.
        public string sourceJson;
        /// Aidan, Oct 20: the playlist's word for its rounds ({one:"Race", many:"Races"}; World / Game / Match / Mission).
        /// Null / empty = Round / Rounds. Every player-facing round label and Dreamie's context use it.
        public PlaylistRoundWord roundWord;
        /// repair-oct20 § 8 (record v2): "dreampark.playlist/2" once upgraded (PlaylistRecordV2.Schema); "" = a v1 record.
        public string schema;
        /// § 8.2: the minimum title release this playlist needs (its attractions, events, goal blocks); 0 = any. A device
        /// whose installed release is older hides it from pickers and PlaylistRun refuses it.
        public int requiresRelease;
        /// § 8.3: how the group plays it (Results: coop = team total + contributions; versus / party = a ranking) and the
        /// group size it is for. Replaces fit.minPlayers (still read; mirrored there for older apps).
        public PlaylistPlay play = new PlaylistPlay();

        // Compiled: the sum of the legs' minutes.
        public int totalMinutes;

        /// <summary>The climax leg (exactly one in a valid playlist), else null.</summary>
        public PlaylistLeg Climax()
        {
            if (arc == null) return null;
            foreach (PlaylistLeg leg in arc)
                if (leg != null && string.Equals(leg.beat, "climax", StringComparison.Ordinal)) return leg;
            return null;
        }
    }

    /// A playlist's word for its rounds (`roundWord {one, many}`), each ≤ 16 characters.
    [Serializable]
    public sealed class PlaylistRoundWord
    {
        public const int MaxChars = 16;
        /// No word by default (§ 2.3: players never see "round" / "leg" / "set"): a playlist that names none shows its parts'
        /// own names. An authored "Round" (the old default) counts as none.
        public const string DefaultOne = "", DefaultMany = "";
        const string LegacyOne = "Round", LegacyMany = "Rounds";
        public string one;
        public string many;
        public bool IsDefault => string.IsNullOrWhiteSpace(one) || string.Equals(one.Trim(), LegacyOne, StringComparison.OrdinalIgnoreCase);
        /// (one, many): none (or the legacy "Round") → ("", ""); `many` empty → one + "s".
        public static (string one, string many) Of(PlaylistRoundWord w)
        {
            string one = (w?.one ?? "").Trim();
            if (one.Length == 0 || string.Equals(one, LegacyOne, StringComparison.OrdinalIgnoreCase)) return (DefaultOne, DefaultMany);
            string many = (w?.many ?? "").Trim();
            return (one, many.Length > 0 ? many : one + "s");
        }
    }

    /// § 8.3 `play {mode, players: [min, max]}`: solo | coop | versus | party; "" = not authored (Results as before).
    [Serializable]
    public sealed class PlaylistPlay
    {
        public string mode = "";
        public List<int> players = new List<int>();
        public int Min => players != null && players.Count > 0 ? players[0] : 0;
        public int Max => players != null && players.Count > 1 ? players[1] : 0;
        public bool IsEmpty => string.IsNullOrEmpty(mode) && (players == null || players.Count == 0);
        /// A group of n players may play it (0 / unknown = yes).
        public bool Fits(int n) => n <= 0 || ((Min <= 0 || n >= Min) && (Max <= 0 || n <= Max));
    }

    [Serializable]
    public sealed class PlaylistFit
    {
        public int minRooms;
        public float minAreaM2;
        public bool indoor = true;
        public bool outdoor = true;
        public int minPlayers;   // 0 = any number (PvP Showdown = 2)
    }

    /// <summary>Optional fusion layer; playlist-runner owns the meaning (PLAYLISTS.md § 7).</summary>
    [Serializable]
    public sealed class PlaylistRules
    {
        public int timerSec;                   // per-stop limit, 0 = none
        public string scoring = "";            // "" | sum | best | elimination
        public int lives;                      // 0 = unlimited
        public string advance = "on_complete"; // on_complete | on_timer
        public string script = "";             // reserved: playlist-level Lua script reference
        public string navigation = "";         // repair-oct20: "" = the playlist owns navigation | "title" (freeplay)

        public bool IsDefault => timerSec == 0 && string.IsNullOrEmpty(scoring) && lives == 0
            && (string.IsNullOrEmpty(advance) || advance == "on_complete") && string.IsNullOrEmpty(script);
    }

    [Serializable]
    public sealed class PlaylistLeg
    {
        public string id;
        public string name;
        public string beat;      // opening | rising | climax | finale
        /// The round's ESTIMATE (record v2 `estMinutes`; v1 `minutes` is read too) for cards and the queue. Hard caps
        /// live in the playlist's `timing`. Kept under this name: older apps read `minutes` from the manifest.
        public int minutes;
        public string next = "walk_on";  // walk_on | swap_in_place (ignored on the last leg)
        // The title(s) this leg needs loaded. v1: exactly one (a title switch
        // happens only at a leg boundary). mixed = several titles in one leg: reserved.
        public List<string> titles = new List<string>();
        public bool mixed;
        /// v1 only: a v2 record has no round objective — readers DERIVE it (PlaylistRecordV2.DeriveObjective) so the
        /// runner and older apps (the release path's packages.playlists) keep one. A v1 objective the derivation cannot
        /// reproduce (a timed / scored round) is kept by the upgrade.
        public PlaylistObjective objective = new PlaylistObjective();
        public List<PlaylistStop> stops = new List<PlaylistStop>();
        /// Optional authored goal line for the whole leg ("Clear the meadow, then beat King Jubbo!", ≤ 120);
        /// "" = the runner writes it from the objective (PLAYLISTS.md § 7.4 PlaylistObjectiveText).
        public string objectiveText;
        /// AUTHORED BONUS (§ 6.0, Oct 19 2026): this round's bonus levels, best first — attractions of the leg's
        /// title (prefab name or resourceName) that fill the rooms the round's stops leave free, BEFORE the automatic
        /// on-theme picks (each at the largest frame that fits, facing its approach). Repeats across rounds are fine.
        /// Empty = automatic picks only.
        public List<string> bonusLevels = new List<string>();
        /// A boss (a boss/miniboss stop of any of the title's playlists) may be listed in bonusLevels only when true.
        public bool bonusBosses;
        /// NO BONUS for this round: its spare rooms stay empty (wins over bonusLevels). Absent, PvP / duel / race /
        /// short scored rounds are bonus-free by default unless they author bonusLevels.
        public bool noBonus;
        /// repair-oct20: the round's optional `scene` / `choice` objects as JSON text ("" = none).
        public string sceneJson, choiceJson;
        /// § 8.5: this round's Dreamie guidance `dreamie {guide, react[]}` as JSON text ("" = none).
        public string dreamieJson;
    }

    [Serializable]
    public sealed class PlaylistObjective
    {
        public string type;      // reach_exit | defeat_boss | score | collect | survive | time
        public string target;    // optional: the stop it is about (an authored attraction ref)
        public int count;        // score / collect
        public int seconds;      // survive / time

        // Compiled: target's runtime address ("" when there is no target).
        public string targetAddress;
        /// Record v2: derived by the reader (PlaylistRecordV2.DeriveObjective), never authored — validators skip its
        /// count / seconds rules. Not serialized.
        [NonSerialized] public bool derived;
    }

    [Serializable]
    public sealed class PlaylistStop
    {
        public string contentId;     // the stop's title (= the owning title in a title file)
        public string attraction;    // authored: prefab file name or Web resourceName
        public string role;          // start | stage | miniboss | boss | end
        public List<string> alternates = new List<string>();
        public PlaylistPlacement placement = new PlaylistPlacement();
        /// Opaque per-stop options for the title's Lua (PLAYLISTS.md § 2 / § 7.4: mode, difficulty, holes,
        /// frames, seed …) as JSON object TEXT ("" = none). Authored as an object; the platform never reads
        /// it — the runner hands it to `start_challenge(params).options` / `dp.playlist.stop().options`.
        public string options;
        /// Optional authored goal line for this stop ("Collect all 7 coins!", ≤ 120); "" = generated (§ 7.4).
        public string objectiveText;
        /// Optional player-facing name of this stop ("King Jubbo", ≤ 60); "" = the catalog name / prefab leaf.
        public string displayName;
        /// Optional challenge kind of this stop (PLAYLISTS.md § 12.1 PROPOSAL: collect, reach_end, boss, clear, survive,
        /// score, race, versus, catch, find, solve, protect, moment); "" = derived from the role + leg objective.
        public string challenge;
        /// Optional bonus modifiers (§ 12.1 PROPOSAL: no_hit, combo, clock; 0–2): a bonus line, never required.
        public List<string> bonus = new List<string>();
        /// Optional one-line hint shown/said as the stop goes live ("Shoot the glowing weak spot!", ≤ 120);
        /// "" = derived from the challenge catalog (§ 12).
        public string introHint;
        /// Optional stop id within its round ("" = none): what another stop's `lockedUntil` may name.
        public string id;
        /// AUTHORED LOCK (§ 6.0a, Oct 19): this stop stays locked until these stops of the SAME round are done — each
        /// a sibling's `id` or attraction. Empty = never locked (the default). For stops that would break the game if
        /// entered early (a round-ending boss is the typical use); never on bonus content.
        public List<string> lockedUntil = new List<string>();
        /// repair-oct20 CONTRACT § 2: the stop's goal. "" / "default" = the title's library block for its challenge kind;
        /// otherwise the inline goal-block Lua (authored `goal: {"lua": "…"}`).
        public string goal;
        /// § 8.6: the force field's sign while `lockedUntil` holds the stop ("Clear Mole Trench to open the cannon!");
        /// "" = the platform's "Finish … to unlock".
        public string lockSign;
        /// § 8.5: this stop's Dreamie guidance `dreamie {guide, intro, react[]}` as JSON text ("" = none).
        public string dreamieJson;

        /// § 12.1: the catalog's challenge kinds (+ `moment`) and bonus modifiers.
        public static readonly string[] Challenges = { "collect", "reach_end", "boss", "clear", "survive", "score",
            "race", "versus", "catch", "find", "solve", "protect", "moment", "crossing", "jump" };
        public static readonly string[] Bonuses = { "no_hit", "combo", "clock" };
        public const int MaxBonuses = 2;

        // Compiled: Addressable keys ({id}/Levels/{size}/{leaf}, like
        // PackageOccurrence.resourceAddress) and the Web catalog key.
        public string resourceAddress;
        public List<string> alternateAddresses = new List<string>();
        public string resourceName;
        public List<string> alternateResourceNames = new List<string>();
        // Compiled: Web catalog ids (slug of resourceName); {contentId, attractionId} is a stop's wire identity.
        public string attractionId;
        public List<string> alternateAttractionIds = new List<string>();
        // Compiled: ids into DreamParkPackageManifest.playlist.occurrences
        // (packing envelope, frames, size) for the stop and each alternate.
        public string occurrenceId;
        public List<string> alternateOccurrenceIds = new List<string>();
    }

    [Serializable]
    public sealed class PlaylistPlacement
    {
        public string prefer = "any";  // entrance | biggest | outdoor | far | any
    }
}
