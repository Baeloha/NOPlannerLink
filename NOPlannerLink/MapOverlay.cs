using System;
using System.Collections.Generic;
using NOPlannerLink.Core;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace NOPlannerLink
{
    /// <summary>
    /// Draws the planner's routes, notes and shapes on the local in-game map (DynamicMap), like the game's own
    /// unit-order waypoints: plain UI images under the map's icon layer, so they pan, zoom and rotate with the map
    /// and sit under the unit icons. Only this player's map; nothing is networked.
    /// </summary>
    internal sealed class MapOverlay
    {
        private struct Seg { public RectTransform Rt; public Vector2 A, B; public float Px; }
        private struct Dot { public RectTransform Rt; public Vector2 P; public float Px; }
        private struct Label { public RectTransform Rt; public Vector2 P; public float DyPx; }

        private OverlaySet set = new OverlaySet();
        private bool dirty;
        private GameObject? root;
        private DynamicMap? builtFor;
        private float lastInv = -1, lastFactor = -1;
        private readonly List<Seg> segs = new List<Seg>();
        private readonly List<Dot> dots = new List<Dot>();
        private readonly List<Label> labels = new List<Label>();
        private TMP_FontAsset? font;

        public bool Visible = true;

        /// <summary>Replace what is drawn (empty set = clear). Applied on the next frame.</summary>
        public void Set(OverlaySet s)
        {
            set = s;
            dirty = true;
        }

        public static bool MapAvailable => SceneSingleton<DynamicMap>.i != null && SceneSingleton<DynamicMap>.i.iconLayer != null;

        /// <summary>Every frame on the game thread.</summary>
        public void Tick()
        {
            var map = SceneSingleton<DynamicMap>.i;
            if (map == null || map.iconLayer == null || map.mapImage == null)
            {
                root = null; // destroyed with the map's scene
                builtFor = null;
                return;
            }
            if (dirty || root == null || !ReferenceEquals(builtFor, map)) Build(map);
            if (root == null) return;
            if (root.activeSelf != Visible) root.SetActive(Visible);
            if (!Visible) return;
            Layout(map);
        }

        private void Build(DynamicMap map)
        {
            dirty = false;
            Clear();
            builtFor = map;
            if (set.Count == 0) return;
            if (font == null)
            {
                var existing = map.GetComponentInChildren<TMP_Text>(true);
                font = existing != null ? existing.font : TMP_Settings.defaultFontAsset;
            }
            root = new GameObject("NOPlannerOverlay", typeof(RectTransform));
            root.transform.SetParent(map.iconLayer.transform, false);
            root.transform.SetAsFirstSibling(); // under the unit icons
            var rrt = (RectTransform)root.transform;
            rrt.localPosition = Vector3.zero;
            rrt.localRotation = Quaternion.identity;
            rrt.localScale = Vector3.one;

            foreach (var l in set.Lines)
            {
                var color = new Color(l.R, l.G, l.B, 0.95f);
                int n = l.Pts.Length / 2;
                var pts = new Vector2[n];
                for (int i = 0; i < n; i++) pts[i] = new Vector2(l.Pts[2 * i], l.Pts[2 * i + 1]);
                // Dark casing under the coloured line so it reads on any terrain.
                for (int pass = 0; pass < 2; pass++)
                {
                    var c = pass == 0 ? new Color(0, 0, 0, 0.55f) : color;
                    var w = pass == 0 ? l.Width + 2 : l.Width;
                    for (int i = 0; i + 1 < n; i++) AddSeg(pts[i], pts[i + 1], w, c);
                    if (l.Closed && n > 2) AddSeg(pts[n - 1], pts[0], w, c);
                    if (l.Arrow && n >= 2)
                    {
                        // Arrow head: two short strokes at the last point, 14 px long on screen (scaled in Layout).
                        AddSeg(pts[n - 1], pts[n - 2], -w, c);
                        AddSeg(pts[n - 1], pts[n - 2], -w - 1000, c);
                    }
                }
                if (l.Dots)
                    for (int i = 0; i < n; i++)
                    {
                        AddDot(pts[i], l.Width * 3 + 2, new Color(0, 0, 0, 0.7f));
                        AddDot(pts[i], l.Width * 3, color);
                    }
                if (l.PointLabels != null)
                    for (int i = 0; i < n && i < l.PointLabels.Length; i++)
                        if (!string.IsNullOrEmpty(l.PointLabels[i])) AddLabel(l.PointLabels[i], pts[i], color, 11, 12);
            }
            foreach (var t in set.Labels) AddLabel(t.Text, new Vector2(t.X, t.Z), new Color(t.R, t.G, t.B, 1), 13, 0);
            lastInv = lastFactor = -1;
        }

        private void Clear()
        {
            if (root != null) UnityEngine.Object.Destroy(root);
            root = null;
            segs.Clear();
            dots.Clear();
            labels.Clear();
        }

        private Image NewImage(string name, Color c)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Image));
            go.transform.SetParent(root!.transform, false);
            var img = go.GetComponent<Image>();
            img.color = c;
            img.raycastTarget = false; // never block clicks on the map
            return img;
        }

        /// <summary>Px &lt; 0 marks an arrow stroke (−width, and −width−1000 for the second side).</summary>
        private void AddSeg(Vector2 a, Vector2 b, float px, Color c)
        {
            var rt = NewImage("seg", c).rectTransform;
            rt.pivot = new Vector2(0.5f, 0f);
            segs.Add(new Seg { Rt = rt, A = a, B = b, Px = px });
        }

        private void AddDot(Vector2 p, float px, Color c)
        {
            var rt = NewImage("dot", c).rectTransform;
            dots.Add(new Dot { Rt = rt, P = p, Px = px });
        }

        private void AddLabel(string text, Vector2 p, Color c, float size, float dyPx)
        {
            var go = new GameObject("label", typeof(RectTransform));
            go.transform.SetParent(root!.transform, false);
            var rt = (RectTransform)go.transform;
            var bg = new GameObject("bg", typeof(RectTransform), typeof(Image));
            bg.transform.SetParent(go.transform, false);
            var bgImg = bg.GetComponent<Image>();
            bgImg.color = new Color(0.04f, 0.06f, 0.08f, 0.75f);
            bgImg.raycastTarget = false;
            var tgo = new GameObject("text", typeof(RectTransform));
            tgo.transform.SetParent(go.transform, false);
            var tmp = tgo.AddComponent<TextMeshProUGUI>();
            if (font != null) tmp.font = font;
            tmp.text = text;
            tmp.fontSize = size;
            tmp.color = Color.Lerp(c, Color.white, 0.35f);
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.enableWordWrapping = false;
            tmp.raycastTarget = false;
            var pref = tmp.GetPreferredValues(text);
            var w = pref.x + 8;
            var h = Mathf.Max(pref.y, size) + 2;
            ((RectTransform)tgo.transform).sizeDelta = new Vector2(w, h);
            var brt = (RectTransform)bg.transform;
            brt.sizeDelta = new Vector2(w, h);
            labels.Add(new Label { Rt = rt, P = p, DyPx = dyPx });
        }

        private void Layout(DynamicMap map)
        {
            // Map units: local = world metres × mapDisplayFactor (x → x, z → y), as the game's radar lines.
            // inv keeps widths constant on screen while zooming, as the game's own waypoint markers do.
            float f = map.mapDisplayFactor;
            float inv = 1f / Mathf.Max(1e-4f, map.mapImage.transform.localScale.x);
            if (!Mathf.Approximately(inv, lastInv) || !Mathf.Approximately(f, lastFactor))
            {
                lastInv = inv;
                lastFactor = f;
                foreach (var s in segs)
                {
                    var a = s.A * f;
                    var b = s.B * f;
                    var d = b - a;
                    float len = d.magnitude;
                    float width = s.Px;
                    float angle = -Mathf.Atan2(d.x, d.y) * Mathf.Rad2Deg;
                    if (s.Px < 0)
                    {
                        // Arrow stroke from the tip, ±28° off the line back towards the previous point.
                        bool second = s.Px < -500;
                        width = second ? -s.Px - 1000 : -s.Px;
                        len = 14f * inv;
                        angle += second ? -28f : 28f;
                    }
                    s.Rt.localPosition = new Vector3(a.x, a.y, 0);
                    s.Rt.sizeDelta = new Vector2(width * inv, len);
                    s.Rt.localEulerAngles = new Vector3(0, 0, angle);
                }
                foreach (var d in dots)
                {
                    var p = d.P * f;
                    d.Rt.localPosition = new Vector3(p.x, p.y, 0);
                    d.Rt.sizeDelta = new Vector2(d.Px * inv, d.Px * inv);
                    d.Rt.localEulerAngles = new Vector3(0, 0, 45); // diamond
                }
                foreach (var l in labels)
                {
                    var p = l.P * f;
                    l.Rt.localPosition = new Vector3(p.x, p.y + l.DyPx * inv, 0);
                    l.Rt.localScale = Vector3.one * inv;
                }
            }
            // The small HUD map rotates with the aircraft: keep text upright on screen.
            foreach (var l in labels) l.Rt.rotation = Quaternion.identity;
        }

        public void Dispose() => Clear();
    }
}
