using System;
using System.Collections.Generic;

namespace NOPlannerLink.Core
{
    public enum IntelMode { Side, All }

    /// <summary>One unit as the planner sees it (docs/live-protocol.md, protocol v2).</summary>
    public sealed class EntityState
    {
        public string Id = "";
        public string Type = "";
        /// <summary>aircraft | ground | ship | building | missile | pilot | other</summary>
        public string Kind = "other";
        public string Faction = "Neutral";
        /// <summary>World metres, same as mission files: x east, y up, z north.</summary>
        public double X, Y, Z;
        /// <summary>Degrees from north, clockwise.</summary>
        public double Heading;
        /// <summary>m/s</summary>
        public double Speed;
        public bool Alive = true;
        public string? Player;
        public bool IsLocal;
        /// <summary>Enemy unit shown because the local faction tracks it (fog-of-war mode).</summary>
        public bool Detected;
        /// <summary>Position in the local player's weapon target list: 1 = primary target, 2+ = other selected targets, 0 = not targeted.</summary>
        public int TargetRank;
        /// <summary>This unit's radar is painting my aircraft (the in-game map's radar line): 0 none, 1 painted, 2 detected me, 3 locked/targeting me.</summary>
        public int RadarOnMe;
        /// <summary>A missile my aircraft's missile warning knows is coming at me (the in-game incoming-missile warning).</summary>
        public bool IncomingOnMe;
        /// <summary>This unit's radar is jammed (the in-game map's JAMMED marker).</summary>
        public bool Jammed;
        /// <summary>Id of the unit jamming it, when known (the in-game map draws a line from it).</summary>
        public string? JammedBy;
        /// <summary>This unit is jamming my aircraft (in the last ~2 s).</summary>
        public bool JammingMe;

        /// <summary>True if the planner should be told about a change (thresholds keep deltas small).</summary>
        public bool DiffersFrom(EntityState o)
        {
            return Math.Abs(X - o.X) > 2 || Math.Abs(Y - o.Y) > 2 || Math.Abs(Z - o.Z) > 2
                || AngleDiff(Heading, o.Heading) > 2 || Math.Abs(Speed - o.Speed) > 2
                || Alive != o.Alive || Faction != o.Faction || Player != o.Player || Detected != o.Detected
                || Type != o.Type || IsLocal != o.IsLocal || TargetRank != o.TargetRank || RadarOnMe != o.RadarOnMe || IncomingOnMe != o.IncomingOnMe || Jammed != o.Jammed || JammedBy != o.JammedBy || JammingMe != o.JammingMe;
        }

        private static double AngleDiff(double a, double b)
        {
            var d = Math.Abs(a - b) % 360;
            return d > 180 ? 360 - d : d;
        }

        public void Write(JsonWriter w)
        {
            w.BeginObject()
                .Prop("id", Id)
                .Prop("type", Type)
                .Prop("kind", Kind)
                .Prop("faction", Faction)
                .Key("pos").BeginObject().Prop("x", Math.Round(X, 1)).Prop("y", Math.Round(Y, 1)).Prop("z", Math.Round(Z, 1)).EndObject()
                .Prop("heading", Math.Round(Heading))
                .Prop("speed", Math.Round(Speed, 1))
                .Prop("alive", Alive);
            if (Player != null) w.Prop("player", Player).Prop("isPlayer", true);
            if (IsLocal) w.Prop("isLocal", true);
            if (Detected) w.Prop("detected", true);
            if (TargetRank > 0) w.Prop("target", TargetRank);
            if (RadarOnMe > 0) w.Prop("radarOnMe", RadarOnMe);
            if (IncomingOnMe) w.Prop("incoming", true);
            if (Jammed) w.Prop("jammed", true);
            if (JammedBy != null) w.Prop("jammedBy", JammedBy);
            if (JammingMe) w.Prop("jammingMe", true);
            w.EndObject();
        }
    }

    /// <summary>Session info sent in the hello message.</summary>
    public sealed class SessionInfo
    {
        public string? Mission;
        public string? Map;
        public string? LocalFaction;
        public string? LocalPlayer;
        public bool InMission;
        /// <summary>single | host | client | menu</summary>
        public string Role = "menu";
        public IntelMode Intel = IntelMode.Side;
        public bool AllowAll;

        public bool SameAs(SessionInfo o) =>
            Mission == o.Mission && Map == o.Map && LocalFaction == o.LocalFaction && LocalPlayer == o.LocalPlayer
            && InMission == o.InMission && Role == o.Role && Intel == o.Intel && AllowAll == o.AllowAll;
    }

    /// <summary>A discrete game event (destroyed, launch, detected, lost...).</summary>
    public sealed class GameEvent
    {
        public string Kind = "custom";
        public double T;
        public string? Source;
        public string? Target;
        public string? Text;
    }

    /// <summary>
    /// Turns successive frames into protocol messages: hello / snapshot / delta / event.
    /// Pure logic (no sockets, no Unity) so it can be unit-tested.
    /// </summary>
    public sealed class FrameEncoder
    {
        public const int ProtocolVersion = 2;
        private Dictionary<string, EntityState> last = new Dictionary<string, EntityState>();
        private SessionInfo? lastSession;
        public string ModVersion = "0.0.0";

        public string Hello(SessionInfo s)
        {
            lastSession = s;
            var w = new JsonWriter();
            w.BeginObject()
                .Prop("type", "hello")
                .Prop("protocol", ProtocolVersion)
                .Prop("game", "Nuclear Option")
                .Prop("mod", "NOPlannerLink " + ModVersion)
                .Prop("inMission", s.InMission)
                .Prop("role", s.Role)
                .Prop("intel", s.Intel == IntelMode.All ? "all" : "side")
                .Prop("allowAll", s.AllowAll);
            if (s.Mission != null) w.Prop("mission", s.Mission);
            if (s.Map != null) w.Prop("map", s.Map);
            if (s.LocalFaction != null) w.Prop("localFaction", s.LocalFaction);
            if (s.LocalPlayer != null) w.Prop("localPlayer", s.LocalPlayer);
            w.EndObject();
            return w.ToString();
        }

        /// <summary>True if the session changed since the last hello (a new hello should be sent).</summary>
        public bool SessionChanged(SessionInfo s) => lastSession == null || !lastSession.SameAs(s);

        /// <summary>Full state; resets the diff baseline.</summary>
        public string Snapshot(double t, IReadOnlyList<EntityState> entities, IEnumerable<string>? destroyed = null)
        {
            last = new Dictionary<string, EntityState>(entities.Count);
            var w = new JsonWriter();
            w.BeginObject().Prop("type", "snapshot").Prop("t", Math.Round(t, 1)).Key("entities").BeginArray();
            foreach (var e in entities)
            {
                if (last.ContainsKey(e.Id)) continue;
                last[e.Id] = e;
                e.Write(w);
            }
            w.EndArray();
            w.Key("destroyed").BeginArray();
            if (destroyed != null) foreach (var id in destroyed) w.Value(id);
            w.EndArray().EndObject();
            return w.ToString();
        }

        /// <summary>Changed and removed entities since the previous frame, or null if nothing changed.</summary>
        public string? Delta(double t, IReadOnlyList<EntityState> entities)
        {
            var next = new Dictionary<string, EntityState>(entities.Count);
            var changed = new List<EntityState>();
            foreach (var e in entities)
            {
                if (next.ContainsKey(e.Id)) continue;
                if (last.TryGetValue(e.Id, out var prev) && !e.DiffersFrom(prev))
                {
                    next[e.Id] = prev; // keep the baseline so slow drift still crosses the threshold
                    continue;
                }
                next[e.Id] = e;
                changed.Add(e);
            }
            var removed = new List<string>();
            foreach (var id in last.Keys) if (!next.ContainsKey(id)) removed.Add(id);
            last = next;
            if (changed.Count == 0 && removed.Count == 0) return null;

            var w = new JsonWriter();
            w.BeginObject().Prop("type", "delta").Prop("t", Math.Round(t, 1)).Key("entities").BeginArray();
            foreach (var e in changed) e.Write(w);
            w.EndArray().Key("removed").BeginArray();
            foreach (var id in removed) w.Value(id);
            w.EndArray().EndObject();
            return w.ToString();
        }

        /// <summary>The running mission file (answer to getMission): {type:"mission", name, json} or {type:"mission", error}.</summary>
        public static string Mission(string? name, string? json, string? error)
        {
            var w = new JsonWriter();
            w.BeginObject().Prop("type", "mission");
            if (name != null) w.Prop("name", name);
            if (json != null) w.Prop("json", json);
            if (error != null) w.Prop("error", error);
            w.EndObject();
            return w.ToString();
        }

        public static string Event(GameEvent e)
        {
            var w = new JsonWriter();
            w.BeginObject().Prop("type", "event").Key("event").BeginObject()
                .Prop("kind", e.Kind).Prop("t", Math.Round(e.T, 1));
            if (e.Source != null) w.Prop("source", e.Source);
            if (e.Target != null) w.Prop("target", e.Target);
            if (e.Text != null) w.Prop("text", e.Text);
            w.EndObject().EndObject();
            return w.ToString();
        }
    }
}
