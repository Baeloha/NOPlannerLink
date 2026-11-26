using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using NOPlannerLink.Core;
using Xunit;

public class JsonTests
{
    [Fact]
    public void EscapesAndFormatsInvariantly()
    {
        var w = new JsonWriter();
        w.BeginObject().Prop("s", "a\"b\\c\n").Prop("n", 1.5).Prop("b", true).Key("arr").BeginArray().Value("x").Value(2).EndArray().EndObject();
        using var doc = JsonDocument.Parse(w.ToString());
        Assert.Equal("a\"b\\c\n", doc.RootElement.GetProperty("s").GetString());
        Assert.Equal(1.5, doc.RootElement.GetProperty("n").GetDouble());
        Assert.Equal(2, doc.RootElement.GetProperty("arr").GetArrayLength());
    }

    [Fact]
    public void ReadsCommandFields()
    {
        Assert.Equal("setIntel", JsonFields.GetString("{\"type\": \"setIntel\",\"mode\":\"all\"}", "type"));
        Assert.Equal("all", JsonFields.GetString("{\"type\": \"setIntel\",\"mode\":\"all\"}", "mode"));
        Assert.Null(JsonFields.GetString("{\"type\":1}", "type"));
    }
}

public class EncoderTests
{
    private static EntityState E(string id, double x) => new() { Id = id, Type = "T", Kind = "ground", Faction = "A", X = x };

    [Fact]
    public void DeltaSendsOnlyChangesAndRemovals()
    {
        var enc = new FrameEncoder();
        var snap = JsonDocument.Parse(enc.Snapshot(0, new[] { E("a", 0), E("b", 0) }, new[] { "dead" })).RootElement;
        Assert.Equal(2, snap.GetProperty("entities").GetArrayLength());
        Assert.Equal("dead", snap.GetProperty("destroyed")[0].GetString());

        Assert.Null(enc.Delta(1, new[] { E("a", 1), E("b", 0) })); // 1 m move is below threshold

        var d = JsonDocument.Parse(enc.Delta(2, new[] { E("a", 50) })!).RootElement;
        Assert.Equal("delta", d.GetProperty("type").GetString());
        Assert.Equal("a", d.GetProperty("entities")[0].GetProperty("id").GetString());
        Assert.Equal("b", d.GetProperty("removed")[0].GetString());
    }

    [Fact]
    public void TargetSelectionIsSentAsAChange()
    {
        var enc = new FrameEncoder();
        enc.Snapshot(0, new[] { E("a", 0) });
        var t = E("a", 0); t.TargetRank = 1;
        var d = JsonDocument.Parse(enc.Delta(1, new[] { t })!).RootElement;
        Assert.Equal(1, d.GetProperty("entities")[0].GetProperty("target").GetInt32());
        var none = JsonDocument.Parse(enc.Delta(2, new[] { E("a", 0) })!).RootElement;
        Assert.False(none.GetProperty("entities")[0].TryGetProperty("target", out _));
    }

    [Fact]
    public void RadarOnMeIsSentAsAChange()
    {
        var enc = new FrameEncoder();
        enc.Snapshot(0, new[] { E("sam", 0) });
        var r = E("sam", 0); r.RadarOnMe = 3;
        var d = JsonDocument.Parse(enc.Delta(1, new[] { r })!).RootElement;
        Assert.Equal(3, d.GetProperty("entities")[0].GetProperty("radarOnMe").GetInt32());
    }

    [Fact]
    public void IncomingMissileIsFlagged()
    {
        var enc = new FrameEncoder();
        var m = E("msl", 0); m.Kind = "missile"; m.IncomingOnMe = true;
        var s = JsonDocument.Parse(enc.Snapshot(0, new[] { m })).RootElement;
        Assert.True(s.GetProperty("entities")[0].GetProperty("incoming").GetBoolean());
    }

    [Fact]
    public void SlowDriftEventuallyCrossesThreshold()
    {
        var enc = new FrameEncoder();
        enc.Snapshot(0, new[] { E("a", 0) });
        Assert.Null(enc.Delta(1, new[] { E("a", 1.5) }));
        Assert.NotNull(enc.Delta(2, new[] { E("a", 3) })); // compared to the last *sent* state
    }

    [Fact]
    public void HelloCarriesSession()
    {
        var enc = new FrameEncoder();
        var s = new SessionInfo { InMission = true, Role = "client", LocalFaction = "Boscali", Intel = IntelMode.Side, AllowAll = false };
        var h = JsonDocument.Parse(enc.Hello(s)).RootElement;
        Assert.Equal(2, h.GetProperty("protocol").GetInt32());
        Assert.Equal("side", h.GetProperty("intel").GetString());
        Assert.False(enc.SessionChanged(s));
        Assert.True(enc.SessionChanged(new SessionInfo { InMission = true, Role = "client", LocalFaction = "Primeva" }));
    }
}

public class OriginTests
{
    [Theory]
    [InlineData(null, true)]
    [InlineData("https://nuclear-planner.vercel.app", true)]
    [InlineData("http://localhost:5173", true)]
    [InlineData("http://127.0.0.1:4173", true)]
    [InlineData("https://evil.example", false)]
    [InlineData("http://localhost.evil.example", false)]
    public void DefaultPolicy(string? origin, bool ok) => Assert.Equal(ok, new OriginPolicy(OriginPolicy.Default).Allowed(origin));
}

public class ServerTests
{
    private sealed class World : IWorldSource
    {
        public string? SelectTarget(string op, string? id, IntelMode effective) => effective == IntelMode.Side ? null : "only in Detected mode";
        public double X;
        public SessionInfo Session(IntelMode r) => new() { InMission = true, Role = "single", AllowAll = true, Intel = r };
        public double Capture(IntelMode intel, List<EntityState> into, List<GameEvent> events)
        {
            into.Add(new EntityState { Id = "me", Type = "Fighter1", Kind = "aircraft", Faction = "A", X = X, IsLocal = true });
            if (intel == IntelMode.All) into.Add(new EntityState { Id = "enemy", Type = "T", Faction = "B" });
            return 1;
        }
        public IReadOnlyCollection<string> Destroyed => Array.Empty<string>();
    }

    private static async Task<JsonElement> Next(ClientWebSocket ws)
    {
        var buf = new byte[1 << 16];
        var sb = new StringBuilder();
        WebSocketReceiveResult r;
        using var cts = new CancellationTokenSource(5000);
        do
        {
            r = await ws.ReceiveAsync(buf, cts.Token);
            sb.Append(Encoding.UTF8.GetString(buf, 0, r.Count));
        } while (!r.EndOfMessage);
        return JsonDocument.Parse(sb.ToString()).RootElement.Clone();
    }

    [Fact]
    public async Task EndToEndOverRealWebSocket()
    {
        var port = 18787 + Random.Shared.Next(1000);
        using var server = new WsServer(port, new OriginPolicy(OriginPolicy.Default).Allowed, _ => { });
        server.Start();
        var link = new LinkService(server, IntelMode.Side, 20, "test");
        var world = new World();
        using var ws = new ClientWebSocket();
        await ws.ConnectAsync(new Uri($"ws://127.0.0.1:{port}"), default);
        var pump = Task.Run(async () =>
        {
            for (int i = 0; i < 400 && ws.State == WebSocketState.Open; i++)
            {
                world.X += 10;
                link.Tick(i * 0.06, world);
                await Task.Delay(10);
            }
        });

        var hello = await Next(ws);
        Assert.Equal("hello", hello.GetProperty("type").GetString());
        var snap = await Next(ws);
        Assert.Equal("snapshot", snap.GetProperty("type").GetString());
        Assert.Equal(1, snap.GetProperty("entities").GetArrayLength());
        var delta = await Next(ws);
        Assert.Equal("delta", delta.GetProperty("type").GetString());

        // Planner switches intel to "all": a new hello + snapshot with the enemy follows.
        await ws.SendAsync(Encoding.UTF8.GetBytes("{\"type\":\"setIntel\",\"mode\":\"all\"}"), WebSocketMessageType.Text, true, default);
        JsonElement m;
        do m = await Next(ws); while (m.GetProperty("type").GetString() != "hello");
        Assert.Equal("all", m.GetProperty("intel").GetString());
        m = await Next(ws);
        Assert.Equal(2, m.GetProperty("entities").GetArrayLength());
        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", default);
        await pump;
    }

    [Fact]
    public async Task RefusesForeignOrigin()
    {
        var port = 19787 + Random.Shared.Next(1000);
        using var server = new WsServer(port, new OriginPolicy(OriginPolicy.Default).Allowed, _ => { });
        server.Start();
        using var ws = new ClientWebSocket();
        ws.Options.SetRequestHeader("Origin", "https://evil.example");
        await Assert.ThrowsAnyAsync<WebSocketException>(() => ws.ConnectAsync(new Uri($"ws://127.0.0.1:{port}"), default));
    }
}
