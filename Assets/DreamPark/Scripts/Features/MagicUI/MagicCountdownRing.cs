using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;

namespace DreamPark.MagicUI
{
    /// <summary>
    /// A VISIBLE countdown: a ring of dots of light that go out one by one (clockwise from 12), the seconds in its
    /// middle. Used by the personal results card and the "NEXT UP" card. Call <see cref="Set"/> each frame.
    /// </summary>
    public sealed class MagicCountdownRing
    {
        public readonly Transform root;
        readonly List<Material> _dots = new List<Material>();
        readonly TextMeshPro _num;
        int _lit = -1;
        readonly int _count;

        public MagicCountdownRing(Transform parent, Vector3 at, float radius = 0.11f, int dots = 30, int queue = MagicLook.QSpark)
        {
            _count = dots;
            root = new GameObject("Countdown").transform;
            root.SetParent(parent, false);
            root.localPosition = at;
            Mesh quad = MagicLook.Quad();
            for (int i = 0; i < dots; i++)
            {
                float a = Mathf.PI * 0.5f - i * Mathf.PI * 2f / dots;
                Material m = MagicLook.HoloMat(MagicLook.GlowDiscTexture(), MagicLook.VioletBright, true, queue);
                MagicLook.Plate(root, "Dot" + i, quad, m, new Vector3(Mathf.Cos(a), Mathf.Sin(a), 0) * radius, Quaternion.identity, Vector3.one * radius * 0.31f);
                _dots.Add(m);
            }
            _num = MagicLook.Label(root, "Seconds", 70f * radius / 0.11f, Color.white, radius * 1.8f, true);
            _num.transform.localPosition = new Vector3(0, -radius * 0.11f, 0);
        }

        /// Dots still lit out of <paramref name="dots"/> (pure): the last one stays until the very end.
        public static int Lit(double now, double since, double endsAt, int dots)
        {
            if (endsAt <= since || dots <= 0) return 0;
            double left = Math.Max(0.0, endsAt - now), total = endsAt - since;
            return Math.Min(dots, (int)Math.Ceiling(dots * left / total));
        }

        public bool Active { get => root.gameObject.activeSelf; set { if (root.gameObject.activeSelf != value) root.gameObject.SetActive(value); } }

        public void Set(double now, double since, double endsAt)
        {
            bool show = endsAt > since && now < endsAt;
            Active = show;
            if (!show) return;
            int lit = Lit(now, since, endsAt, _count);
            string secs = Mathf.CeilToInt((float)(endsAt - now)).ToString();
            if (_num.text != secs) _num.text = secs;
            if (lit == _lit) return;
            _lit = lit;
            for (int i = 0; i < _dots.Count; i++)
                _dots[i].SetColor("_BaseColor", i < lit ? MagicLook.VioletBright : new Color(MagicLook.Lilac.r, MagicLook.Lilac.g, MagicLook.Lilac.b, 0.12f));
        }
    }
}
