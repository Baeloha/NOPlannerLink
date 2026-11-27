using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace NOPlannerLink.Core
{
    /// <summary>A polyline the planner wants on the in-game map (route, drawn line, circle/oval outline).</summary>
    public sealed class OverlayLine
    {
        /// <summary>Points as x0,z0,x1,z1,… in world metres (same coordinates as mission files).</summary>
        public float[] Pts = new float[0];
        public float R = 1, G = 1, B = 1;
        public bool Closed;
        public bool Arrow;
        /// <summary>Draw a dot at every point (route waypoints).</summary>
        public bool Dots;
        /// <summary>Line width in screen pixels (1–6).</summary>
        public float Width = 2;
        /// <summary>Optional label per point (e.g. waypoint numbers); null or "" = none.</summary>
        public string[]? PointLabels;
    }

    /// <summary>A text label on the in-game map (note, route or shape name).</summary>
    public sealed class OverlayLabel
    {
        public string Text = "";
        public float X, Z;
        public float R = 1, G = 1, B = 1;
    }

    public sealed class OverlaySet
    {
        public readonly List<OverlayLine> Lines = new List<OverlayLine>();
        public readonly List<OverlayLabel> Labels = new List<OverlayLabel>();
        public int Count => Lines.Count + Labels.Count;
    }

    /// <summary>
    /// Parses the planner's overlay command:
    /// {"type":"overlay","lines":[{"pts":[x,z,…],"color":"#rrggbb","closed":false,"arrow":false,"dots":true,"width":2,"labels":["S","1"]}],
    ///  "labels":[{"text":"…","x":0,"z":0,"color":"#rrggbb"}]}
    /// Limits keep a bad message from flooding the map.
    /// </summary>
    public static class OverlayParser
    {
        public const int MaxLines = 200, MaxPoints = 2000, MaxLabels = 300, MaxText = 60;

        public static OverlaySet Parse(string json)
        {
            var root = MiniJson.Parse(json) as Dictionary<string, object?> ?? throw new FormatException("overlay: not an object");
            var set = new OverlaySet();
            if (root.TryGetValue("lines", out var lo) && lo is List<object?> lines)
            {
                foreach (var o in lines)
                {
                    if (set.Lines.Count >= MaxLines) break;
                    if (!(o is Dictionary<string, object?> d)) continue;
                    if (!(d.TryGetValue("pts", out var po) && po is List<object?> pts) || pts.Count < 4) continue;
                    var n = Math.Min(pts.Count / 2, MaxPoints) * 2;
                    var arr = new float[n];
                    for (int i = 0; i < n; i++) arr[i] = (float)Num(pts[i]);
                    var line = new OverlayLine
                    {
                        Pts = arr,
                        Closed = Bool(d, "closed"),
                        Arrow = Bool(d, "arrow"),
                        Dots = Bool(d, "dots"),
                        Width = (float)Math.Max(1, Math.Min(6, d.TryGetValue("width", out var w) ? Num(w) : 2)),
                    };
                    Color(d, out line.R, out line.G, out line.B);
                    if (d.TryGetValue("labels", out var lb) && lb is List<object?> ls)
                    {
                        line.PointLabels = new string[n / 2];
                        for (int i = 0; i < line.PointLabels.Length && i < ls.Count; i++) line.PointLabels[i] = Text(ls[i] as string);
                    }
                    set.Lines.Add(line);
                }
            }
            if (root.TryGetValue("labels", out var la) && la is List<object?> labels)
            {
                foreach (var o in labels)
                {
                    if (set.Labels.Count >= MaxLabels) break;
                    if (!(o is Dictionary<string, object?> d)) continue;
                    var text = Text(d.TryGetValue("text", out var t) ? t as string : null);
                    if (text.Length == 0) continue;
                    var l = new OverlayLabel { Text = text, X = (float)Num(d.TryGetValue("x", out var x) ? x : null), Z = (float)Num(d.TryGetValue("z", out var z) ? z : null) };
                    Color(d, out l.R, out l.G, out l.B);
                    set.Labels.Add(l);
                }
            }
            return set;
        }

        private static double Num(object? o) => o is double d && !double.IsNaN(d) && !double.IsInfinity(d) ? d : 0;
        private static bool Bool(Dictionary<string, object?> d, string k) => d.TryGetValue(k, out var v) && v is bool b && b;

        private static string Text(string? s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = s!.Replace('\n', ' ').Replace('\r', ' ').Trim();
            return s.Length > MaxText ? s.Substring(0, MaxText) + "…" : s;
        }

        private static void Color(Dictionary<string, object?> d, out float r, out float g, out float b)
        {
            r = g = b = 1;
            if (!(d.TryGetValue("color", out var c) && c is string s)) return;
            s = s.TrimStart('#');
            if (s.Length == 3) s = new string(new[] { s[0], s[0], s[1], s[1], s[2], s[2] });
            if (s.Length != 6 || !int.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v)) return;
            r = ((v >> 16) & 255) / 255f;
            g = ((v >> 8) & 255) / 255f;
            b = (v & 255) / 255f;
        }
    }

    /// <summary>Small JSON reader: objects → Dictionary, arrays → List, numbers → double, plus string / bool / null.</summary>
    public static class MiniJson
    {
        public static object? Parse(string s)
        {
            int i = 0;
            var v = Value(s, ref i, 0);
            Ws(s, ref i);
            if (i != s.Length) throw new FormatException("trailing characters at " + i);
            return v;
        }

        private static void Ws(string s, ref int i)
        {
            while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
        }

        private static object? Value(string s, ref int i, int depth)
        {
            if (depth > 32) throw new FormatException("too deep");
            Ws(s, ref i);
            if (i >= s.Length) throw new FormatException("unexpected end");
            char c = s[i];
            if (c == '{')
            {
                i++;
                var d = new Dictionary<string, object?>();
                Ws(s, ref i);
                if (i < s.Length && s[i] == '}') { i++; return d; }
                while (true)
                {
                    Ws(s, ref i);
                    if (i >= s.Length || s[i] != '"') throw new FormatException("key expected at " + i);
                    var k = Str(s, ref i);
                    Ws(s, ref i);
                    if (i >= s.Length || s[i] != ':') throw new FormatException(": expected at " + i);
                    i++;
                    d[k] = Value(s, ref i, depth + 1);
                    Ws(s, ref i);
                    if (i < s.Length && s[i] == ',') { i++; continue; }
                    if (i < s.Length && s[i] == '}') { i++; return d; }
                    throw new FormatException(", or } expected at " + i);
                }
            }
            if (c == '[')
            {
                i++;
                var l = new List<object?>();
                Ws(s, ref i);
                if (i < s.Length && s[i] == ']') { i++; return l; }
                while (true)
                {
                    l.Add(Value(s, ref i, depth + 1));
                    Ws(s, ref i);
                    if (i < s.Length && s[i] == ',') { i++; continue; }
                    if (i < s.Length && s[i] == ']') { i++; return l; }
                    throw new FormatException(", or ] expected at " + i);
                }
            }
            if (c == '"') return Str(s, ref i);
            if (Lit(s, ref i, "true")) return true;
            if (Lit(s, ref i, "false")) return false;
            if (Lit(s, ref i, "null")) return null;
            int start = i;
            while (i < s.Length && "+-0123456789.eE".IndexOf(s[i]) >= 0) i++;
            if (start == i || !double.TryParse(s.Substring(start, i - start), NumberStyles.Float, CultureInfo.InvariantCulture, out var num))
                throw new FormatException("bad value at " + start);
            return num;
        }

        private static bool Lit(string s, ref int i, string lit)
        {
            if (string.CompareOrdinal(s, i, lit, 0, lit.Length) != 0) return false;
            i += lit.Length;
            return true;
        }

        private static string Str(string s, ref int i)
        {
            i++; // opening quote
            var sb = new StringBuilder();
            while (i < s.Length)
            {
                char c = s[i++];
                if (c == '"') return sb.ToString();
                if (c != '\\') { sb.Append(c); continue; }
                if (i >= s.Length) break;
                char e = s[i++];
                switch (e)
                {
                    case 'n': sb.Append('\n'); break;
                    case 't': sb.Append('\t'); break;
                    case 'r': sb.Append('\r'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'u':
                        if (i + 4 > s.Length) throw new FormatException("bad \\u escape");
                        sb.Append((char)int.Parse(s.Substring(i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                        i += 4;
                        break;
                    default: sb.Append(e); break;
                }
            }
            throw new FormatException("unterminated string");
        }
    }
}
