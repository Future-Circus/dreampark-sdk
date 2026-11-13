using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;

namespace DreamPark.MagicUI
{
    /// <summary>
    /// A player's face in light (Oct 18 2026, Aidan: "show the profile picture of each person next to their
    /// username"): the profile photo cut to a circle with a soft rim ring, a faint glow behind.
    ///   • a player with a photo: the photo (centre-cropped, circular, feathered edge);
    ///   • a guest / unbound player (no photo): the initial letter on a brand-violet disc;
    ///   • a CPU entrant: its livery colour disc with a wheel glyph (or its own avatar image when it has one).
    /// The ring carries meaning: READY lights it, the podium gives it gold / silver / bronze, the viewer's own row
    /// brightens it. Drawn on the board's glass layers, so the board's x-ray (readability fallback) covers it too.
    /// Photos load through <see cref="Loader"/> (core sets it to its image cache); none set → the initial.
    /// </summary>
    public sealed class MagicAvatar
    {
        /// url → texture (callback null on failure). Core: HoloImageCache (relative URLs made absolute).
        public static Action<string, Action<Texture2D>> Loader;

        public readonly Transform root;
        readonly Material _glow, _disc, _photo, _ring, _wheel;
        readonly Transform _photoT, _wheelT;
        readonly TextMeshPro _initial;
        string _key, _url;
        Color _ringColor = MagicLook.Lilac;
        float _ringK = 0.6f;

        static readonly Dictionary<string, Texture2D> s_round = new Dictionary<string, Texture2D>();

        /// <paramref name="queueBase"/>: the layer it draws in (a board's glass by default; a badge sticker passes its
        /// own so the face sits on top of the art).
        public MagicAvatar(Transform parent, string name, Vector3 at, float size, int queueBase = MagicLook.QGlass)
        {
            root = new GameObject(name).transform;
            root.SetParent(parent, false);
            root.localPosition = at;
            root.localScale = Vector3.one * size;
            Mesh q = MagicLook.Quad();
            if (MagicLook.Holo != null)
            {
                _glow = MagicLook.HoloMat(MagicLook.GlowDiscTexture(), new Color(MagicLook.Violet.r, MagicLook.Violet.g, MagicLook.Violet.b, 0.35f), true, queueBase + 3);
                MagicLook.Plate(root, "Glow", q, _glow, new Vector3(0, 0, 0.002f), Quaternion.identity, Vector3.one * 1.7f);
                _disc = MagicLook.HoloMat(DiscTexture(), MagicLook.Violet, false, queueBase + 4);
                MagicLook.Plate(root, "Disc", q, _disc, new Vector3(0, 0, 0.001f), Quaternion.identity, Vector3.one);
                _photo = MagicLook.HoloMat(DiscTexture(), Color.white, false, queueBase + 5);
                _photoT = MagicLook.Plate(root, "Photo", q, _photo, new Vector3(0, 0, 0.0005f), Quaternion.identity, Vector3.one * 0.96f).transform;
                _photoT.gameObject.SetActive(false);
                _wheel = MagicLook.HoloMat(WheelTexture(), new Color(1f, 1f, 1f, 0.92f), false, queueBase + 5);
                _wheelT = MagicLook.Plate(root, "Wheel", q, _wheel, new Vector3(0, 0, 0.0005f), Quaternion.identity, Vector3.one * 0.62f).transform;
                _wheelT.gameObject.SetActive(false);
                _ring = MagicLook.HoloMat(RingTexture(), MagicLook.Lilac, true, queueBase + 6);
                MagicLook.Plate(root, "Ring", q, _ring, Vector3.zero, Quaternion.identity, Vector3.one * 1.18f);
            }
            // The initial: a label sized for a 1 m avatar, scaled with the root.
            _initial = MagicLook.Label(root, "Initial", 520f, Color.white, 1f, display: true);
            _initial.transform.localScale = Vector3.one * MagicLook.TextUnitScale;
            _initial.transform.localPosition = new Vector3(0, -0.02f, -0.001f);
            if (queueBase + 7 > 3000) _initial.fontMaterial.renderQueue = queueBase + 7;   // over its own disc
        }

        public bool Active { get => root.gameObject.activeSelf; set { if (root.gameObject.activeSelf != value) root.gameObject.SetActive(value); } }

        /// Who it is: their photo URL (or null/""), display name (for the initial), "cpu" / "player", livery colour.
        /// <paramref name="id"/>: a CPU's id → THIS device's portrait from the title's declared cast (CpuRoster) wins over
        /// the row's URL (a file:// URL baked on another device never loads here).
        public void Set(string url, string name, string kind, string color, string id = null)
        {
            Texture2D local = null;
            if (kind == "cpu" && !string.IsNullOrEmpty(id) && CpuRoster.TryTexture(id, out local) && local != null) url = "cpu-portrait:" + id;
            string key = url + "|" + name + "|" + kind + "|" + color;
            if (key == _key) return;
            _key = key;
            bool cpu = kind == "cpu";
            Color disc = ColorUtility.TryParseHtmlString(color ?? "", out Color c) ? c : cpu ? new Color(0.45f, 0.47f, 0.55f) : MagicLook.Violet;
            if (_disc != null) _disc.SetColor("_BaseColor", new Color(disc.r, disc.g, disc.b, 1f));
            string n = (name ?? "").Trim();
            _initial.text = cpu || n.Length == 0 ? "" : char.ToUpperInvariant(n[0]).ToString();
            if (_wheelT != null) _wheelT.gameObject.SetActive(cpu && string.IsNullOrEmpty(url));
            if (_photoT != null) _photoT.gameObject.SetActive(false);
            _url = url;
            if (local != null && _photo != null)
            {
                if (!s_round.TryGetValue(url, out Texture2D lr) || lr == null) s_round[url] = lr = Round(local);
                if (lr != null) { ShowPhoto(lr); return; }
            }
            if (string.IsNullOrEmpty(url) || Loader == null || _photo == null) return;
            if (s_round.TryGetValue(url, out Texture2D ready) && ready != null) { ShowPhoto(ready); return; }
            string want = url;
            try
            {
                Loader(url, tex =>
                {
                    if (tex == null || _photo == null || _url != want) return;
                    if (!s_round.TryGetValue(want, out Texture2D round) || round == null) s_round[want] = round = Round(tex);
                    if (round != null) ShowPhoto(round);
                });
            }
            catch (Exception) { }
        }

        void ShowPhoto(Texture2D t)
        {
            _photo.SetTexture("_BaseMap", t);
            _photo.SetColor("_BaseColor", Color.white);
            _photoT.gameObject.SetActive(true);
            _initial.text = "";
            if (_wheelT != null) _wheelT.gameObject.SetActive(false);
        }

        /// The ring: a colour and a strength (0 dim … 1 lit). READY = VioletBright 1; podium = medal colours.
        public void SetRing(Color c, float k) { _ringColor = c; _ringK = Mathf.Clamp01(k); }

        /// Per frame (optional): a lit ring breathes.
        public void Tick(float pulse = 1f)
        {
            if (_ring == null) return;
            Color c = _ringColor; c.a = (0.25f + 0.75f * _ringK) * pulse;
            _ring.SetColor("_BaseColor", c);
            if (_glow != null) { Color g = _ringColor; g.a = 0.1f + 0.35f * _ringK * pulse; _glow.SetColor("_BaseColor", g); }
        }

        /// The photo, centre-cropped to a square, cut to a circle with a feathered edge (any source texture:
        /// a GPU blit, so non-readable textures work).
        public static Texture2D Round(Texture2D src, int size = 128)
        {
            if (src == null) return null;
            RenderTexture rt = RenderTexture.GetTemporary(size, size, 0, RenderTextureFormat.ARGB32);
            RenderTexture prev = RenderTexture.active;
            try
            {
                float w = src.width, h = src.height, m = Mathf.Min(w, h);
                var scale = new Vector2(m / w, m / h);
                var offset = new Vector2((1f - scale.x) * 0.5f, (1f - scale.y) * 0.5f);
                Graphics.Blit(src, rt, scale, offset);
                RenderTexture.active = rt;
                var t = new Texture2D(size, size, TextureFormat.RGBA32, true) { wrapMode = TextureWrapMode.Clamp, name = "AvatarRound" };
                t.ReadPixels(new Rect(0, 0, size, size), 0, 0);
                Color32[] px = t.GetPixels32();
                float r0 = size * 0.5f;
                for (int y = 0; y < size; y++)
                    for (int x = 0; x < size; x++)
                    {
                        float d = new Vector2(x + 0.5f - r0, y + 0.5f - r0).magnitude / r0;
                        float a = Mathf.Clamp01((1f - d) / 0.04f);
                        int i = y * size + x;
                        px[i].a = (byte)(a * 255f);
                    }
                t.SetPixels32(px);
                t.Apply(true, false);
                return t;
            }
            catch (Exception) { return null; }
            finally { RenderTexture.active = prev; RenderTexture.ReleaseTemporary(rt); }
        }

        static Texture2D DiscTexture() => MagicLook.Tex("AvatarDisc", 128, 128, (u, v) =>
        {
            float d = new Vector2(u - 0.5f, v - 0.5f).magnitude * 2f;
            return Mathf.Clamp01((1f - d) / 0.04f);
        });

        static Texture2D RingTexture() => MagicLook.Tex("AvatarRing", 128, 128, (u, v) =>
        {
            float d = new Vector2(u - 0.5f, v - 0.5f).magnitude * 2f * 1.18f;   // the ring sits on the photo's edge
            float e = d - 1f;
            return Mathf.Exp(-e * e / (0.035f * 0.035f)) + Mathf.Exp(-Mathf.Abs(e) / 0.06f) * 0.25f * (d < 1.4f ? 1f : 0f);
        });

        /// A CPU's glyph: a steering wheel (rim, hub, three spokes).
        static Texture2D WheelTexture() => MagicLook.Tex("AvatarWheel", 128, 128, (u, v) =>
        {
            Vector2 p = new Vector2(u - 0.5f, v - 0.5f) * 2f;
            float d = p.magnitude;
            float rim = Mathf.Clamp01(1f - Mathf.Abs(d - 0.8f) / 0.11f);
            float hub = Mathf.Clamp01((0.24f - d) / 0.04f);
            float spoke = 0f;
            if (d < 0.8f)
                for (int k = 0; k < 3; k++)
                {
                    float a = Mathf.PI * 0.5f + k * Mathf.PI * 2f / 3f + Mathf.PI;   // down + the two sides
                    var dir = new Vector2(Mathf.Cos(a), Mathf.Sin(a));
                    float along = Vector2.Dot(p, dir);
                    float across = Mathf.Abs(dir.x * p.y - dir.y * p.x);
                    if (along > 0f) spoke = Mathf.Max(spoke, Mathf.Clamp01(1f - across / 0.09f));
                }
            return Mathf.Clamp01(Mathf.Max(rim, Mathf.Max(hub, spoke)) * 1.4f);
        });
    }
}
