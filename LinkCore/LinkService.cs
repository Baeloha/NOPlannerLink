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
            while (server.Incoming.TryDequeue(out var cmd)) HandleCommand(cmd);
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
