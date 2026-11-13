using System.Collections.Generic;
using DreamPark.MagicUI;
using UnityEngine;
using TMPro;

namespace DreamPark
{
    /// <summary>
    /// The name floating over every remote player's head. Instant presence:
    /// the park feels multiplayer the moment two headsets share a relay, even
    /// when no game has a single multiplayer feature — you can see who
    /// somebody is before any game does anything.
    ///
    /// Created by <see cref="RemotePlayer"/> for every peer; never authored.
    /// The name is whatever the peer's ProfileAPI reports, or a stable
    /// "Guest XXXX" derived from their presence id (PlayerPresence sends it
    /// on the state cadence, so late joiners get it within a couple of
    /// seconds). Yaw-billboarded to this headset's camera, hidden while the
    /// peer has produced no pose yet.
    ///
    /// THE NAMEPLATE (Oct 28 2026; layout, focus and distance rules in <see cref="NameplateRules"/>): one slim pill in the
    /// guest-panel look (violet glass, an outline of light, brand type with a violet glow — MagicLook): their round
    /// picture on the left (<see cref="MagicAvatar"/>: the photo, or their initial on a coloured circle while it loads or
    /// when they have none), the name in the display face (Zalando Sans Expanded, the /hq and /setup font). Stand within
    /// 2 m and look right at a name, steadily, for 0.7 s and it MORPHS into a profile card like the app's (the picture
    /// travels to centred on top, the name centres under it, their pinned badges — presence "bd", ≤ 5 — rise in along the
    /// bottom); the bottom edge stays put above the head, so the card grows upward and never covers the face. It closes
    /// 0.15 s after you look away or step back. Only one plate opens at a time. ONE world size at every distance.
    /// Depth-tested like the world: never drawn over the game.
    /// Pictures: their own profile picture, or a guest's headset picture (both in presence "av"); an older peer's from
    /// the play session (<see cref="PeerAvatarProvider"/>). Images load through <see cref="MagicAvatar.Loader"/> (core: the
    /// app's disk image cache). Greppable: [Nameplate].
    ///
    /// THE CAMERA TAG (Nov 13 2026, live camera; Kevin's idea): over a live-camera headset (presence dv "camera") the
    /// plate becomes a camera tag — a camera mark where the picture goes, "Live camera", and a solid red LIVE / REC tab
    /// while it streams / records (presence "cm"). <see cref="NameplateRules.CameraScale"/> × a player's plate, it
    /// never opens into a card (layout in <see cref="NameplateRules.CameraLayoutFor"/>).
    ///
    /// A game that wants its own treatment can switch the default off with
    /// <see cref="show"/> (e.g. from core settings) — its RemoteRig can then
    /// carry whatever nameplate it likes.
    /// </summary>
    public class RemoteNameTag : MonoBehaviour
    {
        /// <summary>Park-wide switch for the default tags.</summary>
        public static bool show = true;

        public RemotePlayer peer;
        [Tooltip("Metres above the head anchor (the bottom of the plate).")]
        public float height = 0.22f;

        // Render order: glass, its rim, the picture (MagicAvatar layers on the glass), badges, then the text (3000).
        const int QPill = 2984, QRim = 2985, QAvatarBase = 2986, QBadge = 2996;
        static readonly Color GlassCentre = new Color(0.26f, 0.12f, 0.50f, 0.80f);
        static readonly Color GlassEdge = new Color(0.12f, 0.05f, 0.24f, 0.78f);
        static readonly Color RimColor = new Color(0.80f, 0.70f, 1f, 0.45f);
        const float Feather = 0.006f, RimHalf = 0.0018f;
        /// TMP 3D text: font size 10 ≈ 1 local unit of em; MagicLook labels sit at TextUnitScale.
        const float TmpEmPerPoint = 0.1f;

        Transform _plate;
        Mesh _pillMesh, _rimMesh;
        Material _pillMat, _rimMat;
        MagicAvatar _avatar;
        TextMeshPro _text;
        readonly List<Transform> _badgeT = new List<Transform>();
        readonly List<Material> _badgeMat = new List<Material>();
        string _shown, _avatarKey, _badgesKey;
        float _nameWidth;
        float _builtW = -1f, _builtH = -1f, _avatarSize = -1f;
        float _open;
        NameplateRules.Focus _focus;
        bool _wantsOpen, _keepsOpen;
        float _builtCorner = -1f;
        // The camera tag (built on first use; hidden on a player's plate).
        bool _camera;
        Transform _camGlyph, _tabT, _dotT;
        Mesh _tabMesh;
        Material _camGlyphMat, _tabMat, _dotMat;
        TextMeshPro _tabText;
        string _tabShown;
        float _tabTextWidth;
        float _camBuiltW = -1f;
        const int QTab = 2994, QCamGlyph = 2995, QTabDot = 2997;
        static readonly Color TabRed = new Color(0.90f, 0.10f, 0.12f, 1f);
        static readonly Color TabRedEdge = new Color(0.78f, 0.05f, 0.08f, 1f);
        static readonly Color CamDisc = new Color(0.13f, 0.12f, 0.16f, 1f);
        static Vector3 s_lastForward;
        static float s_lastForwardTime = -1f, s_headDegPerSec;

        // One plate opens at a time: the visible plate nearest the gaze, decided once per frame across all tags.
        static readonly List<RemoteNameTag> s_all = new List<RemoteNameTag>();
        static int s_gazeFrame = -1;

        public static RemoteNameTag Attach(RemotePlayer peer)
        {
            var go = new GameObject("NameTag");
            // Under the peer root (the park origin), not under the head:
            // the tag follows the head's position but must never inherit its
            // pitch and roll.
            go.transform.SetParent(peer.transform, false);
            var tag = go.AddComponent<RemoteNameTag>();
            tag.peer = peer;
            return tag;
        }

        void Awake()
        {
            _plate = new GameObject("Plate").transform;
            _plate.SetParent(transform, false);

            var white = Texture2D.whiteTexture;
            _pillMat = MagicLook.HoloMat(white, Color.white, false, QPill);
            _rimMat = MagicLook.HoloMat(white, Color.white, true, QRim);
            if (_pillMat != null) _pillMat.SetFloat("_VertexColor", 1f);
            if (_rimMat != null) _rimMat.SetFloat("_VertexColor", 1f);
            _pillMesh = new Mesh { name = "NameplatePill" };
            _rimMesh = new Mesh { name = "NameplateRim" };
            MagicLook.Plate(_plate, "Glass", _pillMesh, _pillMat, Vector3.zero, Quaternion.identity, Vector3.one);
            MagicLook.Plate(_plate, "Rim", _rimMesh, _rimMat, new Vector3(0f, 0f, -0.0005f), Quaternion.identity, Vector3.one);

            _avatar = new MagicAvatar(_plate, "Avatar", Vector3.zero, NameplateRules.AvatarCollapsed, QAvatarBase);
            _avatar.SetRing(MagicLook.Lilac, 0.5f);

            _text = MagicLook.Label(_plate, "Name", NameplateRules.NameEm / (MagicLook.TextUnitScale * TmpEmPerPoint), Color.white, 1.2f, display: true);
            _text.alignment = TextAlignmentOptions.Midline;   // positioned by its centre (left of the pill → centred on the card)
            _text.text = "";
            s_all.Add(this);
        }

        void OnDestroy()
        {
            s_all.Remove(this);
            if (_pillMesh != null) Destroy(_pillMesh);
            if (_rimMesh != null) Destroy(_rimMesh);
            if (_pillMat != null) Destroy(_pillMat);
            if (_rimMat != null) Destroy(_rimMat);
            if (_tabMesh != null) Destroy(_tabMesh);
            if (_tabMat != null) Destroy(_tabMat);
            if (_camGlyphMat != null) Destroy(_camGlyphMat);
            if (_dotMat != null) Destroy(_dotMat);
            foreach (var m in _badgeMat) if (m != null) Destroy(m);
        }

        void LateUpdate()
        {
            if (peer == null || peer.head == null || _plate == null) { return; }

            // Only over a pose that is a real place here (RemotePlayer.VisualsShown:
            // both frames localized and the same, or this device's diorama, after
            // its reveal hold). Unsynced, the head sits in limbo and a tag there is
            // the "missing or way off" name of the Oct 8 2026 demo.
            bool visible = show && peer.seq != 0 && peer.VisualsShown;
            if (_plate.gameObject.activeSelf != visible) _plate.gameObject.SetActive(visible);
            if (!visible) { _focus = default; _open = 0f; return; }

            bool camera = peer.IsCamera;
            if (camera != _camera) SwitchCamera(camera);
            transform.position = peer.head.position + new Vector3(0f, camera ? NameplateRules.CameraHeight : height, 0f);

            // Yaw-only billboard: readable from anywhere, never tilted. ONE world size at every distance (no scaling).
            var cam = Camera.main;
            if (cam != null)
            {
                var to = transform.position - cam.transform.position;
                to.y = 0f;
                if (to.sqrMagnitude > 0.0001f)
                    transform.rotation = Quaternion.LookRotation(to.normalized, Vector3.up);
            }
            Vector3 scale = Vector3.one * (camera ? NameplateRules.CameraScale : 1f);
            if (transform.localScale != scale) transform.localScale = scale;
            if (camera) { LateUpdateCamera(); return; }

            string name = NameplateRules.ShortName(NameFor(peer));
            if (name != _shown)
            {
                _shown = name;
                _text.text = name;
                _nameWidth = _text.GetPreferredValues(name, Mathf.Infinity, Mathf.Infinity).x * MagicLook.TextUnitScale;
            }
            string url = AvatarFor(peer);
            string key = url + "|" + name;
            if (key != _avatarKey)
            {
                _avatarKey = key;
                Color c = Color.HSVToRGB(NameplateRules.HueFor(peer.id), 0.55f, 0.72f);
                _avatar.Set(url, name, "player", "#" + ColorUtility.ToHtmlStringRGB(c));
            }
            if (peer.pinnedBadgesKey != _badgesKey) RebuildBadges();

            // Focus (deliberate): within OpenDistance, the gaze ray ON the closed pill, the head steady, for FocusDwell
            // without a break. Closes ReleaseDelay after the gaze leaves the card or you step back past CloseDistance.
            UpdateGaze(cam);
            _focus = NameplateRules.StepFocus(_focus, _wantsOpen, _keepsOpen, Time.deltaTime);
            _open = NameplateRules.StepOpen(_open, _focus.focused, Time.deltaTime);

            Apply(NameplateRules.LayoutAt(_open, _nameWidth, _badgeT.Count));
        }

        void Apply(NameplateRules.Layout L)
        {
            if (Mathf.Abs(L.width - _builtW) > 0.0005f || Mathf.Abs(L.height - _builtH) > 0.0005f || Mathf.Abs(L.corner - _builtCorner) > 0.0005f)
            {
                _builtW = L.width; _builtH = L.height; _builtCorner = L.corner;
                BuildPill(_pillMesh, _rimMesh, L.width, L.height, L.corner);
            }
            if (Mathf.Abs(L.avatar - _avatarSize) > 0.0002f)
            {
                _avatarSize = L.avatar;
                _avatar.root.localScale = Vector3.one * L.avatar;
            }
            _avatar.root.localPosition = new Vector3(L.avatarX, L.avatarY, -0.002f);
            _avatar.Tick();
            _text.rectTransform.localPosition = new Vector3(L.nameX, L.nameY, -0.003f);
            for (int i = 0; i < _badgeT.Count; i++)
            {
                var p = i < L.badges.Length ? L.badges[i] : default;
                bool on = p.alpha > 0.01f;
                if (_badgeT[i].gameObject.activeSelf != on) _badgeT[i].gameObject.SetActive(on);
                if (!on) continue;
                _badgeT[i].localPosition = new Vector3(p.x, p.y, -0.0025f);
                _badgeT[i].localScale = Vector3.one * (NameplateRules.BadgeSize * p.scale);
                if (_badgeMat[i] != null) _badgeMat[i].SetColor("_BaseColor", new Color(1f, 1f, 1f, p.alpha));
            }
        }

        void UpdateGaze(Camera cam)
        {
            if (s_gazeFrame == Time.frameCount) return;
            s_gazeFrame = Time.frameCount;
            // How fast the head is turning (a sweep across the room during play never counts as looking).
            if (cam != null)
            {
                Vector3 fwd = cam.transform.forward;
                float now = Time.time;
                if (s_lastForwardTime >= 0f && now > s_lastForwardTime)
                {
                    float rate = Vector3.Angle(s_lastForward, fwd) / (now - s_lastForwardTime);
                    s_headDegPerSec = Mathf.Lerp(s_headDegPerSec, rate, 0.5f);   // a light smoothing over frame jitter
                }
                s_lastForward = fwd; s_lastForwardTime = now;
            }
            RemoteNameTag best = null;
            float bestDist = float.MaxValue;
            foreach (var t in s_all)
            {
                if (t == null) continue;
                t._wantsOpen = false; t._keepsOpen = false;
                if (cam == null || t._plate == null || !t._plate.gameObject.activeInHierarchy) continue;
                if (t._camera) continue;   // the camera tag never opens into a card
                // The gaze ray in the plate's own space (metres, bottom-centre origin, the plate in its XY plane).
                Vector3 o = t._plate.InverseTransformPoint(cam.transform.position);
                Vector3 d = t._plate.InverseTransformDirection(cam.transform.forward);
                bool hit = d.z > 1e-4f && o.z < 0f;
                Vector3 p = hit ? o + d * (-o.z / d.z) : Vector3.zero;
                float dist = (t._plate.TransformPoint(new Vector3(0f, t._builtH * 0.5f, 0f)) - cam.transform.position).magnitude;
                NameplateRules.PillSize(t._nameWidth, out float pw, out float ph);
                bool onPill = NameplateRules.OnPlate(hit, p.x, p.y, pw, ph, NameplateRules.HitMargin);
                bool onCard = NameplateRules.OnPlate(hit, p.x, p.y, Mathf.Max(pw, t._builtW), Mathf.Max(ph, t._builtH), NameplateRules.KeepMargin);
                t._keepsOpen = NameplateRules.KeepsOpen(dist, onCard);
                if (NameplateRules.WantsOpen(dist, onPill, s_headDegPerSec) && dist < bestDist) { bestDist = dist; best = t; }
            }
            if (best == null) return;
            best._wantsOpen = true;
            // Looking at another name closes the one that was open (one plate at a time).
            foreach (var t in s_all) if (t != null && t != best) t._keepsOpen = false;
        }

        // ── The camera tag (Nov 13 2026) ─────────────────────────────

        /// A peer became (or stopped being) a live camera: swap the plate's contents.
        void SwitchCamera(bool on)
        {
            _camera = on;
            _focus = default; _open = 0f;
            _shown = null; _avatarKey = null; _badgesKey = null;
            _builtW = _builtH = _builtCorner = -1f; _avatarSize = -1f; _camBuiltW = -1f; _tabShown = null;
            if (on) EnsureCameraParts();
            else _avatar.SetRing(MagicLook.Lilac, 0.5f);
            if (_camGlyph != null) _camGlyph.gameObject.SetActive(on);
            if (_tabT != null) _tabT.gameObject.SetActive(false);
            foreach (var t in _badgeT) if (t != null) t.gameObject.SetActive(false);
            Debug.Log("[Nameplate] " + (peer != null ? peer.id : "?") + (on ? " is a live camera: camera tag" : " is a player again: nameplate"));
        }

        void EnsureCameraParts()
        {
            if (_camGlyph != null) return;
            _camGlyphMat = MagicLook.HoloMat(CameraGlyphTexture(), Color.white, false, QCamGlyph);
            _camGlyph = MagicLook.Plate(_plate, "CameraMark", MagicLook.Quad(), _camGlyphMat, Vector3.zero, Quaternion.identity, Vector3.one).transform;

            _tabT = new GameObject("LiveTab").transform;
            _tabT.SetParent(_plate, false);
            _tabMat = MagicLook.HoloMat(Texture2D.whiteTexture, Color.white, false, QTab);
            if (_tabMat != null) _tabMat.SetFloat("_VertexColor", 1f);
            _tabMesh = new Mesh { name = "CameraTab" };
            MagicLook.Plate(_tabT, "Glass", _tabMesh, _tabMat, Vector3.zero, Quaternion.identity, Vector3.one);
            _dotMat = MagicLook.HoloMat(MagicLook.DiscTexture(), Color.white, false, QTabDot);
            _dotT = MagicLook.Plate(_tabT, "Dot", MagicLook.Quad(), _dotMat, Vector3.zero, Quaternion.identity, Vector3.one * NameplateRules.TabDot).transform;
            _tabText = MagicLook.Label(_tabT, "Label", NameplateRules.TabEm / (MagicLook.TextUnitScale * TmpEmPerPoint), Color.white, 0.6f, display: true);
            _tabText.alignment = TextAlignmentOptions.Midline;
            _tabText.text = "";
            _tabT.gameObject.SetActive(false);
        }

        void LateUpdateCamera()
        {
            string label = NameplateRules.CameraLabel;
            if (label != _shown)
            {
                _shown = label;
                _text.text = label;
                _nameWidth = _text.GetPreferredValues(label, Mathf.Infinity, Mathf.Infinity).x * MagicLook.TextUnitScale;
            }
            if (_avatarKey != "camera")
            {
                _avatarKey = "camera";
                _avatar.Set("", "", "player", "#" + ColorUtility.ToHtmlStringRGB(CamDisc));
                _avatar.SetRing(Color.white, 0.55f);
            }
            bool live = peer.CameraLive, rec = peer.CameraRecording;
            string tab = NameplateRules.CameraTabText(live, rec);
            if (tab != _tabShown)
            {
                _tabShown = tab;
                _tabText.text = tab ?? "";
                _tabTextWidth = tab == null ? 0f : _tabText.GetPreferredValues(tab, Mathf.Infinity, Mathf.Infinity).x * MagicLook.TextUnitScale;
            }
            var L = NameplateRules.CameraLayoutFor(_nameWidth, _tabTextWidth);
            if (Mathf.Abs(L.width - _builtW) > 0.0005f || Mathf.Abs(L.height - _builtH) > 0.0005f || Mathf.Abs(L.corner - _builtCorner) > 0.0005f)
            {
                _builtW = L.width; _builtH = L.height; _builtCorner = L.corner;
                BuildPill(_pillMesh, _rimMesh, L.width, L.height, L.corner);
            }
            if (Mathf.Abs(NameplateRules.AvatarCollapsed - _avatarSize) > 0.0002f)
            {
                _avatarSize = NameplateRules.AvatarCollapsed;
                _avatar.root.localScale = Vector3.one * NameplateRules.AvatarCollapsed;
            }
            _avatar.root.localPosition = new Vector3(L.avatarX, L.avatarY, -0.002f);
            _avatar.Tick();
            if (_camGlyph != null)
            {
                _camGlyph.localPosition = new Vector3(L.avatarX, L.avatarY, -0.0035f);
                _camGlyph.localScale = Vector3.one * (NameplateRules.AvatarCollapsed * 0.66f);
            }
            _text.rectTransform.localPosition = new Vector3(L.nameX, L.nameY, -0.003f);

            if (_tabT.gameObject.activeSelf != L.tab) _tabT.gameObject.SetActive(L.tab);
            if (!L.tab) return;
            if (Mathf.Abs(L.tabW - _camBuiltW) > 0.0005f)
            {
                _camBuiltW = L.tabW;
                BuildPill(_tabMesh, null, L.tabW, L.tabH, L.tabH * 0.5f, TabRed, TabRedEdge, default);
            }
            _tabT.localPosition = new Vector3(L.tabX, L.tabY, -0.0042f);
            _dotT.localPosition = new Vector3(L.dotX - L.tabX, L.tabH * 0.5f, -0.0005f);
            _tabText.rectTransform.localPosition = new Vector3(L.textX - L.tabX, L.tabH * 0.5f, -0.001f);
            if (_dotMat != null) _dotMat.SetColor("_BaseColor", new Color(1f, 1f, 1f, NameplateRules.TabDotAlpha(live, rec, Time.time)));
        }

        /// A video-camera mark (body + lens wedge), white on transparent, tinted by the material.
        static Texture2D CameraGlyphTexture() => MagicLook.Tex("NameplateCamera", 128, 128, (u, v) =>
        {
            const float aa = 1.5f / 128f;
            // Body: a rounded box u 0.10..0.64, v 0.27..0.73.
            float bx = Mathf.Max(0f, Mathf.Abs(u - 0.37f) - (0.27f - 0.08f)), by = Mathf.Max(0f, Mathf.Abs(v - 0.5f) - (0.23f - 0.08f));
            float body = Mathf.Sqrt(bx * bx + by * by) - 0.08f;
            // Lens wedge: u 0.68..0.92, half-height 0.08 → 0.21.
            float k = Mathf.Clamp01((u - 0.68f) / 0.24f);
            float half = Mathf.Lerp(0.08f, 0.21f, k);
            float wedge = Mathf.Max(Mathf.Max(0.68f - u, u - 0.92f), Mathf.Abs(v - 0.5f) - half);
            float d = Mathf.Min(body, wedge);
            return 1f - Mathf.SmoothStep(-aa, aa, d);
        });

        void RebuildBadges()
        {
            _badgesKey = peer.pinnedBadgesKey;
            foreach (var t in _badgeT) if (t != null) Destroy(t.gameObject);
            foreach (var m in _badgeMat) if (m != null) Destroy(m);
            _badgeT.Clear(); _badgeMat.Clear();
            int n = Mathf.Min(NameplateRules.MaxBadges, peer.pinnedBadges.Count);
            for (int i = 0; i < n; i++)
            {
                var b = peer.pinnedBadges[i];
                // Their art when it arrives; the rosette (the fallback badge art) until then, or when it never loads.
                Material m = MagicLook.HoloMat(MagicLook.RosetteTexture(), new Color(1f, 1f, 1f, 0f), false, QBadge);
                var t = MagicLook.Plate(_plate, "Badge " + b.id, MagicLook.Quad(), m, Vector3.zero, Quaternion.identity, Vector3.one * NameplateRules.BadgeSize).transform;
                t.gameObject.SetActive(false);
                _badgeT.Add(t); _badgeMat.Add(m);
                if (string.IsNullOrEmpty(b.iconUrl) || MagicAvatar.Loader == null || m == null) continue;
                Material want = m;
                try
                {
                    MagicAvatar.Loader(b.iconUrl, tex =>
                    {
                        if (tex == null || want == null || !_badgeMat.Contains(want)) return;
                        want.SetTexture("_BaseMap", tex);
                    });
                }
                catch (System.Exception e) { Debug.LogWarning("[Nameplate] badge art " + b.iconUrl + ": " + e.Message); }
            }
        }

        /// The glass (a rounded rectangle: the pill when the corner is half the height, the card when it is CardCorner; a
        /// radial fan from its heart so the middle is a little lighter, with a feathered edge) and its rim of light, in the
        /// plate's local XY plane, bottom-centre at the origin.
        static void BuildPill(Mesh pill, Mesh rim, float w, float h, float corner)
            => BuildPill(pill, rim, w, h, corner, GlassCentre, GlassEdge, RimColor);

        /// <paramref name="rim"/> null = no rim (the camera tag's red tab).
        static void BuildPill(Mesh pill, Mesh rim, float w, float h, float corner, Color GlassCentre, Color GlassEdge, Color RimColor)
        {
            const int Seg = 8;   // per corner
            float r = Mathf.Clamp(corner, 0.001f, 0.5f * Mathf.Min(w, h));
            float hx = Mathf.Max(0f, w * 0.5f - r), y0 = r, y1 = Mathf.Max(r, h - r);
            var outline = new List<Vector2>(4 * (Seg + 1));
            void Arc(float cx, float cy, float fromDeg)
            {
                for (int i = 0; i <= Seg; i++)
                {
                    float a = (fromDeg + 90f * i / Seg) * Mathf.Deg2Rad;
                    outline.Add(new Vector2(cx + r * Mathf.Cos(a), cy + r * Mathf.Sin(a)));
                }
            }
            Arc(hx, y0, -90f); Arc(hx, y1, 0f); Arc(-hx, y1, 90f); Arc(-hx, y0, 180f);
            int n = outline.Count;
            // Vertex colours are not converted to linear the way material colours are: convert them here, or the glass
            // comes out pale lavender in a linear-space project (Oct 28 2026, the first in-Unity capture).
            bool lin = QualitySettings.activeColorSpace == ColorSpace.Linear;
            Color glassCentre = lin ? GlassCentre.linear : GlassCentre, glassEdge = lin ? GlassEdge.linear : GlassEdge;
            Color rimColor = lin ? RimColor.linear : RimColor;
            Vector3 Out(Vector2 p, float by)
            {
                Vector2 c = new Vector2(Mathf.Clamp(p.x, -hx, hx), Mathf.Clamp(p.y, y0, y1));
                Vector2 d = p - c;
                return d.sqrMagnitude > 1e-8f ? p + d.normalized * by : p;
            }

            // Glass: the heart + the inner outline (a fan) + the outer feather ring (alpha 0).
            var v = new List<Vector3>(1 + 2 * n);
            var col = new List<Color>(1 + 2 * n);
            v.Add(new Vector3(0f, h * 0.5f, 0f)); col.Add(glassCentre);
            Color clear = new Color(glassEdge.r, glassEdge.g, glassEdge.b, 0f);
            for (int i = 0; i < n; i++) { v.Add(Out(outline[i], -Feather * 0.5f)); col.Add(glassEdge); }
            for (int i = 0; i < n; i++) { v.Add(Out(outline[i], Feather * 0.5f)); col.Add(clear); }
            var tri = new List<int>(n * 9);
            for (int i = 0; i < n; i++)
            {
                int a = 1 + i, b = 1 + (i + 1) % n, oa = 1 + n + i, ob = 1 + n + (i + 1) % n;
                tri.Add(0); tri.Add(b); tri.Add(a);
                tri.Add(a); tri.Add(b); tri.Add(ob);
                tri.Add(a); tri.Add(ob); tri.Add(oa);
            }
            Fill(pill, v, col, tri);
            if (rim == null) return;

            // Rim: three rings (inside, the line, outside), the light only on the middle one.
            v = new List<Vector3>(3 * n); col = new List<Color>(3 * n);
            Color rimClear = new Color(rimColor.r, rimColor.g, rimColor.b, 0f);
            for (int i = 0; i < n; i++) { v.Add(Out(outline[i], -Feather * 0.5f - RimHalf)); col.Add(rimClear); }
            for (int i = 0; i < n; i++) { v.Add(Out(outline[i], -Feather * 0.5f)); col.Add(rimColor); }
            for (int i = 0; i < n; i++) { v.Add(Out(outline[i], -Feather * 0.5f + RimHalf)); col.Add(rimClear); }
            tri = new List<int>(n * 12);
            for (int ring = 0; ring < 2; ring++)
                for (int i = 0; i < n; i++)
                {
                    int a = ring * n + i, b = ring * n + (i + 1) % n, oa = a + n, ob = b + n;
                    tri.Add(a); tri.Add(b); tri.Add(ob);
                    tri.Add(a); tri.Add(ob); tri.Add(oa);
                }
            Fill(rim, v, col, tri);
        }

        static void Fill(Mesh m, List<Vector3> v, List<Color> col, List<int> tri)
        {
            m.Clear();
            m.SetVertices(v);
            m.SetColors(col);
            var uv = new List<Vector2>(v.Count);
            for (int i = 0; i < v.Count; i++) uv.Add(new Vector2(0.5f, 0.5f));
            m.SetUVs(0, uv);
            m.SetTriangles(tri, 0);
            m.RecalculateBounds();
        }

        /// <summary>
        /// Core: a peer's name from the play session (web-setup headset names, the server's `names: {peerId: name}`), or
        /// null. Used only when the peer itself sends no real name (an older app still sending "Guest XXXX").
        /// </summary>
        [XLua.BlackList] public static System.Func<string, string> PeerNameProvider;

        /// <summary>
        /// Core: a peer's picture from the play session (the server's `avatars: {peerId: url}`), or null. Used only when the
        /// peer itself sends no picture (an older app's guest).
        /// </summary>
        [XLua.BlackList] public static System.Func<string, string> PeerAvatarProvider;

        /// <summary>What a remote player is called: their own name, else the play session's name for their headset, else
        /// "Guest XXXX".</summary>
        public static string NameFor(RemotePlayer peer)
        {
            if (peer == null) return FallbackName(null);
            string own = peer.displayName;
            if (!string.IsNullOrEmpty(own) && own != FallbackName(peer.id)) return own;
            string fromSession = null;
            try { fromSession = PeerNameProvider?.Invoke(peer.id); } catch (System.Exception) { fromSession = null; }
            if (!string.IsNullOrEmpty(fromSession)) return fromSession;
            return string.IsNullOrEmpty(own) ? FallbackName(peer.id) : own;
        }

        /// <summary>A remote player's picture: their own (presence "av": the profile's, or a guest's headset picture), else
        /// the play session's picture for their headset, else "" (their initial on a coloured circle).</summary>
        [XLua.BlackList]
        public static string AvatarFor(RemotePlayer peer)
        {
            if (peer == null) return "";
            if (!string.IsNullOrEmpty(peer.avatarUrl)) return peer.avatarUrl;
            if (!NameplateRules.SessionPictureApplies(peer.displayName, FallbackName(peer.id))) return "";
            string fromSession = null;
            try { fromSession = PeerAvatarProvider?.Invoke(peer.id); } catch (System.Exception) { fromSession = null; }
            return fromSession ?? "";
        }

        /// <summary>"Guest 3F2A" — stable per headset, derived from the presence id.</summary>
        public static string FallbackName(string peerId)
        {
            if (string.IsNullOrEmpty(peerId)) return "Guest";
            var tail = peerId.Length <= 4 ? peerId : peerId.Substring(peerId.Length - 4);
            return "Guest " + tail.ToUpperInvariant();
        }
    }
}
