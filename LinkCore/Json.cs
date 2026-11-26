using System.Globalization;
using System.Text;

namespace NOPlannerLink.Core
{
    /// <summary>Tiny allocation-light JSON writer (no external JSON library inside the game).</summary>
    public sealed class JsonWriter
    {
        private readonly StringBuilder sb = new StringBuilder(4096);
        private bool needComma;

        public JsonWriter BeginObject() { Sep(); sb.Append('{'); needComma = false; return this; }
        public JsonWriter EndObject() { sb.Append('}'); needComma = true; return this; }
        public JsonWriter BeginArray() { Sep(); sb.Append('['); needComma = false; return this; }
        public JsonWriter EndArray() { sb.Append(']'); needComma = true; return this; }

        public JsonWriter Key(string key)
        {
            Sep();
            WriteString(key);
            sb.Append(':');
            needComma = false;
            return this;
        }

        public JsonWriter Value(string? v)
        {
            Sep();
            if (v == null) sb.Append("null"); else WriteString(v);
            needComma = true;
            return this;
        }

        public JsonWriter Value(double v)
        {
            Sep();
            if (double.IsNaN(v) || double.IsInfinity(v)) sb.Append('0');
            else sb.Append(v.ToString("0.###", CultureInfo.InvariantCulture));
            needComma = true;
            return this;
        }

        public JsonWriter Value(bool v)
        {
            Sep();
            sb.Append(v ? "true" : "false");
            needComma = true;
            return this;
        }

        public JsonWriter Prop(string k, string? v) => Key(k).Value(v);
        public JsonWriter Prop(string k, double v) => Key(k).Value(v);
        public JsonWriter Prop(string k, bool v) => Key(k).Value(v);

        private void Sep()
        {
            if (needComma) sb.Append(',');
        }

        private void WriteString(string s)
        {
            sb.Append('"');
            foreach (var c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }

        public override string ToString() => sb.ToString();
    }

    /// <summary>Minimal reader for the planner's small command messages, e.g. {"type":"setIntel","mode":"all"}.</summary>
    public static class JsonFields
    {
        /// <summary>Returns the string value of a top-level "key":"value" pair, or null.</summary>
        public static string? GetString(string json, string key)
        {
            var needle = "\"" + key + "\"";
            var i = json.IndexOf(needle, System.StringComparison.Ordinal);
            if (i < 0) return null;
            i = json.IndexOf(':', i + needle.Length);
            if (i < 0) return null;
            i++;
            while (i < json.Length && char.IsWhiteSpace(json[i])) i++;
            if (i >= json.Length || json[i] != '"') return null;
            var end = json.IndexOf('"', i + 1);
            return end < 0 ? null : json.Substring(i + 1, end - i - 1);
        }
    }
}
