using System.Text.Json;
using NOPlannerLink.Core;

// Fake Nuclear Option session for testing the planner without the game.
//   dotnet run -- [mission.json] [--faction Boscali] [--allow-all] [--port 8787]
// With a mission file, enemy units use the mission's real UniqueNames, so the planner can
// match them; one enemy is destroyed 20 s after the first planner connects.
var argv = args.ToList();
string? Opt(string name) { var i = argv.IndexOf(name); return i >= 0 && i + 1 < argv.Count ? argv[i + 1] : null; }
var port = int.Parse(Opt("--port") ?? "8787");
var faction = Opt("--faction") ?? "Boscali";
var allowAll = argv.Contains("--allow-all");
var missionPath = argv.FirstOrDefault(a => a.EndsWith(".json", StringComparison.OrdinalIgnoreCase));

var world = new FakeWorld(faction, allowAll, missionPath);
using var server = new WsServer(port, new OriginPolicy("*").Allowed, Console.WriteLine);
server.Start();
var link = new LinkService(server, IntelMode.Side, 4, "1.4.0-harness");
Console.WriteLine($"Fake game on ws://localhost:{port} · faction {faction} · allowAll={allowAll} · {world.Describe()}");
var clock = System.Diagnostics.Stopwatch.StartNew();
while (true)
{
    link.Tick(clock.Elapsed.TotalSeconds, world);
    Thread.Sleep(20);
}

sealed class FakeWorld : IWorldSource
{
    private readonly string faction;
    private readonly bool allowAll;
    private readonly List<(string id, string type, string faction, double x, double y, double z, string kind)> statics = new();
    private readonly HashSet<string> destroyed = new();
    private readonly System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
    private readonly double cx, cz;
    private string? victim, second;
    private bool loaded;
    private List<string>? targets; // my aircraft's target list (first = primary); the planner can change it
    private readonly HashSet<string> visible = new();
    public IReadOnlyCollection<string> Destroyed => destroyed;

    private readonly string? missionText;
    private readonly string? missionName;
    private readonly string mapKey = "(None,)";
    public string? MissionJson(out string? name) { name = missionName; return missionText; }

    public string? SetOverlay(OverlaySet overlay)
    {
        if (!loaded) return "not in a mission";
        Console.WriteLine($"[harness] in-game map overlay: {overlay.Lines.Count} lines, {overlay.Labels.Count} labels" +
            (overlay.Labels.Count > 0 ? " (" + string.Join(", ", overlay.Labels.Take(6).Select(l => l.Text)) + ")" : ""));
        return null;
    }

    public string? SelectTarget(string op, string? id, IntelMode effective)
    {
        if (effective != IntelMode.Side) return "target selection only works in Detected mode";
        targets ??= new List<string>();
        if (op == "clear") { targets.Clear(); return null; }
        if (id == null) return "unit not found";
        var s = statics.FirstOrDefault(x => x.id == id);
        var f = s.id != null ? s.faction : id == "p:201" ? "enemy" : null;
        if (f == null || destroyed.Contains(id)) return "unit not found";
        if (f == faction || f == "Neutral") return "only enemy units can be targeted";
        if (!visible.Contains(id)) return "your side hasn't detected it";
        if (op == "remove") { targets.Remove(id); return null; }
        if (!targets.Contains(id)) targets.Insert(0, id);
        Console.WriteLine($"[harness] target list: {string.Join(", ", targets)}");
        return null;
    }

    public FakeWorld(string faction, bool allowAll, string? missionPath)
    {
        this.faction = faction;
        this.allowAll = allowAll;
        if (missionPath != null)
        {
            missionText = File.ReadAllText(missionPath);
            missionName = Path.GetFileNameWithoutExtension(missionPath);
            using var doc = JsonDocument.Parse(missionText);
            // Report the map like the game does (MapKey.ToString()).
            if (doc.RootElement.TryGetProperty("MapKey", out var mk) && mk.TryGetProperty("Path", out var mp) && !string.IsNullOrEmpty(mp.GetString()))
                mapKey = $"(GameWorldPrefab,{mp.GetString()})";
            foreach (var (list, kind) in new[] { ("vehicles", "ground"), ("buildings", "building"), ("ships", "ship") })
            {
                if (!doc.RootElement.TryGetProperty(list, out var arr)) continue;
                foreach (var u in arr.EnumerateArray())
                {
                    var p = u.GetProperty("globalPosition");
                    var f = u.TryGetProperty("faction", out var fv) ? fv.GetString() ?? "Neutral" : "Neutral";
                    statics.Add((u.GetProperty("UniqueName").GetString()!, u.GetProperty("type").GetString()!, f == "" || f == "None" ? "Neutral" : f,
                        p.GetProperty("x").GetDouble(), p.GetProperty("y").GetDouble(), p.GetProperty("z").GetDouble(), kind));
                }
            }
        }
        if (statics.Count == 0)
        {
            statics.Add(("SAM_1", "SAMTrailer1", "Primeva", 6000, 100, 4000, "ground"));
            statics.Add(("IFV_1", "AFV8_IFV", "Primeva", 5000, 100, 2000, "ground"));
            statics.Add(("IFV_2", "AFV8_IFV", faction, -3000, 100, -2000, "ground"));
        }
        // Fly around the first enemy ground unit so it gets "detected" and destroyed.
        var target = statics.FirstOrDefault(s => s.faction != faction && s.faction != "Neutral" && s.kind == "ground");
        if (target.id == null) target = statics[0];
        victim = target.id;
        var t0 = target;
        second = statics.Where(s => s.faction == t0.faction && s.kind == "ground" && s.id != t0.id)
            .OrderBy(s => (s.x - t0.x) * (s.x - t0.x) + (s.z - t0.z) * (s.z - t0.z)).Select(s => s.id).FirstOrDefault();
        cx = target.x; cz = target.z;
    }

    public string Describe() => $"{statics.Count} units, will destroy '{victim}'";

    public SessionInfo Session(IntelMode requested) => new()
    {
        InMission = true,
        Role = "host",
        Mission = "Harness mission",
        Map = mapKey,
        LocalFaction = faction,
        LocalPlayer = "You",
        AllowAll = allowAll,
        Intel = requested == IntelMode.All && allowAll ? IntelMode.All : IntelMode.Side,
    };

    public double Capture(IntelMode intel, List<EntityState> into, List<GameEvent> events)
    {
        var t = clock.Elapsed.TotalSeconds;
        if (!loaded) { loaded = true; events.Add(new GameEvent { Kind = "missionLoaded", Text = "Harness mission" }); }
        if (t > 20 && victim != null && destroyed.Add(victim))
            events.Add(new GameEvent { Kind = "destroyed", T = t, Target = victim, Source = "You" });

        // My plane: 12 km circle around the target, 150 m/s at 2000 m.
        double a = t * 150 / 12000;
        var me = Plane("me", "Multirole1", faction, cx + 12000 * Math.Sin(a), 2000, cz + 12000 * Math.Cos(a), a, 150, "You", true);
        into.Add(me);
        // Teammate (human, same faction) trailing 3 km; friendly AI wider; enemy AI fighter opposite side.
        double b = a - 0.25;
        into.Add(Plane("p:101", "Fighter1", faction, cx + 12000 * Math.Sin(b), 2500, cz + 12000 * Math.Cos(b), b, 150, "Wingman", false));
        double c = -t * 200 / 20000;
        into.Add(Plane("p:102", "Multirole1", faction, cx + 20000 * Math.Sin(c), 4000, cz + 20000 * Math.Cos(c), c + Math.PI, 200, null, false));
        var enemyFaction = statics.FirstOrDefault(s => s.faction != faction && s.faction != "Neutral").faction ?? "Primeva";
        double d = a + Math.PI;
        var bandit = Plane("p:201", "Fighter1", enemyFaction, cx + 25000 * Math.Sin(d), 5000, cz + 25000 * Math.Cos(d), d, 220, null, false);
        // The bandit's radar sweeps me now and then, and every 20 s locks on for 6 s (red line in the planner).
        bandit.RadarOnMe = (int)t % 20 >= 12 && (int)t % 20 < 18 ? 3 : (int)t % 10 < 5 ? 1 : 0;
        if (bandit.RadarOnMe == 0) bandit.JammingMe = true; // ...and jams me the rest of the time
        if (targets != null && targets.Contains(bandit.Id)) bandit.TargetRank = targets.IndexOf(bandit.Id) + 1;
        if (Visible(intel, bandit, me) || bandit.RadarOnMe > 0) { into.Add(bandit); visible.Add(bandit.Id); }
        else visible.Remove(bandit.Id);

        // An incoming missile from the locked SAM site between 8 s and 18 s.
        var src = statics.FirstOrDefault(s => s.id == victim);
        if (src.id != null && t > 8 && t < 18)
        {
            var f = (t - 8) / 10;
            var msl = new EntityState
            {
                Id = "p:301", Type = "AAM2", Kind = "missile", Faction = src.faction,
                X = src.x + (me.X - src.x) * f, Y = 100 + (me.Y - 100) * f, Z = src.z + (me.Z - src.z) * f,
                Heading = Math.Atan2(me.X - src.x, me.Z - src.z) * 180 / Math.PI, Speed = 900, IncomingOnMe = true,
            };
            into.Add(msl);
        }

        foreach (var s in statics)
        {
            if (destroyed.Contains(s.id)) continue;
            var e = new EntityState { Id = s.id, Type = s.type, Kind = s.kind, Faction = s.faction, X = s.x, Y = s.y, Z = s.z };
            // Targets: start with the victim (primary) and its neighbour; the planner can change the list.
            if (targets == null) targets = new List<string> { victim!, second! }.Where(x => x != null).ToList();
            var rank = targets.Where(x => !destroyed.Contains(x)).ToList().IndexOf(s.id);
            if (rank >= 0) e.TargetRank = rank + 1;
            // Jamming: the 'second' radar is jammed by my friendly AI (p:102) in 10 s windows.
            if (s.id == second && (int)t % 20 >= 10) { e.Jammed = true; e.JammedBy = "p:102"; }
            // Enemy radars near me paint me: the victim locks me, other enemies within 20 km cycle painted → detected.
            if (s.faction != faction && s.faction != "Neutral")
            {
                var dist = Math.Sqrt((s.x - me.X) * (s.x - me.X) + (s.z - me.Z) * (s.z - me.Z));
                if (s.id == victim) e.RadarOnMe = 3;
                else if (dist < 20000 && s.kind == "ground" && (s.id.GetHashCode() & 7) == 0) e.RadarOnMe = ((int)t / 3 % 3 == 0) ? 0 : (int)t / 3 % 3;
            }
            if (Visible(intel, e, me) || e.RadarOnMe > 0) { into.Add(e); visible.Add(e.Id); }
            else visible.Remove(e.Id);
        }
        return t;
    }

    /// <summary>Side intel: enemies count as "detected" within 30 km of my plane.</summary>
    private bool Visible(IntelMode intel, EntityState e, EntityState me)
    {
        if (e.Faction == faction || e.Faction == "Neutral" || intel == IntelMode.All) return true;
        var near = Math.Sqrt((e.X - me.X) * (e.X - me.X) + (e.Z - me.Z) * (e.Z - me.Z)) < 30000;
        e.Detected = near;
        return near;
    }

    private static EntityState Plane(string id, string type, string faction, double x, double y, double z, double angle, double speed, string? player, bool local) => new()
    {
        Id = id, Type = type, Kind = "aircraft", Faction = faction, X = x, Y = y, Z = z,
        Heading = ((angle * 180 / Math.PI) + 90) % 360, Speed = speed, Player = player, IsLocal = local,
    };
}
