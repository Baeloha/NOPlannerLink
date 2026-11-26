using System.Collections.Generic;
using NOPlannerLink.Core;
using NuclearOption.Networking;
using UnityEngine;

namespace NOPlannerLink
{
    /// <summary>
    /// Reads the running game (main thread only) into protocol entities.
    /// "Side" intel = own faction + neutral + enemies in the local faction's tracking database
    /// (at their last known position), i.e. what the in-game map shows.
    /// "All" intel = every unit in UnitRegistry (only when HostGate allows it).
    /// </summary>
    internal sealed class GameWorld : IWorldSource
    {
        private readonly HostGate gate;
        private Dictionary<string, Unit> prev = new Dictionary<string, Unit>();
        private readonly HashSet<string> destroyed = new HashSet<string>();
        private object? missionRef;
        // Radar warnings on my aircraft, like the in-game map's radar lines (DynamicMap.ShowRadarPing).
        private Aircraft? hooked;
        private readonly Dictionary<Unit, (int level, float until)> pings = new Dictionary<Unit, (int, float)>();

        public IReadOnlyCollection<string> Destroyed => destroyed;

        public GameWorld(HostGate gate) { this.gate = gate; }

        public SessionInfo Session(IntelMode requested)
        {
            var state = GameManager.gameState;
            var s = new SessionInfo
            {
                InMission = state.IsSingleOrMultiplayer(),
                Role = state == GameState.SinglePlayer ? "single" : state == GameState.Multiplayer ? (gate.IsHost ? "host" : "client") : "menu",
                AllowAll = gate.AllowAll,
            };
            s.Intel = requested == IntelMode.All && s.AllowAll ? IntelMode.All : IntelMode.Side;
            if (!s.InMission) return s;
            s.Mission = MissionManager.CurrentMission?.Name;
            if (NetworkManagerNuclearOption.IsLoaded) s.Map = NetworkManagerNuclearOption.i.MapKey?.ToString();
            if (GameManager.GetLocalHQ(out var hq) && hq != null) s.LocalFaction = hq.faction != null ? hq.faction.factionName : null;
            if (GameManager.GetLocalPlayer<Player>(out var me) && me != null) s.LocalPlayer = me.GetDisplayName(PlayerNameContext.Other);
            return s;
        }

        public double Capture(IntelMode intel, List<EntityState> into, List<GameEvent> events)
        {
            if (!GameManager.gameState.IsSingleOrMultiplayer())
            {
                prev.Clear();
                return 0;
            }
            var mission = MissionManager.CurrentMission;
            if (!ReferenceEquals(mission, missionRef))
            {
                missionRef = mission;
                destroyed.Clear();
                prev.Clear();
                events.Add(new GameEvent { Kind = "missionLoaded", Text = mission?.Name });
            }

            double t = Time.timeSinceLevelLoad;
            var hasHq = GameManager.GetLocalHQ(out var hq) && hq != null;
            GameManager.GetLocalAircraft(out var myAircraft);
            var cur = new Dictionary<string, Unit>(prev.Count + 16);
            HookRadarWarnings(myAircraft);
            var now = Time.timeSinceLevelLoad;

            // Units selected as targets in my aircraft (first = primary).
            Dictionary<Unit, int>? targets = null;
            var wm = myAircraft != null ? myAircraft.weaponManager : null;
            if (wm != null)
            {
                var list = wm.GetTargetList();
                if (list != null && list.Count > 0)
                {
                    targets = new Dictionary<Unit, int>();
                    for (int i = 0; i < list.Count; i++)
                        if (list[i] != null && !targets.ContainsKey(list[i])) targets[list[i]] = i + 1;
                }
            }

            foreach (var u in UnitRegistry.allUnits.ToArray())
            {
                if (u == null || u.disabled) continue;
                var unitHq = u.NetworkHQ;
                var own = hasHq && unitHq == hq;
                var neutral = unitHq == null;
                var pos = u.GlobalPosition();
                var detected = false;
                var radarOnMe = pings.TryGetValue(u, out var ping) && ping.until > now ? ping.level : 0;
                if (intel == IntelMode.Side && !own && !neutral)
                {
                    if (hasHq && hq!.trackingDatabase.TryGetValue(u.persistentID, out var info) && info != null)
                    {
                        pos = info.lastKnownPosition;
                        detected = true;
                    }
                    // The game draws the radar line to the emitter's true position even when it isn't tracked.
                    else if (radarOnMe == 0) continue;
                }

                var id = IdOf(u);
                if (cur.ContainsKey(id)) id += "#" + u.persistentID.Id;
                var e = new EntityState
                {
                    Id = id,
                    Type = u.definition != null ? u.definition.jsonKey ?? "" : "",
                    Kind = KindOf(u),
                    Faction = unitHq != null && unitHq.faction != null ? unitHq.faction.factionName : "Neutral",
                    X = pos.x,
                    Y = pos.y,
                    Z = pos.z,
                    Heading = u.transform.eulerAngles.y,
                    Speed = u.speed,
                    Detected = detected,
                    IsLocal = myAircraft != null && ReferenceEquals(u, myAircraft),
                };
                if (targets != null && targets.TryGetValue(u, out var rank)) e.TargetRank = rank;
                e.RadarOnMe = radarOnMe;
                if (u is Aircraft ac)
                {
                    var pl = ac.Player;
                    if (pl != null) e.Player = pl.GetDisplayName(PlayerNameContext.Other);
                }
                into.Add(e);
                cur[id] = u;

                if (!prev.ContainsKey(id) && u is Missile m && prev.Count > 0)
                {
                    string? target = null;
                    if (UnitRegistry.TryGetUnit(m.targetID, out var tu) && tu != null) target = IdOf(tu);
                    events.Add(new GameEvent { Kind = "launch", T = t, Source = m.owner != null ? IdOf(m.owner) : null, Target = target, Text = e.Type });
                }
            }

            // Units we were showing that left the registry because they were disabled = destroyed.
            foreach (var kv in prev)
            {
                if (cur.ContainsKey(kv.Key)) continue;
                var u = kv.Value;
                if (u == null || !u.disabled || u is Missile) continue;
                if (destroyed.Add(kv.Key)) events.Add(new GameEvent { Kind = "destroyed", T = t, Target = kv.Key, Text = u.definition != null ? u.definition.jsonKey : null });
            }
            prev = cur;
            if (pings.Count > 0)
            {
                List<Unit>? old = null;
                foreach (var kv in pings) if (kv.Key == null || kv.Value.until <= now) (old ??= new List<Unit>()).Add(kv.Key!);
                if (old != null) foreach (var u in old) pings.Remove(u);
            }
            return t;
        }

        private void HookRadarWarnings(Aircraft? me)
        {
            if (ReferenceEquals(me, hooked)) return;
            if (hooked != null) hooked.onRadarWarning -= OnRadarWarning;
            pings.Clear();
            hooked = me;
            if (me != null) me.onRadarWarning += OnRadarWarning;
        }

        private void OnRadarWarning(Aircraft.OnRadarWarning w)
        {
            if (w.emitter == null) return;
            // Same lifetimes as the in-game map line (1 s painted, 2 s detected, 4 s locked) + 0.5 s so a 4 Hz sample sees it.
            int level = w.isTarget ? 3 : w.detected ? 2 : 1;
            float until = Time.timeSinceLevelLoad + (level == 3 ? 4f : level == 2 ? 2f : 1f) + 0.5f;
            if (pings.TryGetValue(w.emitter, out var p) && p.until > Time.timeSinceLevelLoad && p.level > level) level = p.level;
            pings[w.emitter] = (level, until);
        }

        /// <summary>Mission units keep their editor UniqueName (matches the mission JSON); others use the network id.</summary>
        internal static string IdOf(Unit u)
        {
            var n = u.UniqueName;
            if (!string.IsNullOrEmpty(n))
            {
                if (n.StartsWith(Unit.BUILTIN_UNIT_PREFIX)) n = n.Substring(Unit.BUILTIN_UNIT_PREFIX.Length);
                return n;
            }
            return "p:" + u.persistentID.Id;
        }

        private static string KindOf(Unit u)
        {
            switch (u)
            {
                case Aircraft _: return "aircraft";
                case Missile _: return "missile";
                case Ship _: return "ship";
                case GroundVehicle _: return "ground";
                case Building _: return "building";
                case PilotDismounted _: return "pilot";
                default: return "other";
            }
        }
    }
}
