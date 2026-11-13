using System;
using System.Collections.Generic;
using System.Text;

namespace DreamPark
{
    /// <summary>One pinned badge as it travels to the players around you (presence "bd") and is drawn on your nameplate.</summary>
    public sealed class NameplateBadge
    {
        public string id, iconUrl, name;
    }

    /// <summary>
    /// THE NAMEPLATE over every remote player (Oct 28 2026, web-setup round; Aidan: "use the cool font from /hq and
    /// /setup for the name, and then display the user's avatar image AND their pinned badges when the player focuses on
    /// the username … keep the layout tight so it's not a big blob above each player all the time").
    ///
    /// Pure rules (plain .NET, no UnityEngine — tested by Tools/nameplate-offline/run.sh) for <see cref="RemoteNameTag"/>.
    /// ONE FIXED SIZE IN THE WORLD (metres) at every distance — no distance scaling.
    ///   • CLOSED: one slim pill, their round picture on the left (concentric with the left cap), the name in the display
    ///     face. OPEN: a profile card like the app's (ringed picture centred on top, the name centred under it, the pinned
    ///     badges in a row at the bottom), roughly square. The pill MORPHS into the card: the picture travels from the
    ///     pill's left end to centred on top, the name re-centres under it, the badges rise in at the bottom. The bottom
    ///     edge never moves (it sits just above the head), so the card grows UPWARD and never covers the face.
    ///   • OPENING IS DELIBERATE (Aidan: "it must not open by accident during gameplay"): you must be within
    ///     <see cref="OpenDistance"/>, the gaze ray must land ON the closed pill (+ <see cref="HitMargin"/>), and the head
    ///     must be steady (&lt; <see cref="SteadyDegPerSec"/>) for <see cref="FocusDwell"/> without a break. It closes fast:
    ///     <see cref="ReleaseDelay"/> after the gaze leaves the card (or you step back past <see cref="CloseDistance"/>),
    ///     then a <see cref="CollapseSeconds"/> close.
    ///   • the badge wire: the profile's pinned rows (pinnedRank, the server's /app/profile snapshot) → presence "bd".
    /// </summary>
    public static class NameplateRules
    {
        // ── Closed pill (metres; the size it had at 2 m before the fixed-size change) ──
        public const float AvatarCollapsed = 0.085f;
        /// Picture inset from the pill's edge (top, bottom and the left end): concentric with the left cap.
        public const float Inset = 0.020f;
        public const float Gap = 0.024f, PadRight = 0.042f;
        /// The name's em (TMP font size × MagicLook.TextUnitScale); its cap height ≈ <see cref="NameCapHalf"/> × 2.
        public const float NameEm = 0.057f;
        public const float NameCapHalf = NameEm * 0.36f;

        // ── Open card (the app's profile header: ringed picture on top, name, badge row) ──
        public const float AvatarFocused = 0.130f;
        public const float CardPadTop = 0.032f, CardPadSide = 0.050f, CardPadBottom = 0.036f;
        /// Picture → name cap top, name cap bottom → badge row top.
        public const float CardNameGap = 0.020f, CardRowGap = 0.028f;
        public const float CardCorner = 0.050f;
        public const float BadgeSize = 0.068f, BadgeGap = 0.014f;
        /// The badges rise in once the card has mostly formed (0..1 of the open), staggered left to right over this share.
        public const float BadgeStart = 0.55f, BadgeStagger = 0.18f;
        /// How far below its slot a badge starts its rise, and its starting scale.
        public const float BadgeRise = 0.025f, BadgeStartScale = 0.6f;
        /// The share of the open over which the card grows tall and the picture rises (the morph's first beat).
        public const float RiseShare = 0.4f;
        public const int MaxBadges = 5, MaxNameChars = 20;

        // ── Focus (deliberate open, quick close) ──
        /// Within 2 m: the distance you stand from someone you are actually talking to (the near end of "social
        /// distance"). In play, players are usually 2–4 m apart and moving, so a plate across the room never opens.
        public const float OpenDistance = 2.0f, CloseDistance = 2.3f;
        /// The gaze ray must land on the closed pill itself, with this much slack round it (metres).
        public const float HitMargin = 0.015f;
        /// While open, the gaze may wander over the card plus this much before it counts as looking away.
        public const float KeepMargin = 0.04f;
        /// Steady looking: 0.7 s without a break, head turning slower than this.
        public const float FocusDwell = 0.7f, SteadyDegPerSec = 45f;
        /// Looking away: 0.15 s grace, then a 0.18 s close.
        public const float ReleaseDelay = 0.15f;
        public const float ExpandSeconds = 0.40f, CollapseSeconds = 0.18f;

        public struct Layout
        {
            public float avatar, width, height, corner;
            /// Relative to the plate's bottom-centre (x right, y up): the picture's centre, the name's centre.
            public float avatarX, avatarY, nameX, nameY;
            public BadgePose[] badges;
        }

        public struct BadgePose { public float x, y, alpha, scale; }

        static float Clamp01(float x) => x < 0f ? 0f : x > 1f ? 1f : x;
        static float Lerp(float a, float b, float k) => a + (b - a) * k;
        /// The play session's picture stands in only for a peer that sends neither a name of its own nor a picture (an
        /// older app's guest). One that names itself and sends no picture has none (their initial on a circle), and the
        /// session's would be their old guest picture.
        public static bool SessionPictureApplies(string ownName, string fallbackName) =>
            string.IsNullOrEmpty(ownName) || ownName == fallbackName;

        public static float Smooth(float x) { x = Clamp01(x); return x * x * (3f - 2f * x); }
        public static float EaseOut(float x) { x = Clamp01(x); float i = 1f - x; return 1f - i * i * i; }
        public static float EaseIn(float x) { x = Clamp01(x); return x * x * x; }

        /// The closed pill's size for a name <paramref name="nameWidth"/> metres wide.
        public static void PillSize(float nameWidth, out float w, out float h)
        {
            h = AvatarCollapsed + 2f * Inset;
            w = Inset + AvatarCollapsed + Gap + Math.Max(0f, nameWidth) + PadRight;
        }

        /// The open card's size: content width + side padding, never narrower than it is tall (roughly square).
        public static void CardSize(float nameWidth, int badgeCount, out float w, out float h)
        {
            int n = Math.Max(0, Math.Min(MaxBadges, badgeCount));
            float row = n > 0 ? n * BadgeSize + (n - 1) * BadgeGap : 0f;
            h = CardPadTop + AvatarFocused + CardNameGap + 2f * NameCapHalf + (n > 0 ? CardRowGap + BadgeSize : 0f) + CardPadBottom;
            float content = Math.Max(Math.Max(nameWidth, row), AvatarFocused);
            w = Math.Max(content + 2f * CardPadSide, h);
        }

        /// The plate at open-ness <paramref name="t"/> (0 = the closed pill … 1 = the open card).
        public static Layout LayoutAt(float t, float nameWidth, int badgeCount)
        {
            t = Clamp01(t);
            int n = Math.Max(0, Math.Min(MaxBadges, badgeCount));
            PillSize(nameWidth, out float w0, out float h0);
            CardSize(nameWidth, n, out float w1, out float h1);
            float k = Smooth(t);
            // The card grows TALL first (ease-out over the first 40%) so the picture has room to rise above the name before
            // either moves across; the width follows the smooth curve.
            float kh = EaseOut(t / RiseShare);
            float w = Lerp(w0, w1, k), h = Lerp(h0, h1, kh);
            float corner = Math.Min(Lerp(h0 * 0.5f, CardCorner, k), 0.5f * Math.Min(w, h));

            // The picture rises first (ease-out up, smooth across) while the name re-centres under it: they never cross.
            float a = Lerp(AvatarCollapsed, AvatarFocused, k);
            float ax0 = -w0 * 0.5f + h0 * 0.5f, ay0 = h0 * 0.5f;
            float ay1 = h1 - CardPadTop - AvatarFocused * 0.5f;
            float ky = EaseOut(t / RiseShare);
            float nx0 = -w0 * 0.5f + Inset + AvatarCollapsed + Gap + nameWidth * 0.5f, ny0 = h0 * 0.5f;
            float ny1 = ay1 - AvatarFocused * 0.5f - CardNameGap - NameCapHalf;
            float kn = k;
            var L = new Layout
            {
                avatar = a, width = w, height = h, corner = corner,
                avatarX = Lerp(ax0, 0f, k), avatarY = Math.Min(Lerp(ay0, ay1, ky), h - Lerp(Inset, CardPadTop, kh) - a * 0.5f),
                nameX = Lerp(nx0, 0f, kn), nameY = Lerp(ny0, ny1, kn),
                badges = new BadgePose[n],
            };
            float row = n > 0 ? n * BadgeSize + (n - 1) * BadgeGap : 0f;
            float by = CardPadBottom + BadgeSize * 0.5f;
            for (int i = 0; i < n; i++)
            {
                float start = BadgeStart + i * BadgeStagger * (1f - BadgeStart) / n;
                float bt = EaseOut((t - start) / ((1f - BadgeStart) * (1f - BadgeStagger)));
                L.badges[i] = new BadgePose
                {
                    x = -row * 0.5f + BadgeSize * 0.5f + i * (BadgeSize + BadgeGap),
                    y = by - BadgeRise * (1f - bt),
                    alpha = bt,
                    scale = BadgeStartScale + (1f - BadgeStartScale) * bt,
                };
            }
            return L;
        }

        /// Open-ness after one frame: toward 1 while focused, toward 0 otherwise (linear; the layout eases it).
        public static float StepOpen(float t, bool focused, float dt)
        {
            float rate = focused ? 1f / ExpandSeconds : -1f / CollapseSeconds;
            return Clamp01(t + rate * Math.Max(0f, dt));
        }

        public struct Focus
        {
            public bool focused;
            public float dwell, away;
        }

        /// One frame of the focus. <paramref name="wantsOpen"/>: this plate is the one being looked at, steadily, up close
        /// (<see cref="WantsOpen"/>); <paramref name="keepsOpen"/>: the gaze is still on the card and you are still close
        /// (<see cref="KeepsOpen"/>). Any break resets the dwell.
        public static Focus StepFocus(Focus f, bool wantsOpen, bool keepsOpen, float dt)
        {
            dt = Math.Max(0f, dt);
            if (!f.focused)
            {
                f.dwell = wantsOpen ? f.dwell + dt : 0f;
                if (f.dwell >= FocusDwell) { f.focused = true; f.away = 0f; }
            }
            else
            {
                f.away = keepsOpen ? 0f : f.away + dt;
                if (f.away >= ReleaseDelay) { f.focused = false; f.dwell = 0f; f.away = 0f; }
            }
            return f;
        }

        /// The gaze ray's hit on the plate's plane (plate-local metres, bottom-centre origin) is on the rectangle
        /// <paramref name="w"/> × <paramref name="h"/> grown by <paramref name="margin"/>. No hit (looking away from the
        /// plane) = <paramref name="hit"/> false.
        public static bool OnPlate(bool hit, float x, float y, float w, float h, float margin) =>
            hit && Math.Abs(x) <= w * 0.5f + margin && y >= -margin && y <= h + margin;

        /// Wants to open: close enough, the gaze on the closed pill, the head steady.
        public static bool WantsOpen(float distanceM, bool onPill, float headDegPerSec) =>
            distanceM <= OpenDistance && onPill && headDegPerSec < SteadyDegPerSec;

        /// Keeps it open: still within the close distance and the gaze still on the card.
        public static bool KeepsOpen(float distanceM, bool onCard) => distanceM <= CloseDistance && onCard;

        // ── The CAMERA TAG (Nov 13 2026, live camera; Kevin's idea: "a camera name tag UI kind of thing above it
        //    in-game so people in headsets playing can look at it and see that its a camera and that UI could also
        //    inform them when its recording") ──
        // Over a live-camera headset (presence dv "camera") the nameplate becomes the camera tag: the same pill, a
        // camera mark where the picture goes, "Live camera", and a solid red LIVE / REC tab at the right end while it
        // streams / records. Larger than a player's plate (it is read across the room, often over a bright, busy
        // passthrough room), never opens into a card, never shows a picture or badges.

        /// The camera tag's world scale against a player's plate (still ONE size at every distance).
        public const float CameraScale = 1.45f;
        /// Metres above the camera's head anchor (the bottom of the plate): clears the headset and a tripod mount.
        public const float CameraHeight = 0.20f;
        /// What the tag says.
        public const string CameraLabel = "Live camera";
        /// The red tab (unscaled metres): height, the dot, the gap after it, side padding, the label's em.
        public const float TabHeight = 0.064f, TabDot = 0.024f, TabDotGap = 0.012f, TabPad = 0.022f;
        public const float TabEm = 0.046f;
        /// Gap between the name and the tab; the pill's right padding with a tab (the tab's own round end is the edge).
        public const float TabGap = 0.022f, TabPadRight = 0.030f;
        /// The REC dot blinks (Hz); LIVE's dot stays lit.
        public const float RecBlinkHz = 1f;

        /// The tab's text for a camera's presence bits (LIVE 1, REC 2), or null when it is neither (no tab).
        public static string CameraTabText(bool live, bool rec) => live && rec ? "LIVE \u00b7 REC" : live ? "LIVE" : rec ? "REC" : null;

        public struct CameraLayout
        {
            public float width, height, corner;
            public float avatarX, avatarY, nameX, nameY;
            public bool tab;
            public float tabX, tabY, tabW, tabH, dotX, textX;
        }

        /// The camera tag (unscaled metres, bottom-centre origin): <paramref name="nameWidth"/> of "Live camera",
        /// <paramref name="tabTextWidth"/> of the tab's text (0 = no tab).
        public static CameraLayout CameraLayoutFor(float nameWidth, float tabTextWidth)
        {
            bool tab = tabTextWidth > 0f;
            float h = AvatarCollapsed + 2f * Inset;
            float tabW = tab ? TabPad + TabDot + TabDotGap + tabTextWidth + TabPad : 0f;
            float w = Inset + AvatarCollapsed + Gap + Math.Max(0f, nameWidth) + (tab ? TabGap + tabW + TabPadRight : PadRight);
            float left = -w * 0.5f;
            var L = new CameraLayout
            {
                width = w, height = h, corner = h * 0.5f, tab = tab,
                avatarX = left + Inset + AvatarCollapsed * 0.5f, avatarY = h * 0.5f,
                nameX = left + Inset + AvatarCollapsed + Gap + Math.Max(0f, nameWidth) * 0.5f, nameY = h * 0.5f,
            };
            if (tab)
            {
                float tabLeft = w * 0.5f - TabPadRight - tabW;
                L.tabW = tabW; L.tabH = TabHeight;
                L.tabX = tabLeft + tabW * 0.5f; L.tabY = (h - TabHeight) * 0.5f;   // the tab's bottom-centre
                L.dotX = tabLeft + TabPad + TabDot * 0.5f;
                L.textX = tabLeft + TabPad + TabDot + TabDotGap + tabTextWidth * 0.5f;
            }
            return L;
        }

        /// The tab dot's opacity at <paramref name="time"/> s: LIVE lit, REC-only blinking (a soft square wave).
        public static float TabDotAlpha(bool live, bool rec, float time)
        {
            if (live || !rec) return 1f;
            float ph = time * RecBlinkHz;
            ph -= (float)Math.Floor(ph);
            return ph < 0.5f ? 1f : 0.25f;
        }

        /// The name as drawn: trimmed, at most <see cref="MaxNameChars"/> characters, ending in "..." when cut.
        public static string ShortName(string name)
        {
            string n = (name ?? "").Trim();
            if (n.Length <= MaxNameChars) return n;
            return n.Substring(0, MaxNameChars - 3).TrimEnd() + "...";
        }

        /// A stable hue (0..1) for a player's initial circle, from their presence id.
        public static float HueFor(string id)
        {
            uint h = 2166136261;
            string s = id ?? "";
            for (int i = 0; i < s.Length; i++) { h ^= s[i]; h *= 16777619; }
            return (h % 360u) / 360f;
        }

        // ── Badges ──

        /// One of the profile's badge rows (the SDK's ProfileBadge + the row's pinnedRank, 0 = not pinned).
        public struct BadgeRow { public string id, name, iconUrl; public int pinnedRank; }

        public const int MaxBadgeUrl = 300, MaxBadgeName = 40;

        /// The pinned rows, in pin order, at most <see cref="MaxBadges"/>. A row with no id or an over-long URL is skipped
        /// (a cut URL loads nothing); a duplicate id counts once.
        public static List<NameplateBadge> Pinned(IEnumerable<BadgeRow> rows)
        {
            var list = new List<BadgeRow>();
            if (rows != null) foreach (var r in rows) if (r.pinnedRank > 0 && !string.IsNullOrEmpty(r.id)) list.Add(r);
            list.Sort((a, b) => a.pinnedRank.CompareTo(b.pinnedRank));
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var outList = new List<NameplateBadge>();
            foreach (var r in list)
            {
                if (outList.Count >= MaxBadges) break;
                if (!seen.Add(r.id)) continue;
                string url = r.iconUrl ?? "";
                if (url.Length > MaxBadgeUrl) url = "";
                string name = r.name ?? "";
                if (name.Length > MaxBadgeName) name = name.Substring(0, MaxBadgeName);
                outList.Add(new NameplateBadge { id = r.id.Length > 80 ? r.id.Substring(0, 80) : r.id, iconUrl = url, name = name });
            }
            return outList;
        }

        /// Presence "bd": <c>[{"i":id,"u":iconUrl,"n":name}, …]</c> ("[]" = no pins; a receiver clears what it showed).
        public static string BadgesWire(IList<NameplateBadge> badges)
        {
            var sb = new StringBuilder(32);
            sb.Append('[');
            int n = 0;
            if (badges != null)
                foreach (var b in badges)
                {
                    if (b == null || string.IsNullOrEmpty(b.id) || n >= MaxBadges) continue;
                    if (n++ > 0) sb.Append(',');
                    sb.Append("{\"i\":\"").Append(Esc(b.id)).Append("\",\"u\":\"").Append(Esc(b.iconUrl)).Append("\",\"n\":\"").Append(Esc(b.name)).Append("\"}");
                }
            sb.Append(']');
            return sb.ToString();
        }

        /// The same badges as last time (a receiver only rebuilds its row on a change).
        public static string Key(IList<NameplateBadge> badges)
        {
            if (badges == null || badges.Count == 0) return "";
            var sb = new StringBuilder();
            foreach (var b in badges) sb.Append(b?.id).Append('|').Append(b?.iconUrl).Append(';');
            return sb.ToString();
        }

        static string Esc(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder(s.Length + 4);
            foreach (char c in s)
            {
                if (c == '"' || c == '\\') sb.Append('\\').Append(c);
                else if (c < 0x20) sb.Append(' ');
                else sb.Append(c);
            }
            return sb.ToString();
        }
    }
}
