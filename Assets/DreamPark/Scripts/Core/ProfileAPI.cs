// ─────────────────────────────────────────────────────────────────────────
//  ProfileAPI.cs
//
//  DreamPark SDK — User Profile / Inventory / Achievements / Badges
//
//  Cross-game profile data API. Backed by /app/profile/* on the backend.
//  Identity is bound by the QR pairing flow in core (see
//  Assets/Scripts/ProfilePairingHandler.cs in dreampark-core) and cleared
//  automatically on session-end / sleep / cycle.
//
//  All gameplay scripts (C# or Lua) should hit this class — never the raw
//  HTTP endpoints — so that responses are cached, requests don't duplicate,
//  and behavior degrades gracefully when no user is bound (SDK preview /
//  guest play).
//
//
//  ── WRITES REQUIRE THE PLAYER TO ENTER YOUR ATTRACTION FIRST ──
//  A guest walking around a venue has not opted into every game installed
//  there. Writes are therefore held by ContentGate until the player
//  physically steps into a GameArea belonging to this content; the moment
//  they do, everything queued is sent in order and every later write goes
//  straight through for the rest of that identity. Walking back out does
//  not close it again — it is a one-time entry, not a presence check.
//
//  Reads are never gated. Neither is session playtime reporting.
//
//  In practice this is invisible: the natural place to write is inside the
//  attraction the guest is standing in. It matters for park-wide systems on
//  Player.prefab that write on start(), and in the editor where there may
//  be no GameArea to walk into (editor sessions auto-open the gate — see
//  ContentGate.AutoOpenInEditor).
//
//  Calling pattern (C#):
//      ProfileAPI.FetchProfile("dragon-raid", (ok, profile) => { ... });
//      var wand = ProfileAPI.GetItemByType("wand");          // sync cache read
//      ProfileAPI.AwardItem("rune_fire", 1);                 // fire and forget
//
//  Calling pattern (Lua):
//      dp.profile.onReady(function()
//          local wand = dp.profile.getItemByType("wand")
//          if wand then ... end
//      end)
// ─────────────────────────────────────────────────────────────────────────

using System;
using System.Collections.Generic;
using Defective.JSON;
using UnityEngine;
using XLua;
using APIResponse = DreamPark.API.DreamParkAPI.APIResponse;

namespace DreamPark.API
{
    /// <summary>
    /// Catalog + entry data for one inventory item, achievement, or badge.
    /// Fields are merged from the user's instance + the catalog doc.
    /// </summary>
    [System.Serializable]
    public class ProfileItem
    {
        public string itemId;        // base item id ("wand", "rune_fire")
        public string instanceId;    // unique key in inventory map (often itemId + suffix)
        public string baseItemId;    // base id stripped of suffix
        public string name;
        public string type;          // catalog "type" field — used for GetItemByType
        public string contentId;     // game/content scoping ("dragon-raid", "wizardsway-onboarding")
        public string gameId;        // legacy synonym for contentId
        public string rarity;
        public string iconUri;
        public string modelUri;
        public int amount = 1;
        public JSONObject metadata;  // free-form per-instance data
        public JSONObject raw;       // original JSON for advanced consumers
    }

    [System.Serializable]
    public class ProfileAchievement
    {
        public string achievementId;
        public float progress;
        public bool completed;
        public float maxValue;
        public string name;
        public string contentId;
        public JSONObject raw;
    }

    [System.Serializable]
    public class ProfileBadge
    {
        public string badgeId;
        public string name;
        public string contentId;
        public string iconUri;
        public string description;
        public double awardedAtMs;
        public JSONObject raw;
    }

    [LuaCallCSharp]
    public static class ProfileAPI
    {
        // ── Identity ─────────────────────────────────────────────────────
        public static string BoundUserId      { get; private set; }
        public static string BoundDreamId { get; private set; }
        public static string ContentFilter    { get; private set; }
        public static bool   IsBound          => !string.IsNullOrEmpty(BoundUserId) || !string.IsNullOrEmpty(BoundDreamId);
        public static bool   IsLoaded         { get; private set; }

        // ── Display identity (from the snapshot's `profile` block) ──────────
        // The bound account's displayName / avatarUrl / email. Null for an
        // unclaimed anonymous DreamID (IsAnonymous = true). Hydrated on every
        // profile load, cleared on ClearIdentity. This is what UI like the
        // DreamBand reads — NOT AuthAPI (which is the separate session path).
        public static string DisplayName { get; private set; }
        public static string AvatarUrl   { get; private set; }
        public static bool   IsAnonymous { get; private set; }

        // Physical height belongs to the selected sub-profile, just like the
        // display name and avatar. The backend sends absolute inches; the SDK
        // derives metres and a dimensionless scale from that one source of
        // truth. Content authored for a 5'8" adult can multiply a vertical
        // offset by HeightFactor without knowing anything about profile JSON.
        public const float MinimumSupportedHeightInches = 24f;
        public const float MaximumSupportedHeightInches = 96f;
        public const float ReferenceHeightInches        = 68f;
        public const float InchesToMeters               = 0.0254f;
        public static float HeightInches { get; private set; } = ReferenceHeightInches;
        public static float HeightMeters => HeightInches * InchesToMeters;
        public static float HeightFactor => HeightInches / ReferenceHeightInches;

        // Player wallet — populated from the snapshot's `dreamPoints` field
        // when bound to a user account. Always 0 for anonymous DreamID
        // bindings (the backend doesn't issue points to unclaimed identities).
        public static int    DreamPoints      { get; private set; }

        // Server-issued preview key returned by /api/pairing/preview-claim
        // when BindToLoggedInUser succeeds. Sent as `Bearer <key>` on every
        // /app/profile/* write in SDK preview mode. Scoped server-side to
        // this developer's own uid — a leaked preview key can't touch any
        // other user's profile. Null in production (headset) builds, which
        // authenticate with the real Unity API key instead.
        static string _previewKey;

        // Which backend route family to hit. Headset = /app/profile/* with
        // the Unity API key + server-side headset binding (production path
        // on the Quest at a park). LoggedInUser = /app/profile/* with a
        // server-issued preview key (SDK preview / editor testing against
        // the developer's own account without needing to scan a QR).
        public enum ProfileSource { Headset, LoggedInUser }
        public static ProfileSource Source { get; private set; } = ProfileSource.Headset;

        /// <summary>Where the player physically is, stamped onto badge awards
        /// so the platform can answer "which park did I earn this at" — the
        /// question that makes a badge shelf worth having.
        ///
        /// The SDK's own park id (SessionContext.LocationId) is written by the
        /// kiosk check-in and is EMPTY in the embedded iOS runtime, which
        /// learns its park from the Swift LOAD_PARK bridge instead. So the host
        /// app supplies the truth here and SessionContext is only the fallback
        /// — one property that is right on both platforms beats two award
        /// paths. Core sets it from AdventureLedger's park poll, the same
        /// resolution that opens a visit.
        ///
        /// Null/empty means "don't know" and the award omits the field; the
        /// server then falls back to the bound session's location.</summary>
        public static string ParkContext { get; set; }

        /// <summary>Which PLACE inside that park, when the park has more than
        /// one. Set alongside ParkContext by the host; ALREADY NORMALISED by
        /// the caller, so empty/null means the default place and the award
        /// omits the field entirely — absent is how the platform spells "the
        /// default place", and sending an empty string instead would make a
        /// sharded read and an unsharded one look different on the wire.
        ///
        /// No SessionContext fallback: the kiosk session has no notion of
        /// places, so guessing one would be inventing data.</summary>
        public static string ShardContext { get; set; }

        // internal, not private: ScoreAPI stamps the same parkId onto score
        // submissions that AwardBadge stamps onto badges (same "where was
        // this earned" question) — reusing this method is what keeps the
        // two from resolving park differently if the fallback rule ever
        // changes.
        internal static string CallingParkId()
        {
            if (!string.IsNullOrEmpty(ParkContext)) return ParkContext;
            return SessionContext.LocationId;
        }

        // ── Awards earned in the gap before an identity lands ─────────────
        // A write with no binding used to be dropped with a warning. On iOS
        // that gap is routine, not exotic: the Swift app sends PROFILE_PAIR and
        // the claim round-trips while the player is already standing in a park,
        // so a badge earned in the first seconds of a session was simply lost.
        //
        // Held ONLY while a claim for a KNOWN identity is in flight, and that
        // restriction is the entire safety argument: on an LBE headset a guest
        // who never pairs must not have their award replayed onto whoever pairs
        // next, and "a claim is already running" is the one signal saying the
        // person about to bind is the person who just earned it. A guest with
        // no claim keeps today's behaviour — dropped, and warned about.
        // Widening this to unpaired guests is a product rule, not a code
        // change: it is the same question as the ledger's adoptedGuestId gap
        // and needs an explicit call before anyone implements it.
        public static bool BindPending { get; private set; }
        const int   MaxHeldAwards   = 16;
        const float HeldAwardTtlSec = 90f;
        // contentId is captured when the award is MADE, not when it is sent —
        // see AwardBadge for why replaying it with a freshly-resolved one
        // would attribute the badge to whatever the player wandered into
        // while the claim was in flight.
        struct HeldAward { public string badgeId; public string contentId; public Action<bool, ProfileBadge> done; public float atSec; public long calledAtMs; }
        static readonly List<HeldAward> _heldAwards = new List<HeldAward>();

        // ── Guest writes (Oct 13 2026, DreamPark-Web docs/contracts/guest-transfer.md) ──
        // An UNBOUND headset with a guest id no longer drops (or only holds in
        // memory) what the guest earns. Badge, achievement, item, score and
        // game-storage writes are SENT with `guestId` (top-level body field;
        // a query param on GETs) and the server files them under the anonymous
        // DreamID temp_sessions/{guestId}. When the guest signs in, the claim
        // carries adoptGuestId and the server merges all of it into the account.
        // DreamPoints stay refused for guests (they are not sent at all).
        //
        // The guest id is the open visit's id (AdventureLedger.CurrentGuestId)
        // or, on an account-tethered headset, its guest session id. Core owns
        // both, so the host supplies them through GuestIdProvider; the SDK on
        // its own (no provider) keeps the old unbound behaviour exactly.
        //
        // A BOUND identity never sends guestId: every bound request is built
        // exactly as before.
        static readonly System.Text.RegularExpressions.Regex GuestIdRe =
            new System.Text.RegularExpressions.Regex("^guest_[a-z0-9]{8,32}$");

        /// <summary>Set by the host (core's ProfilePairingHandler): returns the
        /// current guest id, or null when there is none.</summary>
        [XLua.BlackList]
        public static Func<string> GuestIdProvider { get; set; }

        /// <summary>The guest id unbound writes are sent under, or null when a
        /// write would be dropped (bound, SDK preview, no provider, no visit, or
        /// an id the server would refuse).</summary>
        [XLua.BlackList]
        public static string GuestWriteId
        {
            get
            {
                if (IsBound || Source != ProfileSource.Headset) return null;
                return SanitizeGuestId(GuestIdProvider);
            }
        }

        /// <summary>A valid guest id from <paramref name="provider"/>, or null.</summary>
        [XLua.BlackList]
        public static string SanitizeGuestId(Func<string> provider)
        {
            if (provider == null) return null;
            string id;
            try { id = provider(); }
            catch (Exception e) { Debug.LogWarning($"[ProfileAPI] GuestIdProvider threw: {e.Message}"); return null; }
            return SanitizeGuestId(id);
        }

        /// <summary>The id lower-cased if it is a guest id the server accepts
        /// (guest_ + 8..32 [a-z0-9]), else null.</summary>
        [XLua.BlackList]
        public static string SanitizeGuestId(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            id = id.Trim().ToLowerInvariant();
            return GuestIdRe.IsMatch(id) ? id : null;
        }

        /// <summary>Called by the pairing handler the moment a claim goes out,
        /// so awards earned before it returns are held rather than lost.</summary>
        public static void BeginPendingBind() { BindPending = true; }

        /// <summary>Called when a claim fails — the identity is not coming, so
        /// nothing held for it may sit waiting for a later, possibly different,
        /// player. With a guest id the held awards are not dropped: they are
        /// sent as the guest (the person who earned them is still wearing it).</summary>
        public static void CancelPendingBind(string reason = null)
        {
            if (!IsBound && _heldAwards.Count > 0 && GuestWriteId != null)
            {
                Debug.Log($"[ProfileAPI] {reason ?? "pending bind cancelled"} — sending {_heldAwards.Count} held badge award(s) as the guest.");
                FlushHeldAwards(); // clears BindPending first, so each replays down the guest path
                return;
            }
            DropHeldAwards(reason ?? "pending bind cancelled");
        }

        // Replay awards banked while the claim was in flight. Called at the end
        // of BindIdentity, where BoundUserId is already set, so each replayed
        // AwardBadge takes the ordinary path (including the ContentGate latch —
        // consent is a separate question from identity and still applies).
        static void FlushHeldAwards()
        {
            BindPending = false;
            if (_heldAwards.Count == 0) return;
            var held = _heldAwards.ToArray();
            _heldAwards.Clear();
            float now = Time.realtimeSinceStartup;
            for (int i = 0; i < held.Length; i++)
            {
                var h = held[i];
                if (now - h.atSec > HeldAwardTtlSec)
                {
                    Debug.LogWarning($"[ProfileAPI] Dropped held badge award '{h.badgeId}' — identity took longer than {HeldAwardTtlSec:0}s to bind.");
                    try { h.done?.Invoke(false, null); } catch (Exception e) { Debug.LogWarning(e); }
                    continue;
                }
                AwardBadgeWithContent(h.badgeId, h.contentId, h.done, h.calledAtMs);
            }
        }

        static void DropHeldAwards(string why)
        {
            BindPending = false;
            if (_heldAwards.Count == 0) return;
            Debug.LogWarning($"[ProfileAPI] Dropping {_heldAwards.Count} held badge award(s) — {why}.");
            var held = _heldAwards.ToArray();
            _heldAwards.Clear();
            for (int i = 0; i < held.Length; i++)
            {
                try { held[i].done?.Invoke(false, null); } catch (Exception e) { Debug.LogWarning(e); }
            }
        }

        // ── Cache ────────────────────────────────────────────────────────
        static readonly List<ProfileItem>        _items        = new List<ProfileItem>();
        static readonly List<ProfileAchievement> _achievements = new List<ProfileAchievement>();
        static readonly List<ProfileBadge>       _badges       = new List<ProfileBadge>();

        // ── Events ───────────────────────────────────────────────────────
        public static event Action OnIdentityBound;
        public static event Action OnIdentityCleared;
        public static event Action OnProfileLoaded;
        /// <summary>Fired after an already-loaded profile's physical height
        /// changes. The arguments are canonical whole inches and the scale
        /// relative to the 68-inch authoring reference. Initial hydration is
        /// deliberately silent; use OnReady plus the Height* properties for
        /// the active profile's baseline.</summary>
        public static event Action<float /* heightInches */, float /* heightFactor */> OnHeightChanged;
        public static event Action OnInventoryChanged;
        public static event Action<ProfileAchievement> OnAchievementUpdated;
        public static event Action<ProfileBadge> OnBadgeAwarded;

        // ── Badge MOMENT (Oct 13 2026, Session Reels) ─────────────────────
        // The instant the GAME awarded a badge — stamped at the top of the
        // award call, before the bind / held / ContentGate / network path that
        // can delay OnBadgeAwarded by seconds. Fired ONCE per award call (also
        // when unbound, held, or later dropped); a held award replayed on bind
        // keeps its original time and does not fire again. Not for Lua
        // ([BlackList] keeps it out of the XLua wrapper — no codegen needed).
        /// <summary>(badgeId, contentId, calledAtMs) — device epoch ms of the award call.</summary>
        [XLua.BlackList]
        public static event Action<string, string, long> OnBadgeMoment;
        static readonly Dictionary<string, long> _badgeCalledAtMs = new Dictionary<string, long>();

        /// <summary>Device epoch ms of the most recent award call for this badge
        /// (the call being reported while OnBadgeAwarded runs), or 0.</summary>
        [XLua.BlackList]
        public static long BadgeCalledAtMs(string badgeId)
        {
            if (string.IsNullOrEmpty(badgeId)) return 0;
            return _badgeCalledAtMs.TryGetValue(badgeId, out long ms) ? ms : 0;
        }

        static void NoteBadgeCall(string badgeId, long calledAtMs)
        {
            if (_badgeCalledAtMs.Count > 256) _badgeCalledAtMs.Clear();
            _badgeCalledAtMs[badgeId] = calledAtMs;
        }

        /// <summary>Fired when AwardItem succeeds, with the freshly-fetched
        /// inventory row (name/iconUri/contentId hydrated). The Adventure
        /// Ledger logs it as an item_collected timeline event.</summary>
        public static event Action<ProfileItem> OnItemAwarded;
        // Fired whenever DreamPoints changes — including hydrate from snapshot,
        // earn from /add, and debit from /spend. Subscribe to drive HUD updates.
        public static event Action<int /* newBalance */, int /* delta */> OnDreamPointsChanged;

        // ── Pending callbacks (Lua scripts can call .onReady before data lands) ─
        static readonly List<Action> _onReadyQueue = new List<Action>();

        // Persistent Lua callbacks cannot hang directly off the public C#
        // event: attraction scripts need a deterministic unsubscribe handle,
        // and callbacks from one guest must never survive into the next guest.
        static readonly Dictionary<int, Action<float, float>> _luaHeightChangedSubscriptions
            = new Dictionary<int, Action<float, float>>();
        static int _nextLuaHeightSubscriptionId = 1;

        // Same contract for inventory: Lua gets a deterministic unsubscribe
        // handle, and a new guest never inherits the last guest's callbacks.
        // Fired after every snapshot apply (initial load, refresh, and the
        // refetch that follows each inventory write).
        static readonly Dictionary<int, Action> _luaInventorySubscriptions = new Dictionary<int, Action>();
        static int _nextLuaInventorySubscriptionId = 1;

        /// <summary>Bind an identity (called by the core QR pairing handler).
        /// Sets Source = Headset — all reads/writes go through /app/profile/*
        /// which the backend authorizes by looking up the headset's binding.</summary>
        public static void BindIdentity(string userId, string dreamId, string contentFilter = null, JSONObject initialSnapshot = null)
        {
            Source           = ProfileSource.Headset;
            string previousIdentity = IdentitySegment();
            BoundUserId      = string.IsNullOrEmpty(userId) ? null : userId;
            BoundDreamId = string.IsNullOrEmpty(dreamId) ? null : dreamId;
            // A DIFFERENT person (Oct 12 2026: a player signing in over a headset's
            // home guest, or back): the last one's name/avatar must not survive —
            // ApplyProfileSnapshot's anonymous guard keeps a held DisplayName, so an
            // anonymous visitor would otherwise show as the guest ("Zoocade 1").
            if (!string.Equals(previousIdentity, IdentitySegment(), StringComparison.Ordinal))
            {
                DisplayName = null;
                AvatarUrl   = null;
                IsAnonymous = false;
            }
            ContentFilter    = string.IsNullOrEmpty(contentFilter) ? null : contentFilter;
            IsLoaded         = false;
            HeightInches     = ReferenceHeightInches;
            try { OnIdentityBound?.Invoke(); } catch (Exception e) { Debug.LogWarning($"[ProfileAPI] OnIdentityBound subscriber threw: {e}"); }

            // If the caller already holds the authoritative snapshot (the
            // /app/profile/claim response carries identity + profile + inventory),
            // apply it DIRECTLY. It's bound to the userId we just paired and always
            // includes displayName. A second GET /app/profile re-resolves the binding
            // server-side from the headset doc, which can land on the anonymous
            // DreamID (avatar but NO displayName) — exactly the "name shows Player"
            // bug. Fetch only when we have no snapshot (restore/boot paths).
            if (initialSnapshot != null)
            {
                ApplyProfileSnapshot(HydrateProfileSnapshot(initialSnapshot), null);
            }
            else
            {
                FetchProfile(ContentFilter, null);
            }

            // Anything earned while this claim was in flight belongs to the
            // identity that just landed. Replayed AFTER the snapshot call so
            // the award's own refetch is the later of the two writes.
            FlushHeldAwards();
        }

        /// <summary>
        /// SDK preview / editor testing: exercise the REAL pairing-token
        /// pipeline against your own logged-in account — no QR scan needed.
        ///
        /// Flow:
        ///   1. POST /api/pairing/create  (session bearer) → server mints
        ///      a token tied to your uid, returns { token, url, expiresAt }.
        ///   2. POST /api/pairing/preview-claim  (session bearer) → server
        ///      validates the token belongs to this session, marks it
        ///      redeemed, returns the same profile snapshot /app/profile/claim
        ///      would have returned to a headset.
        ///   3. ApplyProfileSnapshot — exactly the production code path runs.
        ///
        /// This means dp.profile.* in editor exercises the same write
        /// paths (/api/user/inventory/add etc. for the LoggedInUser source),
        /// the same hydration, the same token validation, the same caching.
        /// Bugs in the pairing token machinery surface in editor.
        ///
        /// Requires AuthAPI.isLoggedIn — sign in via the SDK login first.
        /// </summary>
        public static void BindToLoggedInUser(string contentFilter = null)
        {
            if (!AuthAPI.isLoggedIn || string.IsNullOrEmpty(AuthAPI.userId))
            {
                Debug.LogWarning("[ProfileAPI] BindToLoggedInUser — no session. Log in via the SDK first.");
                return;
            }
            Source           = ProfileSource.LoggedInUser;
            BoundUserId      = AuthAPI.userId;
            BoundDreamId = null;
            ContentFilter    = string.IsNullOrEmpty(contentFilter) ? null : contentFilter;
            IsLoaded         = false;
            HeightInches     = ReferenceHeightInches;
            Debug.Log($"[ProfileAPI] BindToLoggedInUser — minting pairing token for uid {BoundUserId}…");
            try { OnIdentityBound?.Invoke(); } catch (Exception e) { Debug.LogWarning($"[ProfileAPI] OnIdentityBound subscriber threw: {e}"); }

            // Step 1: mint a pairing token via the same endpoint the web
            // profile page uses for QR codes. CSRF guard requires the
            // X-Requested-With header — but DreamParkAPI doesn't expose
            // arbitrary headers, so we pass a stub body and rely on the
            // session bearer auth path.
            // Defective.JSON's `new JSONObject()` defaults to type=Null —
            // serializes to "null". The server's body parser doesn't like
            // that, so we explicitly create an empty object {}.
            DreamParkAPI.POST("/api/pairing/create", AuthAPI.GetUserAuth(), new JSONObject(JSONObject.Type.Object), (ok, resp) =>
            {
                if (!ok || resp?.json == null)
                {
                    Debug.LogWarning("[ProfileAPI] BindToLoggedInUser: /api/pairing/create failed: " + resp?.error);
                    FlushReadyQueue(failed: true);
                    return;
                }
                var token = resp.json.GetField("token")?.stringValue;
                if (string.IsNullOrEmpty(token))
                {
                    Debug.LogWarning("[ProfileAPI] BindToLoggedInUser: no token in /api/pairing/create response");
                    FlushReadyQueue(failed: true);
                    return;
                }

                // Step 2: redeem it via the preview-claim endpoint.
                var claimBody = new JSONObject();
                claimBody.AddField("token", token);
                if (!string.IsNullOrEmpty(ContentFilter)) claimBody.AddField("contentId", ContentFilter);

                DreamParkAPI.POST("/api/pairing/preview-claim", AuthAPI.GetUserAuth(), claimBody, (claimOk, claimResp) =>
                {
                    if (!claimOk || claimResp?.json == null)
                    {
                        Debug.LogWarning("[ProfileAPI] BindToLoggedInUser: /api/pairing/preview-claim failed: " + claimResp?.error);
                        FlushReadyQueue(failed: true);
                        return;
                    }
                    // Extract the server-issued preview key — subsequent
                    // writes use it as `Bearer <key>` on /app/profile/*.
                    // Without it, AwardItem/AwardBadge/AwardAchievement
                    // would 401 since SDK preview can no longer fall back
                    // to plain session bearer (that surface was retired
                    // because it let any authed user grant arbitrary items).
                    var previewKeyObj = claimResp.json.GetField("previewKey");
                    _previewKey = previewKeyObj?.GetField("key")?.stringValue;
                    if (string.IsNullOrEmpty(_previewKey))
                    {
                        Debug.LogWarning("[ProfileAPI] BindToLoggedInUser: preview-claim returned no previewKey — writes will fail.");
                    }
                    Debug.Log("[ProfileAPI] BindToLoggedInUser: preview-claim succeeded, applying profile snapshot.");
                    ApplyProfileSnapshot(HydrateProfileSnapshot(claimResp.json), null);
                });
            });
        }

        public static void ClearIdentity() => ClearIdentity(flushPending: true);

        /// <param name="flushPending">false when the SERVER binding already moved on
        /// (a headset returning to its home guest): a flush now would write the last
        /// player's pending game storage into the next identity. Callers flush first.</param>
        public static void ClearIdentity(bool flushPending)
        {
            // Best-effort final push of pending game-storage writes while
            // the binding can still authorize them — the OnIdentityCleared
            // handler in GameStorageAPI wipes cache + queue right after.
            if (flushPending)
            {
                try { GameStorageAPI.FlushAll(); } catch (Exception e) { Debug.LogWarning($"[ProfileAPI] pre-clear storage flush threw: {e}"); }
            }
            // The player left. A held award must never survive into whoever
            // picks the headset up next.
            DropHeldAwards("identity cleared");
            BoundUserId = null;
            BoundDreamId = null;
            _previewKey = null;
            DreamPoints = 0;
            DisplayName = null;
            AvatarUrl   = null;
            IsAnonymous = false;
            HeightInches = ReferenceHeightInches;
            IsLoaded = false;
            _items.Clear();
            _achievements.Clear();
            _badges.Clear();
            _luaHeightChangedSubscriptions.Clear();
            _luaInventorySubscriptions.Clear();
            try { OnIdentityCleared?.Invoke(); } catch (Exception e) { Debug.LogWarning($"[ProfileAPI] OnIdentityCleared subscriber threw: {e}"); }
        }

        // internal: GameStorageAPI reuses the same credential selection —
        // /app/profile/storage/* sits behind the identical auth surface.
        internal static string AuthHeader()
        {
#if DREAMPARKCORE
            // Production headset path — the embedded Unity API key. Server
            // resolves identity from the headset binding on the active
            // consumer_session, so writes go to the paired user/DreamID.
            return AuthAPI.GetAPIKey();
#else
            // SDK preview path — the server-issued preview key minted by
            // /api/pairing/preview-claim. Server resolves identity to the
            // key's bound uid (the developer's own). Writes are server-side
            // scoped to that uid only; a leaked preview key cannot mutate
            // any other user's profile.
            return string.IsNullOrEmpty(_previewKey) ? "" : $"Bearer {_previewKey}";
#endif
        }

        /// <summary>"user:abc" or "did:XYZ" or null — used only for display
        /// and diagnostics; the server never trusts this string.</summary>
        public static string IdentitySegment()
        {
            if (!string.IsNullOrEmpty(BoundUserId)) return "user:" + BoundUserId;
            if (!string.IsNullOrEmpty(BoundDreamId)) return "did:" + BoundDreamId;
            return null;
        }

        // Headset id flows in the X-Headset-Id header; the backend looks up
        // which identity this headset is bound to and never trusts a client-
        // supplied uid. SDK preview builds (no real headset) fall back to a
        // stable per-install id so things still work in the editor.
        static string HeadsetIdHeader()
        {
            return SystemInfo.deviceUniqueIdentifier;
        }

        // ── Fetch ────────────────────────────────────────────────────────
        public static void FetchProfile(string contentFilter, Action<bool, ProfileSnapshot> done)
        {
            if (!IsBound)
            {
                // No identity bound yet (no QR scan, no manual BindIdentity).
                // Do NOT flush the onReady queue — callbacks are waiting for
                // "we have data," not "we tried and got nothing." Bindings
                // can happen 1 second or 30 minutes after a script registers
                // onReady, and we want those callbacks to still fire when
                // BindIdentity eventually triggers a real fetch.
                Debug.Log("[ProfileAPI] FetchProfile skipped — no identity bound yet. Callbacks remain queued.");
                done?.Invoke(false, null);
                return;
            }

            if (!string.IsNullOrEmpty(contentFilter)) ContentFilter = contentFilter;

            if (Source == ProfileSource.LoggedInUser)
            {
                FetchProfileForLoggedInUser(done);
                return;
            }

            // Headset path — /app/profile/* with API key, backend looks up binding
            var qs = "?headsetId=" + UnityWebRequestEscape(HeadsetIdHeader());
            if (!string.IsNullOrEmpty(ContentFilter)) qs += "&contentId=" + UnityWebRequestEscape(ContentFilter);
            var url = "/app/profile" + qs;

            DreamParkAPI.GET(url, AuthHeader(), (success, response) =>
            {
                if (!success || response?.json == null)
                {
                    Debug.LogWarning($"[ProfileAPI] FetchProfile failed: {response?.error}");
                    FlushReadyQueue(failed: true);
                    done?.Invoke(false, null);
                    return;
                }
                ApplyProfileSnapshot(HydrateProfileSnapshot(response.json), done);
            });
        }

        // Shared apply-and-fire — used by both fetch paths.
        static void ApplyProfileSnapshot(ProfileSnapshot snapshot, Action<bool, ProfileSnapshot> done)
        {
            _items.Clear();        _items.AddRange(snapshot.items);
            _achievements.Clear(); _achievements.AddRange(snapshot.achievements);
            _badges.Clear();       _badges.AddRange(snapshot.badges);
            int prevPoints = DreamPoints;
            DreamPoints = snapshot.dreamPoints;

            // Identity guard: a later fetch can resolve to the anonymous DreamID
            // (which has an avatar but NO displayName/handle/email). Applying it
            // unconditionally would wipe the real signed-in identity we already
            // loaded — the "DreamBand reverts to Player" bug. So skip the identity
            // overwrite when the incoming snapshot is anonymous AND we already hold
            // a real display identity. (Inventory / points / badges still update.)
            bool haveRealIdentity = !string.IsNullOrEmpty(DisplayName);
            if (!(snapshot.isAnonymous && haveRealIdentity))
            {
                DisplayName = snapshot.displayName;
                AvatarUrl   = snapshot.avatarUrl;
                IsAnonymous = snapshot.isAnonymous;
                // IsLoaded is false for the first hydration, making this a
                // silent baseline. Refreshes of the active profile take the
                // exact same validation/change path and notify subscribers.
                ApplyHeightUpdate(snapshot.heightInches);
            }
            else
            {
                Debug.Log($"[ProfileAPI] Ignored anonymous snapshot — kept identity displayName='{DisplayName}'.");
            }
            IsLoaded = true;
            try { OnProfileLoaded?.Invoke(); } catch (Exception e) { Debug.LogWarning($"[ProfileAPI] OnProfileLoaded subscriber threw: {e}"); }
            if (prevPoints != DreamPoints) {
                try { OnDreamPointsChanged?.Invoke(DreamPoints, DreamPoints - prevPoints); } catch (Exception e) { Debug.LogWarning(e); }
            }
            FlushReadyQueue(failed: false);
            FireLuaInventoryChanged();
            done?.Invoke(true, snapshot);
        }

        static void FireLuaInventoryChanged()
        {
            if (_luaInventorySubscriptions.Count == 0) return;
            var callbacks = new List<Action>(_luaInventorySubscriptions.Values);
            for (int i = 0; i < callbacks.Count; i++)
            {
                try { callbacks[i]?.Invoke(); }
                catch (Exception e) { Debug.LogWarning($"[ProfileAPI] Lua onInventoryChanged callback threw: {e}"); }
            }
        }

        static int SubscribeLuaInventoryChanged(Action callback)
        {
            if (callback == null) return 0;
            int id = _nextLuaInventorySubscriptionId++;
            if (_nextLuaInventorySubscriptionId <= 0) _nextLuaInventorySubscriptionId = 1;
            _luaInventorySubscriptions[id] = callback;
            return id;
        }

        static void UnsubscribeLuaInventoryChanged(int subscriptionId)
        {
            if (subscriptionId > 0) _luaInventorySubscriptions.Remove(subscriptionId);
        }

        // LoggedInUser refresh path — hits the session-authed snapshot
        // endpoint that returns the exact same shape /app/profile would.
        // Used after AwardItem / AwardBadge / AwardAchievement to refresh
        // the cache. The initial bind goes through the pairing-token flow
        // in BindToLoggedInUser (proves the token machinery works end-to-end);
        // subsequent reads use this lightweight single GET.
        static void FetchProfileForLoggedInUser(Action<bool, ProfileSnapshot> done)
        {
            var qs = string.IsNullOrEmpty(ContentFilter) ? "" : "?contentId=" + UnityWebRequestEscape(ContentFilter);
            DreamParkAPI.GET("/api/pairing/profile-self" + qs, AuthAPI.GetUserAuth(), (ok, resp) =>
            {
                if (!ok || resp?.json == null)
                {
                    Debug.LogWarning("[ProfileAPI] LoggedInUser fetch failed: " + resp?.error);
                    FlushReadyQueue(failed: true);
                    done?.Invoke(false, null);
                    return;
                }
                ApplyProfileSnapshot(HydrateProfileSnapshot(resp.json), done);
            });
        }

        static void FlushReadyQueue(bool failed = false)
        {
            if (_onReadyQueue.Count == 0) return;
            var snapshot = _onReadyQueue.ToArray();
            _onReadyQueue.Clear();
            foreach (var cb in snapshot)
            {
                try { cb?.Invoke(); } catch (Exception e) { Debug.LogWarning($"[ProfileAPI] onReady callback threw: {e}"); }
            }
            if (failed) Debug.LogWarning("[ProfileAPI] Flushed onReady queue without data (fetch failed) — consumers will see empty cache.");
        }

        /// <summary>Register a callback that fires once profile data is loaded.
        /// If already loaded, fires immediately on this frame.</summary>
        public static void OnReady(Action callback)
        {
            if (callback == null) return;
            if (IsLoaded) { try { callback(); } catch (Exception e) { Debug.LogWarning($"[ProfileAPI] OnReady subscriber threw: {e}"); } return; }
            _onReadyQueue.Add(callback);
        }

        /// <summary>
        /// Applies a host-delivered height update for the currently bound
        /// profile. Invalid, fractional, NaN, infinite, or out-of-range input
        /// resolves to the safe 68-inch fallback. Returns true only when the
        /// cached value actually changed. Changes received before the initial
        /// profile load establish the baseline silently; once loaded, both C#
        /// and Lua height subscribers are notified.
        /// </summary>
        public static bool ApplyHeightUpdate(float heightInches)
        {
            float canonical = NormalizeHeightInches(heightInches);
            if (HeightInches == canonical) return false;

            HeightInches = canonical;
            if (!IsLoaded) return true;

            float factor = HeightFactor;
            var csharpSubscribers = OnHeightChanged;
            if (csharpSubscribers != null)
            {
                foreach (Action<float, float> callback in csharpSubscribers.GetInvocationList())
                {
                    try { callback(HeightInches, factor); }
                    catch (Exception e) { Debug.LogWarning($"[ProfileAPI] OnHeightChanged subscriber threw: {e}"); }
                }
            }

            if (_luaHeightChangedSubscriptions.Count > 0)
            {
                var callbacks = new List<Action<float, float>>(_luaHeightChangedSubscriptions.Values);
                for (int i = 0; i < callbacks.Count; i++)
                {
                    try { callbacks[i]?.Invoke(HeightInches, factor); }
                    catch (Exception e) { Debug.LogWarning($"[ProfileAPI] Lua onHeightChanged callback threw: {e}"); }
                }
            }
            return true;
        }

        static int SubscribeLuaHeightChanged(Action<float, float> callback)
        {
            if (callback == null) return 0;

            int id = _nextLuaHeightSubscriptionId++;
            if (_nextLuaHeightSubscriptionId <= 0) _nextLuaHeightSubscriptionId = 1;
            _luaHeightChangedSubscriptions[id] = callback;
            return id;
        }

        static void UnsubscribeLuaHeightChanged(int subscriptionId)
        {
            if (subscriptionId > 0) _luaHeightChangedSubscriptions.Remove(subscriptionId);
        }

        // ── Reads (synchronous; return null if no data yet) ──────────────
        public static IReadOnlyList<ProfileItem>        Items        => _items;
        public static IReadOnlyList<ProfileAchievement> Achievements => _achievements;
        public static IReadOnlyList<ProfileBadge>       Badges       => _badges;

        public static ProfileItem GetItem(string itemId)
        {
            if (string.IsNullOrEmpty(itemId)) return null;
            for (int i = 0; i < _items.Count; i++)
            {
                var it = _items[i];
                if (it.itemId == itemId || it.baseItemId == itemId || it.instanceId == itemId) return it;
            }
            return null;
        }

        public static ProfileItem GetItemByType(string type)
        {
            if (string.IsNullOrEmpty(type)) return null;
            for (int i = 0; i < _items.Count; i++) if (_items[i].type == type) return _items[i];
            return null;
        }

        public static ProfileItem GetItemByName(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            for (int i = 0; i < _items.Count; i++) if (_items[i].name == name) return _items[i];
            return null;
        }

        public static List<ProfileItem> GetItemsByType(string type)
        {
            var list = new List<ProfileItem>();
            if (string.IsNullOrEmpty(type)) return list;
            for (int i = 0; i < _items.Count; i++) if (_items[i].type == type) list.Add(_items[i]);
            return list;
        }

        /// <summary>Every cached inventory row that matches all the given
        /// filters (null/empty = no filter). <paramref name="baseItemId"/>
        /// matches the catalog id, so all unique instances of one item come
        /// back, each with its own instanceId and metadata.</summary>
        public static List<ProfileItem> GetItems(string baseItemId = null, string type = null, string contentId = null)
        {
            var list = new List<ProfileItem>();
            for (int i = 0; i < _items.Count; i++)
            {
                var it = _items[i];
                if (!string.IsNullOrEmpty(baseItemId) && it.baseItemId != baseItemId && it.itemId != baseItemId) continue;
                if (!string.IsNullOrEmpty(type) && it.type != type) continue;
                if (!string.IsNullOrEmpty(contentId) && it.contentId != contentId && it.gameId != contentId) continue;
                list.Add(it);
            }
            return list;
        }

        public static bool HasItem(string itemId)       => GetItem(itemId) != null;
        public static bool HasItemByType(string type)   => GetItemByType(type) != null;
        public static bool HasBadge(string badgeId)     => GetBadge(badgeId) != null;
        public static bool HasAchievement(string id)    => GetAchievement(id) != null;

        public static ProfileAchievement GetAchievement(string achievementId)
        {
            if (string.IsNullOrEmpty(achievementId)) return null;
            for (int i = 0; i < _achievements.Count; i++) if (_achievements[i].achievementId == achievementId) return _achievements[i];
            return null;
        }

        /// <summary>The player's earned badge with this id, or null.
        ///
        /// Badge ids are content-scoped, but the cache is one flat list and
        /// there are live paths that fill it with more than one game's rows
        /// (a consumer headset restoring a cached login binds with no content
        /// filter at all), so a bare-id match can hand game A's "champion" to
        /// game B. Prefer the row belonging to the game that is asking.
        ///
        /// The bare-id fallback is deliberate and must stay: rows awarded
        /// before ids were scoped carry no contentId, and `GameArea.gameId` is
        /// stamped from the local folder id — not guaranteed to equal the
        /// contentId the package was uploaded under. Without the fallback,
        /// either of those makes a badge the player genuinely owns read as
        /// unowned, which is the one wrong answer with a user-visible cost
        /// (a re-award is idempotent; a "you don't have this" is not).</summary>
        public static ProfileBadge GetBadge(string badgeId)
        {
            if (string.IsNullOrEmpty(badgeId)) return null;

            var asking = CallingContentId();
            if (!string.IsNullOrEmpty(asking))
            {
                for (int i = 0; i < _badges.Count; i++)
                {
                    var b = _badges[i];
                    if (b.badgeId == badgeId && b.contentId == asking) return b;
                }
            }

            for (int i = 0; i < _badges.Count; i++) if (_badges[i].badgeId == badgeId) return _badges[i];
            return null;
        }

        // ── Writes (fire-and-forget; optimistic local update) ────────────
        // All writes go through /app/profile/* — the single locked-down
        // write surface for inventory/achievements/badges. The previous
        // /api/user/* alternative was removed because it accepted any
        // session bearer, letting any authed user grant themselves
        // arbitrary items. AuthHeader() picks the right credential for
        // the build (production = Unity API key; SDK preview = server-
        // issued preview key bound to the developer's own uid).
        static (string url, string auth) PickWrite(string suffix)
        {
            return ("/app/profile/" + suffix, AuthHeader());
        }

        static bool _warnedUnapprovedPoints;

        /// <summary>The content id of the attraction the guest is standing in.
        /// DreamPoints are only real on approved content, so the server needs
        /// to know who is asking; the GameArea the player is inside is the
        /// same signal ContentGate latches on. Falls back to the profile's
        /// content filter, then empty (which the server treats as
        /// unapproved — it fails closed).</summary>
        static string CallingContentId()
        {
            var here = GameArea.currentGameArea;
            if (here != null && !string.IsNullOrEmpty(here.gameId)) return here.gameId;
            return ContentFilter ?? "";
        }

        /// <summary>The server returns success with credited/debited=false when
        /// the calling content isn't an approved release. Gameplay carries on
        /// unchanged — but say so once, or a developer ships an economy that
        /// only becomes real at approval and never got tested.</summary>
        static void WarnIfPointsNotApplied(JSONObject json, string verb)
        {
            if (json == null) return;
            var flag = json.GetField(verb == "spend" ? "debited" : "credited");
            if (flag == null || flag.boolValue) return;
            if (_warnedUnapprovedPoints) return;
            _warnedUnapprovedPoints = true;
            var reason = json.GetField("reason")?.stringValue ?? "unapproved";
            Debug.LogWarning(
                $"[ProfileAPI] DreamPoints {verb} succeeded but changed nothing (reason: {reason}). " +
                "Points are only real on APPROVED attractions — experimental builds no-op so your " +
                "game logic runs the same either way. This warns once per session.");
        }

        /// <summary>POST a profile WRITE through the consent latch.
        ///
        /// A guest wandering a park has not opted into every game installed
        /// there. Writes made before the player walks into one of this
        /// content's attractions are held by <see cref="ContentGate"/> and
        /// sent the moment they do — so a script that awards on Start, or a
        /// park-wide system firing for someone who never reached the room,
        /// can't reach the profile.
        ///
        /// Reads are NOT gated (harmless), and neither is session playtime
        /// reporting (it's what tells the platform the guest is playing).
        /// </summary>
        // internal, not private: ScoreAPI's score submission needs the exact
        // same consent latch as every other profile write, and reimplementing
        // "wrap the POST in ContentGate.Run" a second time would be a place
        // that gate policy could silently drift.
        internal static void GatedPost(string url, string auth, JSONObject body, Action<bool, APIResponse> done)
        {
            ContentGate.Run(null, () => DreamParkAPI.POST(url, auth, body, done));
        }

        /// <summary>True when a write may go out: bound, or unbound with a guest
        /// id (<paramref name="guestId"/> is then set and the body must carry it).</summary>
        static bool CanWrite(out string guestId)
        {
            guestId = null;
            if (IsBound) return true;
            guestId = GuestWriteId;
            return guestId != null;
        }

        /// <summary>After a GUEST write: reload the caches from the guest's own
        /// snapshot (GET /app/profile?guestId=) so GetItem/GetBadge and the
        /// award events see what was just written. Touches only the item /
        /// achievement / badge caches: IsLoaded, the onReady queue and the
        /// display identity stay as they are for a real sign-in. If an identity
        /// bound meanwhile, its own fetch owns the caches and this applies nothing.</summary>
        static void RefreshGuestCaches(string guestId, Action then)
        {
            var qs = "?headsetId=" + UnityWebRequestEscape(HeadsetIdHeader());
            if (!string.IsNullOrEmpty(ContentFilter)) qs += "&contentId=" + UnityWebRequestEscape(ContentFilter);
            qs += "&guestId=" + UnityWebRequestEscape(guestId);
            DreamParkAPI.GET("/app/profile" + qs, AuthHeader(), (ok, resp) =>
            {
                if (!IsBound && ok && resp?.json != null && GuestWriteId == guestId)
                {
                    try
                    {
                        var snap = HydrateProfileSnapshot(resp.json);
                        _items.Clear();        _items.AddRange(snap.items);
                        _achievements.Clear(); _achievements.AddRange(snap.achievements);
                        _badges.Clear();       _badges.AddRange(snap.badges);
                    }
                    catch (Exception e) { Debug.LogWarning($"[ProfileAPI] guest snapshot apply threw: {e.Message}"); }
                    FireLuaInventoryChanged();
                }
                else if (!ok)
                {
                    Debug.LogWarning($"[ProfileAPI] guest snapshot fetch failed: {resp?.error}");
                }
                try { then?.Invoke(); } catch (Exception e) { Debug.LogWarning(e); }
            });
        }

        /// <summary>The bound refetch, or the guest one after a guest write.</summary>
        static void RefreshAfterWrite(string guestId, Action then)
        {
            if (guestId != null) RefreshGuestCaches(guestId, then);
            else FetchProfile(ContentFilter, (_, __) => then?.Invoke());
        }

        /// <summary>Grant one UNIQUE instance of an item — a distinct
        /// inventory entry with its own instance id, created fresh on every
        /// call. For loot where each copy is meant to differ (procedurally
        /// rolled gear, numbered collectibles).
        ///
        /// This is NOT how you make an item non-stacking: every call creates
        /// another item, and each one is worth profile XP. For a one-of-a-kind
        /// item (a wand, a key, a costume), untick "Stacks" on the catalog
        /// entry and use <see cref="AwardItem"/>.</summary>

        public static void AwardUniqueItem(string itemId, JSONObject metadata = null, Action<bool, ProfileItem> done = null)
        {
            var meta = metadata;
            if (meta == null)
            {
                // A stub metadata object is what tells the backend to mint a
                // distinct instance — any truthy metadata takes that path.
                meta = new JSONObject(JSONObject.Type.Object);
                meta.AddField("unique", true);
            }
            AwardItem(itemId, 1, meta, done);
        }

        public static void AwardItem(string itemId, int amount = 1, JSONObject metadata = null, Action<bool, ProfileItem> done = null)
        {
            if (!CanWrite(out string guestId)) { Debug.LogWarning("[ProfileAPI] AwardItem with no identity bound."); done?.Invoke(false, null); return; }

            var body = new JSONObject();
            if (Source == ProfileSource.Headset) body.AddField("headsetId", HeadsetIdHeader());
            body.AddField("itemId", itemId);
            body.AddField("amount", amount);
            if (metadata != null)
            {
                // Stamp the awarding content onto per-instance metadata so the
                // row can be scoped to its game even when the catalog doc has
                // no gameId (or doesn't exist yet). A caller's own value wins.
                if (metadata.type == JSONObject.Type.Object && !metadata.HasField("contentId"))
                {
                    var cid = CallingContentId();
                    if (!string.IsNullOrEmpty(cid)) metadata.AddField("contentId", cid);
                }
                body.AddField("metadata", metadata);
            }
            if (guestId != null) body.AddField("guestId", guestId);

            var (url, auth) = PickWrite("inventory/add");
            GatedPost(url, auth, body, (ok, resp) =>
            {
                if (!ok) { done?.Invoke(false, null); return; }
                // The server answers with the row it wrote; for a unique grant
                // its key is the freshly minted instance id. Resolve THAT row
                // after the refetch, not the first row of the same base item.
                var mintedKey = resp?.json?.GetField("item")?.GetField("itemId")?.stringValue;
                RefreshAfterWrite(guestId, () =>
                {
                    try { OnInventoryChanged?.Invoke(); } catch (Exception e) { Debug.LogWarning(e); }
                    var awarded = (!string.IsNullOrEmpty(mintedKey) ? GetItem(mintedKey) : null) ?? GetItem(itemId);
                    try { OnItemAwarded?.Invoke(awarded); } catch (Exception e) { Debug.LogWarning(e); }
                    done?.Invoke(true, awarded);
                });
            });
        }

        /// <summary>Add <paramref name="progress"/> toward an achievement.
        /// Progress ACCUMULATES across calls, so pass the delta, not the
        /// running total. The achievement completes on its own once the
        /// catalog's maxValue is reached (a catalog entry with no maxValue is
        /// binary — threshold 1 — so a single call finishes it).
        ///
        /// For a one-shot "the player did the thing" achievement, prefer
        /// <see cref="CompleteAchievement"/>: it states the intent instead of
        /// relying on the threshold, and is safe to call repeatedly.</summary>
        public static void AwardAchievement(string achievementId, float progress = 1, Action<bool, ProfileAchievement> done = null)
        {
            AwardAchievement(achievementId, progress, false, done);
        }

        /// <summary>Mark an achievement complete outright, whatever its
        /// progress threshold. Idempotent — completion is sticky server-side
        /// and the "earned on" timestamp is stamped once, so calling this from
        /// a trigger the guest can re-enter is safe.</summary>
        public static void CompleteAchievement(string achievementId, Action<bool, ProfileAchievement> done = null)
        {
            AwardAchievement(achievementId, 1, true, done);
        }

        public static void AwardAchievement(string achievementId, float progress, bool complete, Action<bool, ProfileAchievement> done = null)
        {
            if (!CanWrite(out string guestId)) { Debug.LogWarning("[ProfileAPI] AwardAchievement with no identity bound."); done?.Invoke(false, null); return; }

            var body = new JSONObject();
            if (Source == ProfileSource.Headset) body.AddField("headsetId", HeadsetIdHeader());
            body.AddField("achievementId", achievementId);
            body.AddField("progress", progress);
            // Only sent when the caller means "done" — the server treats a
            // bare progress write as an increment.
            if (complete) body.AddField("completed", true);
            if (guestId != null) body.AddField("guestId", guestId);

            var (url, auth) = PickWrite("achievements/add");
            GatedPost(url, auth, body, (ok, resp) =>
            {
                if (!ok) { done?.Invoke(false, null); return; }
                RefreshAfterWrite(guestId, () =>
                {
                    var ach = GetAchievement(achievementId);
                    try { OnAchievementUpdated?.Invoke(ach); } catch (Exception e) { Debug.LogWarning(e); }
                    done?.Invoke(true, ach);
                });
            });
        }

        /// <summary>Award a badge to the bound player.
        ///
        /// WHICH GAME the badge belongs to is resolved HERE, at the call, and
        /// carried through every later hop. It is deliberately not read again
        /// at send time, because there are two gaps between the two moments
        /// and the player can move across both:
        ///
        ///   • ContentGate holds the write until the player enters an
        ///     attraction. The hold is UNSCOPED when the calling content
        ///     can't be named, and the first attraction of ANY content
        ///     releases it — so in a park with two games installed, a
        ///     re-resolve at send time can name the game the player walked
        ///     into rather than the one that awarded the badge.
        ///   • An award made while a pairing claim is in flight is banked and
        ///     replayed on bind, which can be seconds later and a room away.
        ///
        /// Empty is a legitimate answer (nobody is standing in an attraction
        /// yet) and is sent as an omitted field, which the server must read as
        /// "unattributed" — never as a licence to guess.</summary>
        public static void AwardBadge(string badgeId, Action<bool, ProfileBadge> done = null)
        {
            AwardBadgeWithContent(badgeId, CallingContentId(), done);
        }

        // calledAtMs: 0 = a fresh call (stamped + OnBadgeMoment fired here);
        // a held award's replay passes its ORIGINAL call time and fires nothing.
        static void AwardBadgeWithContent(string badgeId, string contentId, Action<bool, ProfileBadge> done, long calledAtMs = 0)
        {
            if (string.IsNullOrEmpty(badgeId)) { Debug.LogWarning("[ProfileAPI] AwardBadge with no badgeId."); done?.Invoke(false, null); return; }
            if (calledAtMs <= 0)
            {
                calledAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                NoteBadgeCall(badgeId, calledAtMs);
                try { OnBadgeMoment?.Invoke(badgeId, contentId, calledAtMs); } catch (Exception e) { Debug.LogWarning($"[ProfileAPI] OnBadgeMoment subscriber threw: {e}"); }
            }
            string guestId = null;
            if (!IsBound)
            {
                // A claim in flight still holds (the badge then lands on the
                // account that binds); otherwise a guest sends it as the guest.
                if (BindPending && _heldAwards.Count < MaxHeldAwards)
                {
                    _heldAwards.Add(new HeldAward { badgeId = badgeId, contentId = contentId, done = done, atSec = Time.realtimeSinceStartup, calledAtMs = calledAtMs });
                    Debug.Log($"[ProfileAPI] AwardBadge '{badgeId}' held — identity claim in flight.");
                    return;
                }
                guestId = GuestWriteId;
                if (guestId == null)
                {
                    Debug.LogWarning("[ProfileAPI] AwardBadge with no identity bound.");
                    done?.Invoke(false, null);
                    return;
                }
            }

            var body = new JSONObject();
            if (Source == ProfileSource.Headset) body.AddField("headsetId", HeadsetIdHeader());
            body.AddField("badgeId", badgeId);
            // WHICH GAME's badge — resolved at the call (see above), so the
            // server can scope a short id like "champion" to its own content
            // instead of a platform-wide namespace.
            if (!string.IsNullOrEmpty(contentId)) body.AddField("contentId", contentId);
            // WHERE it was earned. Stamped by the client because the server
            // can only infer a park from the bound session's location, which
            // the embedded iOS runtime does not have. Additive — a server that
            // doesn't read it drops the field.
            var parkId = CallingParkId();
            if (!string.IsNullOrEmpty(parkId)) body.AddField("parkId", parkId);
            if (!string.IsNullOrEmpty(ShardContext)) body.AddField("shardId", ShardContext);
            if (guestId != null) body.AddField("guestId", guestId);

            var (url, auth) = PickWrite("badges/award");
            GatedPost(url, auth, body, (ok, resp) =>
            {
                if (!ok) { done?.Invoke(false, null); return; }
                RefreshAfterWrite(guestId, () =>
                {
                    // The refetch is CONTENT-FILTERED, so a badge belonging to
                    // other content is on the profile server-side but absent
                    // from this snapshot. Firing null there would silently cost
                    // the adventure timeline its badge_earned event (the ledger
                    // drops a null badge) — so fall back to the id we know is
                    // true. Subscribers get a thin badge rather than none.
                    var b = GetBadge(badgeId) ?? new ProfileBadge { badgeId = badgeId };
                    // THIS award's call time is what BadgeCalledAtMs answers while
                    // OnBadgeAwarded runs (a newer call for the same id may have
                    // overwritten it while this one was in flight).
                    NoteBadgeCall(badgeId, calledAtMs);
                    try { OnBadgeAwarded?.Invoke(b); } catch (Exception e) { Debug.LogWarning(e); }
                    done?.Invoke(true, b);
                });
            });
        }

        /// <summary>
        /// Remove <paramref name="amount"/> copies of <paramref name="itemId"/>
        /// from the user's inventory. Decrements the stack; when the stored
        /// amount hits zero the entry is deleted.
        ///
        /// For unique (metadata-tagged) items, pass the full instance id
        /// (e.g. "wand_stormheart_a3f24b81") and amount=1 to remove that
        /// specific row. Server is idempotent — removing more than is owned
        /// just clears the entry.
        /// </summary>
        public static void RemoveItem(string itemId, int amount = 1, Action<bool> done = null)
        {
            if (!CanWrite(out string guestId)) { Debug.LogWarning("[ProfileAPI] RemoveItem with no identity bound."); done?.Invoke(false); return; }
            if (string.IsNullOrEmpty(itemId)) { done?.Invoke(false); return; }
            if (amount <= 0) amount = 1;

            var body = new JSONObject(JSONObject.Type.Object);
            if (Source == ProfileSource.Headset) body.AddField("headsetId", HeadsetIdHeader());
            body.AddField("itemId", itemId);
            body.AddField("amount", amount);
            if (guestId != null) body.AddField("guestId", guestId);

            var (url, auth) = PickWrite("inventory/remove");
            GatedPost(url, auth, body, (ok, resp) =>
            {
                if (!ok) { done?.Invoke(false); return; }
                RefreshAfterWrite(guestId, () =>
                {
                    try { OnInventoryChanged?.Invoke(); } catch (Exception e) { Debug.LogWarning(e); }
                    done?.Invoke(true);
                });
            });
        }

        static bool _warnedNoItemUpdate;

        /// <summary>
        /// Change the per-instance metadata of ONE unique inventory row (a
        /// caught creature's level, a pet's nickname…). <paramref name="instanceId"/>
        /// is the row's key: the <c>itemId</c>/<c>instanceId</c> AwardItem's
        /// callback handed back (<c>{baseId}_{8 hex}</c>), never the base id.
        ///
        /// The patch MERGES into the stored metadata (top-level keys replaced,
        /// a JSON null deletes a key) unless <paramref name="replace"/> is set.
        /// Either way the row keeps its <c>contentId</c>. The cached row changes
        /// at once (optimistic) and is confirmed by the refetch; a server
        /// refusal rolls the cache back.
        ///
        /// Server rules (POST /app/profile/inventory/update, Oct 2026): only
        /// rows minted with metadata (409 ITEM_NOT_UNIQUE otherwise), 4 KB of
        /// JSON per row, the row must belong to the calling content when both
        /// name one (403 ITEM_NOT_OWNED), and it shares the per-guest award
        /// budget (60 writes/min), so update on game events (a battle won),
        /// not per frame. Against a backend without the route the change stays
        /// local and done gets false.
        /// </summary>
        public static void UpdateItem(string instanceId, JSONObject metadata, bool replace = false, Action<bool, ProfileItem> done = null)
        {
            if (!IsBound) { Debug.LogWarning("[ProfileAPI] UpdateItem with no identity bound."); done?.Invoke(false, null); return; }
            if (string.IsNullOrEmpty(instanceId) || metadata == null || metadata.type != JSONObject.Type.Object) { done?.Invoke(false, null); return; }

            // Optimistic local merge so synchronous reads see the new values.
            // EXACT instance match only: GetItem also matches the base id, and
            // patching "the first cr_fox" for a typo'd key would be wrong twice.
            var cached = GetItemInstance(instanceId);
            JSONObject before = null;
            if (cached != null)
            {
                before = cached.metadata != null ? cached.metadata.Copy() : null;
                cached.metadata = MergeItemMetadata(cached.metadata, metadata, replace);
            }

            var body = new JSONObject(JSONObject.Type.Object);
            if (Source == ProfileSource.Headset) body.AddField("headsetId", HeadsetIdHeader());
            body.AddField("itemId", instanceId);
            body.AddField("metadata", metadata);
            if (replace) body.AddField("replace", true);
            var cid = CallingContentId();
            if (!string.IsNullOrEmpty(cid)) body.AddField("contentId", cid);

            var (url, auth) = PickWrite("inventory/update");
            GatedPost(url, auth, body, (ok, resp) =>
            {
                if (!ok)
                {
                    // A JSON refusal carries a code (ITEM_NOT_FOUND / _UNIQUE /
                    // _OWNED, METADATA_*): the server said no, so undo. A 404
                    // with no JSON is an older backend without the route: keep
                    // the local change for the session and say so once.
                    string code = resp?.json?.GetField("code")?.stringValue;
                    if (!string.IsNullOrEmpty(code))
                    {
                        if (cached != null) cached.metadata = before;
                        Debug.LogWarning($"[ProfileAPI] UpdateItem '{instanceId}' refused: {code} — {resp.json.GetField("error")?.stringValue}");
                    }
                    else if (resp != null && resp.statusCode == 404 && !_warnedNoItemUpdate)
                    {
                        _warnedNoItemUpdate = true;
                        Debug.LogWarning("[ProfileAPI] UpdateItem: this backend has no /app/profile/inventory/update yet — metadata changes are local only until it ships.");
                    }
                    done?.Invoke(false, cached);
                    return;
                }
                FetchProfile(ContentFilter, (_, __) =>
                {
                    try { OnInventoryChanged?.Invoke(); } catch (Exception e) { Debug.LogWarning(e); }
                    done?.Invoke(true, GetItemInstance(instanceId));
                });
            });
        }

        /// <summary>The cached row whose key is exactly this instance id.</summary>
        public static ProfileItem GetItemInstance(string instanceId)
        {
            if (string.IsNullOrEmpty(instanceId)) return null;
            for (int i = 0; i < _items.Count; i++)
            {
                var it = _items[i];
                if (it.instanceId == instanceId || it.itemId == instanceId) return it;
            }
            return null;
        }

        // Mirror of the server's merge (DreamPark-Web lib/inventoryItems.js
        // mergeItemMetadata), so the optimistic cache and the refetch agree.
        static JSONObject MergeItemMetadata(JSONObject existing, JSONObject patch, bool replace)
        {
            bool hasBase = existing != null && existing.type == JSONObject.Type.Object;
            var next = (replace || !hasBase) ? new JSONObject(JSONObject.Type.Object) : existing.Copy();
            for (int i = 0; i < patch.keys.Count; i++)
            {
                var v = patch.list[i];
                if (v == null || v.type == JSONObject.Type.Null) next.RemoveField(patch.keys[i]);
                else next.SetField(patch.keys[i], CloneJson(v));
            }
            var keep = hasBase ? existing.GetField("contentId") : null;
            if (keep != null && keep.type == JSONObject.Type.String && !string.IsNullOrEmpty(keep.stringValue))
                next.SetField("contentId", keep.stringValue);
            return next;
        }

        // JSONObject.Copy() round-trips through Print + parse, which throws on a
        // bare primitive (a number, string or bool): clone those by value.
        static JSONObject CloneJson(JSONObject v)
        {
            if (v == null) return JSONObject.nullObject;
            switch (v.type)
            {
                case JSONObject.Type.String: return JSONObject.CreateStringObject(v.stringValue);
                case JSONObject.Type.Number: return v.isInteger ? JSONObject.Create(v.longValue) : JSONObject.Create(v.doubleValue);
                case JSONObject.Type.Bool:   return JSONObject.Create(v.boolValue);
                case JSONObject.Type.Object:
                case JSONObject.Type.Array:  return v.Copy();
                default:                     return JSONObject.nullObject;
            }
        }

        /// <summary>
        /// Strip a badge off the user. Idempotent — no-op if the user
        /// didn't own the badge.
        /// </summary>
        public static void RemoveBadge(string badgeId, Action<bool> done = null)
        {
            if (!CanWrite(out string guestId)) { Debug.LogWarning("[ProfileAPI] RemoveBadge with no identity bound."); done?.Invoke(false); return; }
            if (string.IsNullOrEmpty(badgeId)) { done?.Invoke(false); return; }

            // Same scoping as the award: a removal has to name the same badge
            // the award named, or a short id resolves somewhere else — or
            // nowhere, which is a removal that silently no-ops.
            var contentId = CallingContentId();

            var body = new JSONObject(JSONObject.Type.Object);
            if (Source == ProfileSource.Headset) body.AddField("headsetId", HeadsetIdHeader());
            body.AddField("badgeId", badgeId);
            if (!string.IsNullOrEmpty(contentId)) body.AddField("contentId", contentId);
            if (guestId != null) body.AddField("guestId", guestId);

            var (url, auth) = PickWrite("badges/remove");

            GatedPost(url, auth, body, (ok, resp) =>
            {
                if (!ok) { done?.Invoke(false); return; }
                RefreshAfterWrite(guestId, () => done?.Invoke(true));
            });
        }

        /// <summary>
        /// Heartbeat the current play session — absolute totals, NOT deltas.
        /// The server reads the session doc, diffs old vs new, and credits
        /// only the difference. Repeating identical payloads is a no-op;
        /// dropped heartbeats are recovered on the next successful call.
        ///
        /// `sessionId` is a UUID minted by the SDK at park-load. `startedAt`
        /// is recorded at park-load too — so the first heartbeat after a
        /// late login credits ALL elapsed time (including the anonymous
        /// portion before the user paired).
        ///
        /// parkId is optional — when omitted, the backend uses the bound
        /// session's locationId.
        ///
        /// This is the LOW-LEVEL write. SessionReporter wraps it with
        /// automatic park-load detection + 60s ticker + zone tracking;
        /// most creators won't call this directly.
        /// </summary>
        public static void SendSessionHeartbeat(
            string sessionId,
            DateTime startedAtUtc,
            float durationSeconds,
            Dictionary<string, float> contentTimes,
            string parkId = null,
            bool ended = false,
            Action<bool> done = null)
        {
            if (!IsBound) { done?.Invoke(false); return; }
            if (string.IsNullOrEmpty(sessionId)) { Debug.LogWarning("[ProfileAPI] SessionHeartbeat empty sessionId."); done?.Invoke(false); return; }

            var body = new JSONObject();
            if (Source == ProfileSource.Headset) body.AddField("headsetId", HeadsetIdHeader());
            body.AddField("sessionId", sessionId);
            body.AddField("startedAt", startedAtUtc.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture));
            body.AddField("durationSeconds", durationSeconds);
            if (!string.IsNullOrEmpty(parkId)) body.AddField("parkId", parkId);

            // Per-content time map. Empty object is fine — server treats it
            // as "no per-game breakdown for this heartbeat" (still credits
            // session total to parkStats).
            var ct = new JSONObject(JSONObject.Type.Object);
            if (contentTimes != null)
            {
                foreach (var kv in contentTimes)
                {
                    if (string.IsNullOrEmpty(kv.Key) || kv.Value <= 0f) continue;
                    ct.AddField(kv.Key, kv.Value);
                }
            }
            body.AddField("contentTimes", ct);

            var suffix = ended ? "session/end" : "session/heartbeat";
            var (url, auth) = PickWrite(suffix);
            DreamParkAPI.POST(url, auth, body, (ok, resp) =>
            {
                if (!ok) Debug.LogWarning($"[ProfileAPI] {suffix} failed.");
                done?.Invoke(ok);
            });
        }

        /// <summary>
        /// Award DreamPoints to the bound user. PRODUCTION HEADSET ONLY —
        /// the server rejects this call when authenticated with an SDK
        /// preview key (403, sdkPreviewBlocked: true). SDK builds can wire
        /// the call into their game logic, but minting only succeeds in
        /// real Quest builds where the embedded Unity API key is present.
        ///
        /// Anonymous DreamID bindings are also rejected server-side —
        /// DreamPoints live on the `users` collection only.
        ///
        /// `reason` is recorded on the ledger for audit (e.g. "quest_complete").
        /// </summary>
        public static void AddDreamPoints(int amount, string reason = null, Action<bool, int /* newBalance */> done = null)
        {
            if (!IsBound) { Debug.LogWarning("[ProfileAPI] AddDreamPoints with no identity bound."); done?.Invoke(false, DreamPoints); return; }
            if (amount <= 0) { Debug.LogWarning("[ProfileAPI] AddDreamPoints requires a positive amount."); done?.Invoke(false, DreamPoints); return; }

            var body = new JSONObject(JSONObject.Type.Object);
            if (Source == ProfileSource.Headset) body.AddField("headsetId", HeadsetIdHeader());
            body.AddField("amount", amount);
            // Tells the server which attraction is asking. Only approved
            // content can move real points; anything else no-ops server-side.
            body.AddField("contentId", CallingContentId());
            if (!string.IsNullOrEmpty(reason)) body.AddField("reason", reason);

            var (url, auth) = PickWrite("dreampoints/add");
            GatedPost(url, auth, body, (ok, resp) =>
            {
                if (!ok)
                {
                    var err = resp?.error ?? "request failed";
                    Debug.LogWarning("[ProfileAPI] AddDreamPoints failed: " + err);
                    done?.Invoke(false, DreamPoints);
                    return;
                }
                WarnIfPointsNotApplied(resp?.json, "add");
                int prev = DreamPoints;
                int newBalance = (int)(resp?.json?.GetField("dreamPoints")?.floatValue ?? prev + amount);
                DreamPoints = newBalance;
                try { OnDreamPointsChanged?.Invoke(newBalance, newBalance - prev); } catch (Exception e) { Debug.LogWarning(e); }
                done?.Invoke(true, newBalance);
            });
        }

        /// <summary>
        /// Spend DreamPoints from the bound user's balance. Server rejects on
        /// insufficient balance (HTTP 402). Works with both production and
        /// SDK preview auth — devs need to test purchase flows in editor.
        ///
        /// `reason` is recorded on the ledger (e.g. "shop:cosmetic_hat_red").
        /// </summary>
        public static void SpendDreamPoints(int amount, string reason = null, Action<bool, int /* newBalance */> done = null)
        {
            if (!IsBound) { Debug.LogWarning("[ProfileAPI] SpendDreamPoints with no identity bound."); done?.Invoke(false, DreamPoints); return; }
            if (amount <= 0) { Debug.LogWarning("[ProfileAPI] SpendDreamPoints requires a positive amount."); done?.Invoke(false, DreamPoints); return; }

            var body = new JSONObject(JSONObject.Type.Object);
            if (Source == ProfileSource.Headset) body.AddField("headsetId", HeadsetIdHeader());
            body.AddField("amount", amount);
            // Tells the server which attraction is asking. Only approved
            // content can move real points; anything else no-ops server-side.
            body.AddField("contentId", CallingContentId());
            if (!string.IsNullOrEmpty(reason)) body.AddField("reason", reason);

            var (url, auth) = PickWrite("dreampoints/spend");
            GatedPost(url, auth, body, (ok, resp) =>
            {
                if (!ok)
                {
                    var err = resp?.error ?? "request failed";
                    Debug.LogWarning("[ProfileAPI] SpendDreamPoints failed: " + err);
                    done?.Invoke(false, DreamPoints);
                    return;
                }
                int prev = DreamPoints;
                WarnIfPointsNotApplied(resp?.json, "spend");
                int newBalance = (int)(resp?.json?.GetField("dreamPoints")?.floatValue ?? Math.Max(0, prev - amount));
                DreamPoints = newBalance;
                try { OnDreamPointsChanged?.Invoke(newBalance, newBalance - prev); } catch (Exception e) { Debug.LogWarning(e); }
                done?.Invoke(true, newBalance);
            });
        }

        // ── JSON hydration ───────────────────────────────────────────────
        public class ProfileSnapshot
        {
            public string identityKind;
            public string identityId;
            public string displayName;
            public string avatarUrl;
            public bool   isAnonymous;
            public float  heightInches = ReferenceHeightInches;
            public float  heightFactor => heightInches / ReferenceHeightInches;
            public int    dreamPoints;
            public List<ProfileItem>        items        = new List<ProfileItem>();
            public List<ProfileAchievement> achievements = new List<ProfileAchievement>();
            public List<ProfileBadge>       badges       = new List<ProfileBadge>();
        }

        static ProfileSnapshot HydrateProfileSnapshot(JSONObject json)
        {
            var b = new ProfileSnapshot();
            if (json == null) return b;

            var identity = json.GetField("identity");
            if (identity != null)
            {
                b.identityKind = identity.GetField("kind")?.stringValue;
                b.identityId   = identity.GetField("id")?.stringValue;
            }

            // The bound account's display identity (displayName/avatarUrl/email).
            // Backend buildProfileSnapshot returns this on /app/profile, /claim,
            // and /api/pairing/profile-self. Null fields = anonymous DreamID.
            var profile = json.GetField("profile");
            Debug.Log($"[ProfileAPI] hydrate (baseUrl={DreamParkAPI.baseUrl}) raw profile block = {(profile != null ? profile.Print() : "NULL")}");
            if (profile != null)
            {
                b.displayName = profile.GetField("displayName")?.stringValue;
                b.avatarUrl   = profile.GetField("avatarUrl")?.stringValue;
                b.isAnonymous = profile.GetField("isAnonymous")?.boolValue ?? (b.identityKind == "dreamid");
                b.heightInches = ReadHeightInches(profile);
            }

            // A headset GUEST SESSION (anonymous, clean slate — Oct 12 2026): show
            // the guest's name (e.g. "Zoocade 1") when the session has none of its own.
            var guestSession = json.GetField("guestSession");
            if (string.IsNullOrEmpty(b.displayName) && guestSession != null && guestSession.type == JSONObject.Type.Object)
                b.displayName = guestSession.GetField("guestName")?.stringValue;

            // DreamPoints balance — 0 for anonymous DreamID bindings.
            b.dreamPoints = (int)(json.GetField("dreamPoints")?.floatValue ?? 0);

            var inv = json.GetField("inventory");
            if (inv != null && inv.type == JSONObject.Type.Array && inv.list != null)
            {
                for (int i = 0; i < inv.list.Count; i++)
                {
                    var entry = inv.list[i];
                    var meta  = entry.GetField("metadata");

                    // Helper: prefer top-level catalog field, fall back to
                    // per-instance metadata. This is the key to making
                    // GetItemByType / GetItemByName work for items granted
                    // through ad-hoc flows (anonymous web onboarding, custom
                    // award calls) that put descriptive fields in metadata
                    // instead of in the items/{id} catalog doc.
                    string getStr(string topKey, string metaKey = null)
                    {
                        var top = entry.GetField(topKey)?.stringValue;
                        if (!string.IsNullOrEmpty(top)) return top;
                        if (meta != null) {
                            var m = meta.GetField(metaKey ?? topKey)?.stringValue;
                            if (!string.IsNullOrEmpty(m)) return m;
                        }
                        return null;
                    }

                    var it = new ProfileItem
                    {
                        itemId      = entry.GetField("itemId")?.stringValue,
                        instanceId  = entry.GetField("instanceId")?.stringValue,
                        baseItemId  = entry.GetField("baseItemId")?.stringValue,
                        name        = getStr("name"),
                        type        = getStr("type"),
                        contentId   = getStr("contentId"),
                        gameId      = entry.GetField("gameId")?.stringValue,
                        rarity      = getStr("rarity"),
                        iconUri     = getStr("iconUri") ?? entry.GetField("icon")?.stringValue,
                        modelUri    = getStr("modelUri"),
                        amount      = (int)(entry.GetField("amount")?.floatValue ?? 1),
                        metadata    = meta,
                        raw         = entry,
                    };
                    if (string.IsNullOrEmpty(it.contentId)) it.contentId = it.gameId;
                    b.items.Add(it);
                }
            }

            var achievements = json.GetField("achievements");
            if (achievements != null && achievements.type == JSONObject.Type.Array && achievements.list != null)
            {
                for (int i = 0; i < achievements.list.Count; i++)
                {
                    var entry = achievements.list[i];
                    b.achievements.Add(new ProfileAchievement
                    {
                        achievementId = entry.GetField("achievementId")?.stringValue,
                        progress      = entry.GetField("progress")?.floatValue ?? 0,
                        completed     = entry.GetField("completed")?.boolValue ?? false,
                        maxValue      = entry.GetField("maxValue")?.floatValue ?? 0,
                        name          = entry.GetField("name")?.stringValue,
                        contentId     = entry.GetField("contentId")?.stringValue,
                        raw           = entry,
                    });
                }
            }

            var badges = json.GetField("badges");
            if (badges != null && badges.type == JSONObject.Type.Array && badges.list != null)
            {
                for (int i = 0; i < badges.list.Count; i++)
                {
                    var entry = badges.list[i];
                    b.badges.Add(new ProfileBadge
                    {
                        badgeId      = entry.GetField("badgeId")?.stringValue,
                        name         = entry.GetField("name")?.stringValue,
                        contentId    = entry.GetField("contentId")?.stringValue,
                        iconUri      = entry.GetField("iconUri")?.stringValue,
                        description  = entry.GetField("description")?.stringValue,
                        awardedAtMs  = ParseTimestampMs(entry.GetField("awardedAt")),
                        raw          = entry,
                    });
                }
            }

            return b;
        }

        // Keep transport validation at the boundary. Older backends omit the
        // field, and malformed/custom responses must never produce NaN or a
        // dangerous ergonomic scale inside game logic.
        static float ReadHeightInches(JSONObject profile)
        {
            var value = profile?.GetField("heightInches");
            if (value == null || value.type != JSONObject.Type.Number) return ReferenceHeightInches;
            return NormalizeHeightInches(value.floatValue);
        }

        static float NormalizeHeightInches(float inches)
        {
            if (float.IsNaN(inches) || float.IsInfinity(inches)
                || inches != Mathf.Round(inches)
                || inches < MinimumSupportedHeightInches
                || inches > MaximumSupportedHeightInches)
                return ReferenceHeightInches;

            return inches;
        }

        // ── Helpers ──────────────────────────────────────────────────────
        static string UnityWebRequestEscape(string s) =>
            System.Uri.EscapeDataString(s ?? "");

        // Tolerant Firestore timestamp parse — handles both shapes the backend
        // can produce:
        //   1. { _seconds: 12345, _nanoseconds: 0 } (Admin SDK toJSON default)
        //   2. "2026-05-27T10:00:00.000Z"           (ISO 8601 string)
        //   3. 1716800000000                        (raw epoch ms)
        // Returns 0 if the field is null/missing/unparseable.
        static double ParseTimestampMs(JSONObject t)
        {
            if (t == null || t.type == JSONObject.Type.Null) return 0;

            // Shape 1 — Firestore admin object
            if (t.type == JSONObject.Type.Object)
            {
                var seconds = t.GetField("_seconds")?.floatValue ?? 0;
                if (seconds > 0) return seconds * 1000;
            }
            // Shape 2 — ISO string
            if (t.type == JSONObject.Type.String && !string.IsNullOrEmpty(t.stringValue))
            {
                if (DateTime.TryParse(t.stringValue, null,
                        System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                        out var dt))
                {
                    return new DateTimeOffset(dt).ToUnixTimeMilliseconds();
                }
            }
            // Shape 3 — raw epoch number (seconds or ms — disambiguate)
            if (t.type == JSONObject.Type.Number)
            {
                var n = t.floatValue;
                return n > 10_000_000_000 ? n : n * 1000; // > ~Sat 2286-Nov-20 in seconds = surely ms
            }
            return 0;
        }

        // ── Lua bridge ───────────────────────────────────────────────────
        // Registered into LuaBehaviour.luaEnv.Global on the very first scene
        // load, before any LuaBehaviour's Awake fires. Lua scripts get a
        // global `dp` table:  dp.profile.getItemByType("wand"), etc.
        //
        // IMPORTANT — IL2CPP / Quest portability:
        // We intentionally do NOT return raw C# ProfileItem/Achievement/Badge
        // objects across the Lua boundary. Returning a custom C# type would
        // force every consumer to add [LuaCallCSharp] to those types and run
        // "XLua → Generate Code" before every release. Instead we convert
        // each object into a native LuaTable here. Lua scripts get a plain
        // table they can read with dot notation, no wrap generation needed.
        // ──────────────────────────────────────────────────────────────────
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterAssembliesLoaded)]
        static void RegisterLua()
        {
            try
            {
                var env = LuaBehaviour.GetLuaEnv();
                if (env == null) return;

                // ── primitive accessors ─────────────────────────────────
                env.Global.Set("dp_profile_is_loaded",     new Func<bool>(()                      => IsLoaded));
                env.Global.Set("dp_profile_is_bound",      new Func<bool>(()                      => IsBound));
                env.Global.Set("dp_profile_identity",      new Func<string>(()                    => IdentitySegment()));
                env.Global.Set("dp_profile_content_id",    new Func<string>(()                    => ContentFilter));
                env.Global.Set("dp_profile_on_ready",      new Action<Action>(cb                  => OnReady(cb)));
                env.Global.Set("dp_profile_subscribe_height_changed",
                    new Func<Action<float, float>, int>(cb => SubscribeLuaHeightChanged(cb)));
                env.Global.Set("dp_profile_unsubscribe_height_changed",
                    new Action<int>(id => UnsubscribeLuaHeightChanged(id)));
                env.Global.Set("dp_profile_refresh",       new Action(()                          => FetchProfile(ContentFilter, null)));

                // ── readers — return LuaTable (or nil), not C# types ────
                env.Global.Set("dp_profile_get_item",          new Func<string, LuaTable>(id => ItemToLuaTable(env, GetItem(id))));
                env.Global.Set("dp_profile_get_item_by_type",  new Func<string, LuaTable>(t  => ItemToLuaTable(env, GetItemByType(t))));
                env.Global.Set("dp_profile_get_item_by_name",  new Func<string, LuaTable>(n  => ItemToLuaTable(env, GetItemByName(n))));
                env.Global.Set("dp_profile_has_item",          new Func<string, bool>(HasItem));
                env.Global.Set("dp_profile_get_items",         new Func<string, string, string, LuaTable>((b, t, c) => ItemsToLuaTable(env, GetItems(b, t, c))));
                env.Global.Set("dp_profile_subscribe_inventory",   new Func<Action, int>(cb => SubscribeLuaInventoryChanged(cb)));
                env.Global.Set("dp_profile_unsubscribe_inventory", new Action<int>(id => UnsubscribeLuaInventoryChanged(id)));
                env.Global.Set("dp_profile_has_item_by_type",  new Func<string, bool>(HasItemByType));
                env.Global.Set("dp_profile_get_achievement",   new Func<string, LuaTable>(id => AchievementToLuaTable(env, GetAchievement(id))));
                env.Global.Set("dp_profile_has_achievement",   new Func<string, bool>(HasAchievement));
                env.Global.Set("dp_profile_get_badge",         new Func<string, LuaTable>(id => BadgeToLuaTable(env, GetBadge(id))));
                env.Global.Set("dp_profile_has_badge",         new Func<string, bool>(HasBadge));
                env.Global.Set("dp_profile_get_dreampoints",   new Func<int>(()                       => DreamPoints));
                env.Global.Set("dp_profile_get_height_inches", new Func<float>(()                     => HeightInches));
                env.Global.Set("dp_profile_get_height_meters", new Func<float>(()                     => HeightMeters));
                env.Global.Set("dp_profile_get_height_factor", new Func<float>(()                     => HeightFactor));

                // ── writers ─────────────────────────────────────────────
                // awardItem accepts a makeUnique bool — when true, we
                // send a stub metadata object so the backend appends a
                // random suffix to the inventory key and forces amount=1.
                // Any truthy `metadata` triggers the unique path server-side
                // (see /api/user/inventory/add + /app/profile/inventory/add).
                env.Global.Set("dp_profile_award_unique_item", new Action<string>(id => AwardUniqueItem(id)));
                env.Global.Set("dp_profile_award_item",        new Action<string, int, bool>((id, amt, unique) => {
                    JSONObject meta = null;
                    if (unique) {
                        meta = new JSONObject(JSONObject.Type.Object);
                        meta.AddField("unique", true);
                    }
                    AwardItem(id, amt, meta);
                }));
                // Metadata arrives as a JSON string encoded by the Lua shim
                // below (no LuaTable walking on the C# side, no codegen).
                // Callbacks are raw LuaFunctions, called with (ok, itemTable).
                env.Global.Set("dp_profile_award_item_json",   new Action<string, int, string, LuaFunction>((id, amt, json, cb) => {
                    JSONObject meta = string.IsNullOrEmpty(json) ? null : new JSONObject(json);
                    if (meta != null && meta.type != JSONObject.Type.Object) meta = null;
                    AwardItem(id, amt <= 0 ? 1 : amt, meta, (ok, it) => CallLua(cb, ok, ItemToLuaTable(env, it)));
                }));
                env.Global.Set("dp_profile_update_item_json",  new Action<string, string, bool, LuaFunction>((id, json, replace, cb) => {
                    JSONObject meta = string.IsNullOrEmpty(json) ? null : new JSONObject(json);
                    UpdateItem(id, meta, replace, (ok, it) => CallLua(cb, ok, ItemToLuaTable(env, it)));
                }));
                env.Global.Set("dp_profile_remove_item_cb",    new Action<string, int, LuaFunction>((id, amt, cb) =>
                    RemoveItem(id, amt <= 0 ? 1 : amt, ok => CallLua(cb, ok, null))));
                env.Global.Set("dp_profile_award_achievement", new Action<string, float>((id, p)  => AwardAchievement(id, p)));
                env.Global.Set("dp_profile_complete_achievement", new Action<string>(id            => CompleteAchievement(id)));
                env.Global.Set("dp_profile_award_badge",       new Action<string>(id              => AwardBadge(id)));
                env.Global.Set("dp_profile_remove_item",       new Action<string, int>((id, amt) => RemoveItem(id, amt)));
                env.Global.Set("dp_profile_remove_badge",      new Action<string>(id             => RemoveBadge(id)));
                env.Global.Set("dp_profile_add_dreampoints",   new Action<int, string>((amt, reason) => AddDreamPoints(amt, reason)));
                env.Global.Set("dp_profile_spend_dreampoints", new Action<int, string>((amt, reason) => SpendDreamPoints(amt, reason)));
                // Playtime is reported automatically by SessionReporter via
                // /app/profile/session/heartbeat. We deliberately don't expose
                // it to Lua — creators reaching for that surface usually want
                // game-specific gating which Lua-side accumulation can't get
                // right (see SessionReporter for the per-content tracking).

                // Ergonomic `dp.profile.*` namespace on top of the flat bindings.
                env.DoString(@"
                    dp = dp or {}
                    -- Minimal JSON encoder for item metadata (strings, numbers,
                    -- booleans, nested tables; a table with keys 1..n is an array).
                    local Q, BS = string.char(34), string.char(92)
                    local function enc_str(s)
                        s = s:gsub(BS, BS..BS):gsub(Q, BS..Q):gsub('\n', BS..'n'):gsub('\r', BS..'r'):gsub('\t', BS..'t')
                        s = s:gsub('[%c]', function(c) return string.format(BS..'u%04x', c:byte()) end)
                        return Q .. s .. Q
                    end
                    local function enc(v, depth)
                        local t = type(v)
                        if t == 'string' then return enc_str(v) end
                        if t == 'number' then
                            if v ~= v or v == math.huge or v == -math.huge then return 'null' end
                            return string.format('%.14g', v)
                        end
                        if t == 'boolean' then return tostring(v) end
                        if t ~= 'table' or depth > 8 then return 'null' end
                        local n = #v
                        local isArr = n > 0
                        if isArr then for k in pairs(v) do if type(k) ~= 'number' then isArr = false break end end end
                        local parts = {}
                        if isArr then
                            for i = 1, n do parts[#parts + 1] = enc(v[i], depth + 1) end
                            return '[' .. table.concat(parts, ',') .. ']'
                        end
                        for k, val in pairs(v) do
                            if type(val) ~= 'function' and type(val) ~= 'userdata' then
                                parts[#parts + 1] = enc_str(tostring(k)) .. ':' .. enc(val, depth + 1)
                            end
                        end
                        return '{' .. table.concat(parts, ',') .. '}'
                    end
                    function dp_profile_json(v) return enc(v, 0) end
                    dp.profile = {
                        isLoaded         = function() return dp_profile_is_loaded() end,
                        isBound          = function() return dp_profile_is_bound() end,
                        identity         = function() return dp_profile_identity() end,
                        contentId        = function() return dp_profile_content_id() end,
                        onReady          = function(cb) dp_profile_on_ready(cb) end,
                        refresh          = function() dp_profile_refresh() end,

                        getItem          = function(id)   return dp_profile_get_item(id) end,
                        getItemByType    = function(t)    return dp_profile_get_item_by_type(t) end,
                        getItemByName    = function(n)    return dp_profile_get_item_by_name(n) end,
                        hasItem          = function(id)   return dp_profile_has_item(id) end,
                        hasItemByType    = function(t)    return dp_profile_has_item_by_type(t) end,
                        -- Every matching row as an array of item tables (each
                        -- unique instance separately, with instanceId and
                        -- metadata). filter: nil = all, a string = base item
                        -- id, or {baseItemId=, type=, contentId=}.
                        getItems         = function(f)
                            if type(f) == 'table' then return dp_profile_get_items(f.baseItemId, f.type, f.contentId) or {} end
                            return dp_profile_get_items(f, nil, nil) or {}
                        end,
                        -- Persistent; fires after every inventory load/refresh/write.
                        -- Returns an idempotent unsubscribe closure.
                        onInventoryChanged = function(cb)
                            local id = dp_profile_subscribe_inventory(cb)
                            local active = true
                            return function()
                                if not active then return end
                                active = false
                                if id ~= 0 then dp_profile_unsubscribe_inventory(id) end
                            end
                        end,
                        supportsItemMetadata = true,

                        getAchievement   = function(id)   return dp_profile_get_achievement(id) end,
                        hasAchievement   = function(id)   return dp_profile_has_achievement(id) end,

                        getBadge         = function(id)   return dp_profile_get_badge(id) end,
                        hasBadge         = function(id)   return dp_profile_has_badge(id) end,

                        getDreamPoints   = function()     return dp_profile_get_dreampoints() end,
                        -- Absolute values plus a scale relative to the 5ft 8in
                        -- authoring reference. All three safely return the
                        -- reference values until an older/missing profile loads.
                        getHeightInches  = function()     return dp_profile_get_height_inches() end,
                        getHeightMeters  = function()     return dp_profile_get_height_meters() end,
                        getHeightFactor  = function()     return dp_profile_get_height_factor() end,
                        -- Persistent subscription for the active profile.
                        -- Returns an idempotent unsubscribe closure. Register
                        -- after/onReady; identity clear also removes it.
                        onHeightChanged  = function(cb)
                            local id = dp_profile_subscribe_height_changed(cb)
                            local active = true
                            return function()
                                if not active then return end
                                active = false
                                if id ~= 0 then dp_profile_unsubscribe_height_changed(id) end
                            end
                        end,

                        -- awardItem(id, amt, unique|metadata, cb): pass a table as the
                        -- third argument to mint a unique instance carrying that
                        -- metadata; cb(ok, item) gets the exact row written.
                        awardItem        = function(id, amt, u, cb)
                            if type(u) == 'table' or cb ~= nil then
                                local meta = u
                                if meta == true then meta = { unique = true } end
                                if type(meta) ~= 'table' then meta = nil end
                                return dp_profile_award_item_json(id, amt or 1, meta and dp_profile_json(meta) or '', cb)
                            end
                            dp_profile_award_item(id, amt or 1, u or false)
                        end,
                        -- One distinct instance per call. For a one-of-a-kind
                        -- item, untick Stacks on the catalog entry and use
                        -- awardItem instead. (No double quotes in this block:
                        -- it is a C# verbatim string, where a bare quote ends
                        -- the literal. Escape as '' if you ever need one.)
                        -- awardUniqueItem(id[, metadata[, cb]]): metadata is your own
                        -- per-instance data (a table of strings/numbers/bools/tables).
                        awardUniqueItem  = function(id, meta, cb)
                            if meta == nil and cb == nil then return dp_profile_award_unique_item(id) end
                            if type(meta) ~= 'table' then meta = { unique = true } end
                            return dp_profile_award_item_json(id, 1, dp_profile_json(meta), cb)
                        end,
                        -- Alias: reward* reads naturally to some callers, and
                        -- both names are cheap to keep pointing at one impl.
                        rewardUniqueItem = function(id, meta, cb) return dp.profile.awardUniqueItem(id, meta, cb) end,
                        -- updateItem(instanceId, patch[, cb[, replace]]): merge the patch
                        -- into that row's metadata (replace=true overwrites it).
                        updateItem       = function(id, meta, cb, replace)
                            return dp_profile_update_item_json(id, dp_profile_json(meta or {}), replace or false, cb)
                        end,
                        -- awardAchievement(id)     → mark it complete (one-shot task)
                        -- awardAchievement(id, n)  → add n progress toward it
                        awardAchievement = function(id, p)
                            if p == nil then return dp_profile_complete_achievement(id) end
                            return dp_profile_award_achievement(id, p)
                        end,
                        completeAchievement = function(id) return dp_profile_complete_achievement(id) end,
                        awardBadge       = function(id)       dp_profile_award_badge(id) end,
                        removeItem       = function(id, amt, cb)
                            if cb ~= nil then return dp_profile_remove_item_cb(id, amt or 1, cb) end
                            dp_profile_remove_item(id, amt or 1)
                        end,
                        removeBadge      = function(id)       dp_profile_remove_badge(id) end,
                        -- Production-only: server rejects from SDK preview builds. Wire it
                        -- into your game logic; mints succeed once shipped through Core.
                        addDreamPoints   = function(amt, reason) dp_profile_add_dreampoints(amt or 0, reason) end,
                        spendDreamPoints = function(amt, reason) dp_profile_spend_dreampoints(amt or 0, reason) end,
                    }
                ", "dp.profile.bootstrap");
            }
            catch (Exception e)
            {
                Debug.LogError("[ProfileAPI] Failed to register Lua bridge: " + e);
            }
        }

        // ── C# → LuaTable marshallers ────────────────────────────────────
        // Convert our domain objects into native Lua tables so Lua scripts
        // get plain values they can read with dot notation without XLua
        // having to generate wraps for ProfileItem/Achievement/Badge.

        static LuaTable ItemToLuaTable(XLua.LuaEnv env, ProfileItem it)
        {
            if (it == null) return null;
            var t = env.NewTable();
            t.Set("itemId",     it.itemId);
            t.Set("instanceId", string.IsNullOrEmpty(it.instanceId) ? it.itemId : it.instanceId);
            t.Set("baseItemId", it.baseItemId);
            t.Set("name",       it.name);
            t.Set("type",       it.type);
            t.Set("contentId",  it.contentId);
            t.Set("gameId",     it.gameId);
            t.Set("rarity",     it.rarity);
            t.Set("iconUri",    it.iconUri);
            t.Set("modelUri",   it.modelUri);
            t.Set("amount",     it.amount);
            // metadata: a free-form sub-table. Convert recursively from JSON.
            t.Set("metadata",   JsonObjectToLuaTable(env, it.metadata));
            return t;
        }

        static LuaTable ItemsToLuaTable(XLua.LuaEnv env, List<ProfileItem> items)
        {
            var t = env.NewTable();
            for (int i = 0; i < items.Count; i++) t.Set(i + 1, ItemToLuaTable(env, items[i]));
            return t;
        }

        static void CallLua(LuaFunction cb, bool ok, LuaTable item)
        {
            if (cb == null) return;
            try { cb.Call(ok, item); }
            catch (Exception e) { Debug.LogWarning($"[ProfileAPI] Lua item callback threw: {e}"); }
        }

        static LuaTable AchievementToLuaTable(XLua.LuaEnv env, ProfileAchievement a)
        {
            if (a == null) return null;
            var t = env.NewTable();
            t.Set("achievementId", a.achievementId);
            t.Set("progress",      a.progress);
            t.Set("completed",     a.completed);
            t.Set("maxValue",      a.maxValue);
            t.Set("name",          a.name);
            t.Set("contentId",     a.contentId);
            return t;
        }

        static LuaTable BadgeToLuaTable(XLua.LuaEnv env, ProfileBadge b)
        {
            if (b == null) return null;
            var t = env.NewTable();
            t.Set("badgeId",     b.badgeId);
            t.Set("name",        b.name);
            t.Set("contentId",   b.contentId);
            t.Set("iconUri",     b.iconUri);
            t.Set("description", b.description);
            t.Set("awardedAtMs", b.awardedAtMs);
            return t;
        }

        // Recursive JSON → Lua table for metadata sub-objects. Returns nil
        // if json is null/missing; primitives boxed into typed values that
        // XLua can natively marshal. Whole numbers arrive as Lua integers
        // (level 5 prints "5", not "5.0"; a 64-bit id survives), others as
        // doubles — never float, which rounded anything past 2^24.
        static LuaTable JsonObjectToLuaTable(XLua.LuaEnv env, JSONObject obj)
        {
            if (obj == null || obj.type == JSONObject.Type.Null) return null;
            var t = env.NewTable();
            if (obj.type == JSONObject.Type.Object)
            {
                for (int i = 0; i < obj.keys.Count; i++)
                {
                    var k = obj.keys[i];
                    var v = obj.list[i];
                    switch (v.type)
                    {
                        case JSONObject.Type.String: t.Set(k, v.stringValue); break;
                        case JSONObject.Type.Number: if (v.isInteger) t.Set(k, v.longValue); else t.Set(k, v.doubleValue); break;
                        case JSONObject.Type.Bool:   t.Set(k, v.boolValue); break;
                        case JSONObject.Type.Object:
                        case JSONObject.Type.Array:  t.Set(k, JsonObjectToLuaTable(env, v)); break;
                        case JSONObject.Type.Null:   break;
                    }
                }
            }
            else if (obj.type == JSONObject.Type.Array)
            {
                for (int i = 0; i < obj.list.Count; i++)
                {
                    var v = obj.list[i];
                    switch (v.type)
                    {
                        case JSONObject.Type.String: t.Set(i + 1, v.stringValue); break;
                        case JSONObject.Type.Number: if (v.isInteger) t.Set(i + 1, v.longValue); else t.Set(i + 1, v.doubleValue); break;
                        case JSONObject.Type.Bool:   t.Set(i + 1, v.boolValue); break;
                        case JSONObject.Type.Object:
                        case JSONObject.Type.Array:  t.Set(i + 1, JsonObjectToLuaTable(env, v)); break;
                        case JSONObject.Type.Null:   break;
                    }
                }
            }
            return t;
        }
    }
}
