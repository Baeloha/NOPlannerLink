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

            foreach (var u in UnitRegistry.allUnits.ToArray())
            {
                if (u == null || u.disabled) continue;
                var unitHq = u.NetworkHQ;
                var own = hasHq && unitHq == hq;
                var neutral = unitHq == null;
                var pos = u.GlobalPosition();
                var detected = false;
                if (intel == IntelMode.Side && !own && !neutral)
                {
                    if (!hasHq || !hq!.trackingDatabase.TryGetValue(u.persistentID, out var info) || info == null) continue;
                    pos = info.lastKnownPosition;
                    detected = true;
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
            return t;
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
