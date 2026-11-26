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
var link = new LinkService(server, IntelMode.Side, 4, "harness");
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
    private string? victim;
    private bool loaded;
    public IReadOnlyCollection<string> Destroyed => destroyed;

    public FakeWorld(string faction, bool allowAll, string? missionPath)
    {
        this.faction = faction;
        this.allowAll = allowAll;
        if (missionPath != null)
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(missionPath));
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
        cx = target.x; cz = target.z;
    }

    public string Describe() => $"{statics.Count} units, will destroy '{victim}'";

    public SessionInfo Session(IntelMode requested) => new()
    {
        InMission = true,
        Role = "host",
        Mission = "Harness mission",
        Map = "Heartland",
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
        if (Visible(intel, bandit, me)) into.Add(bandit);

        foreach (var s in statics)
        {
            if (destroyed.Contains(s.id)) continue;
            var e = new EntityState { Id = s.id, Type = s.type, Kind = s.kind, Faction = s.faction, X = s.x, Y = s.y, Z = s.z };
            if (Visible(intel, e, me)) into.Add(e);
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
