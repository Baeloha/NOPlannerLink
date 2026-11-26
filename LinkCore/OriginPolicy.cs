using System;
using System.Linq;

namespace NOPlannerLink.Core
{
    /// <summary>
    /// Which web pages may connect. Browsers send an Origin header; only the planner (and local dev)
    /// are allowed by default so an unrelated website can't read game state from localhost.
    /// Patterns are comma-separated; "*" allows everything; a trailing ":*" matches any port.
    /// </summary>
    public sealed class OriginPolicy
    {
        public const string Default = "https://nuclear-planner.vercel.app,http://localhost:*,http://127.0.0.1:*";
        private readonly string[] patterns;

        public OriginPolicy(string config)
        {
            patterns = (config ?? "").Split(',').Select(p => p.Trim().TrimEnd('/')).Where(p => p.Length > 0).ToArray();
        }

        public bool Allowed(string? origin)
        {
            // Non-browser clients (tools, tests) send no Origin header.
            if (string.IsNullOrEmpty(origin)) return true;
            var o = origin!.Trim().TrimEnd('/');
            foreach (var p in patterns)
            {
                if (p == "*") return true;
                if (p.EndsWith(":*", StringComparison.Ordinal))
                {
                    var host = p.Substring(0, p.Length - 2);
                    if (o.Equals(host, StringComparison.OrdinalIgnoreCase) || o.StartsWith(host + ":", StringComparison.OrdinalIgnoreCase)) return true;
                }
                else if (o.Equals(p, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }
    }
}
