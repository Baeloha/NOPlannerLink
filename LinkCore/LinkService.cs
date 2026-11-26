using System.Collections.Generic;

namespace NOPlannerLink.Core
{
    /// <summary>Where frames come from: the real game (plugin) or a fake world (harness/tests).</summary>
    public interface IWorldSource
    {
        /// <summary>Session info; decides the effective intel mode (Everything only when allowed).</summary>
        SessionInfo Session(IntelMode requested);

        /// <summary>Fill <paramref name="into"/> with the visible units and append new events. Returns mission time (s).</summary>
        double Capture(IntelMode effective, List<EntityState> into, List<GameEvent> events);

        /// <summary>Ids destroyed so far this mission (sent in every snapshot so a late planner still hides them).</summary>
        IReadOnlyCollection<string> Destroyed { get; }

        /// <summary>
        /// Planner target command: op = add | remove | clear, on the unit with this protocol id.
        /// Only allowed in Detected (Side) intel and only for enemies the local side tracks — the same
        /// targets you could pick on the in-game map. Returns null when done, else the reason it was refused.
        /// </summary>
        string? SelectTarget(string op, string? id, IntelMode effective);

        /// <summary>
        /// The running mission in the game's own mission-file JSON (what the planner loads from a .json),
        /// or null when not in a mission. <paramref name="name"/> = mission name.
        /// </summary>
        string? MissionJson(out string? name);
    }

    /// <summary>
    /// Main-thread driver: reads planner commands, captures a frame at the configured rate and
    /// broadcasts hello / snapshot / delta / event messages. No Unity types, so it is testable.
    /// </summary>
    public sealed class LinkService
    {
        private readonly WsServer server;
        private readonly FrameEncoder encoder = new FrameEncoder();
        private readonly List<EntityState> frame = new List<EntityState>(512);
        private readonly List<GameEvent> events = new List<GameEvent>();
        private double nextAt;
        private bool forceSnapshot = true;

        public IntelMode Requested { get; private set; }
        public double IntervalS { get; set; }

        public LinkService(WsServer server, IntelMode initial, double rateHz, string modVersion)
        {
            this.server = server;
            Requested = initial;
            IntervalS = 1.0 / (rateHz <= 0 ? 4 : rateHz);
            encoder.ModVersion = modVersion;
        }

        /// <summary>Call every game frame with a monotonic clock (seconds).</summary>
        public void Tick(double now, IWorldSource world)
        {
            List<string>? targetCmds = null;
            while (server.Incoming.TryDequeue(out var cmd))
            {
                var type = JsonFields.GetString(cmd, "type");
                if (type == "target") (targetCmds ??= new List<string>()).Add(cmd);
                else if (type == "getMission")
                {
                    // On the game thread: the mission object may only be read here.
                    string? name = null, json = null, error = null;
                    try { json = world.MissionJson(out name); if (json == null) error = "not in a mission"; }
                    catch (System.Exception ex) { error = ex.Message; }
                    server.Broadcast(FrameEncoder.Mission(name, json, error));
                }
                else HandleCommand(cmd);
            }
            if (targetCmds != null)
            {
                // Applied right away (on the game thread) so the next frame already shows the new target list.
                var mode = world.Session(Requested).Intel;
                foreach (var c in targetCmds)
                {
                    var op = JsonFields.GetString(c, "op") ?? "add";
                    var id = JsonFields.GetString(c, "id");
                    string? refused;
                    try { refused = world.SelectTarget(op, id, mode); }
                    catch (System.Exception ex) { refused = "error: " + ex.Message; }
                    server.Broadcast(FrameEncoder.Event(new GameEvent { Kind = refused == null ? "targetSet" : "targetRefused", Target = id, Text = refused ?? op }));
                }
                forceSnapshot = false;
                nextAt = 0;
            }
            if (now < nextAt && !server.ClientJoined && !forceSnapshot) return;
            nextAt = now + IntervalS;

            var session = world.Session(Requested);
            frame.Clear();
            events.Clear();
            // Capture even with no planner connected: destroyed units are detected by diffing frames.
            var t = world.Capture(session.Intel, frame, events);
            if (server.ClientCount == 0) { forceSnapshot = true; return; }

            if (server.ClientJoined || forceSnapshot || encoder.SessionChanged(session))
            {
                server.ClientJoined = false;
                forceSnapshot = false;
                server.Broadcast(encoder.Hello(session));
                server.Broadcast(encoder.Snapshot(t, frame, world.Destroyed));
            }
            else
            {
                var delta = encoder.Delta(t, frame);
                if (delta != null) server.Broadcast(delta);
            }
            foreach (var e in events) server.Broadcast(FrameEncoder.Event(e));
        }

        private void HandleCommand(string json)
        {
            switch (JsonFields.GetString(json, "type"))
            {
                case "setIntel":
                    Requested = JsonFields.GetString(json, "mode") == "all" ? IntelMode.All : IntelMode.Side;
                    forceSnapshot = true;
                    break;
                case "resync":
                    forceSnapshot = true;
                    break;
            }
        }
    }
}
