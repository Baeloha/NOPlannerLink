using System;
using BepInEx;
using BepInEx.Configuration;
using NOPlannerLink.Core;
using UnityEngine;

namespace NOPlannerLink
{
    /// <summary>
    /// NOPlannerLink: streams live unit positions from Nuclear Option to the NO Mission Planner
    /// (https://nuclear-planner.vercel.app) over a WebSocket on 127.0.0.1. Read-only: it never
    /// changes game state. Protocol: docs/live-protocol.md in the planner repository.
    /// </summary>
    [BepInPlugin(Guid, Name, Version)]
    public sealed class Plugin : BaseUnityPlugin
    {
        public const string Guid = "NOPlannerLink";
        public const string Name = "NO Planner Link";
        public const string Version = "1.2.0";

        private ConfigEntry<bool> enabledCfg = null!;
        private ConfigEntry<int> port = null!;
        private ConfigEntry<string> intelDefault = null!;
        private ConfigEntry<bool> allowEverything = null!;
        private ConfigEntry<float> rateHz = null!;
        private ConfigEntry<string> allowedOrigins = null!;

        private WsServer? server;
        private LinkService? link;
        private HostGate? gate;
        private GameWorld? world;
        private float nextErrorLog;

        private void Awake()
        {
            enabledCfg = Config.Bind("General", "Enabled", true, "Run the planner link server.");
            port = Config.Bind("General", "Port", 8787, "Local WebSocket port the planner connects to (ws://localhost:PORT). Restart the game after changing.");
            rateHz = Config.Bind("General", "UpdateRateHz", 4f, new ConfigDescription("Position updates per second.", new AcceptableValueRange<float>(0.5f, 20f)));
            intelDefault = Config.Bind("Intel", "Default", "Side", new ConfigDescription(
                "Side = only what your faction knows (in-game map). All = every unit, only if allowed (see AllowEverything).",
                new AcceptableValueList<string>("Side", "All")));
            allowEverything = Config.Bind("Intel", "AllowEverything", false,
                "HOST setting: let players in your game who run this mod see ALL units (not only what their faction has detected). " +
                "Always allowed in single-player. Ignored when you are a client: then the host's setting decides.");
            allowedOrigins = Config.Bind("Security", "AllowedOrigins", OriginPolicy.Default,
                "Web pages allowed to connect (comma separated, ':*' = any port, '*' = any page).");

            if (!enabledCfg.Value)
            {
                Logger.LogInfo("NOPlannerLink disabled in config");
                return;
            }
            var policy = new OriginPolicy(allowedOrigins.Value);
            try
            {
                server = new WsServer(port.Value, policy.Allowed, msg => Logger.LogInfo(msg));
                server.Start();
                Logger.LogInfo($"NOPlannerLink listening on ws://localhost:{port.Value}");
            }
            catch (Exception e)
            {
                Logger.LogError($"NOPlannerLink could not listen on port {port.Value}: {e.Message}");
                server = null;
                return;
            }
            gate = new HostGate(allowEverything, Logger);
            world = new GameWorld(gate);
            link = new LinkService(server, intelDefault.Value == "All" ? IntelMode.All : IntelMode.Side, rateHz.Value, Version);
            rateHz.SettingChanged += (_, __) => link.IntervalS = 1.0 / rateHz.Value;
        }

        private void Update()
        {
            if (link == null || gate == null || world == null) return;
            // Never let a game-API change break the game: log (rate-limited) and carry on.
            try
            {
                gate.Tick();
                link.Tick(Time.unscaledTimeAsDouble, world);
            }
            catch (Exception e)
            {
                if (Time.unscaledTime >= nextErrorLog)
                {
                    nextErrorLog = Time.unscaledTime + 10f;
                    Logger.LogError("NOPlannerLink update failed: " + e);
                }
            }
        }

        private void OnDestroy() => server?.Dispose();
        private void OnApplicationQuit() => server?.Dispose();
    }
}
