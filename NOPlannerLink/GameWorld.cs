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
        // Jamming: who last jammed each radar unit (the in-game map's JAMMED marker + line), and who jams me.
        private readonly Dictionary<Unit, System.Action<Unit.JamEventArgs>> jamHooks = new Dictionary<Unit, System.Action<Unit.JamEventArgs>>();
        private readonly Dictionary<Unit, (Unit jammer, float t)> jammedBy = new Dictionary<Unit, (Unit, float)>();
        private readonly Dictionary<Unit, float> jammingMe = new Dictionary<Unit, float>();

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
                UnhookJamming();
                events.Add(new GameEvent { Kind = "missionLoaded", Text = mission?.Name });
            }

            double t = Time.timeSinceLevelLoad;
            var hasHq = GameManager.GetLocalHQ(out var hq) && hq != null;
            GameManager.GetLocalAircraft(out var myAircraft);
            var cur = new Dictionary<string, Unit>(prev.Count + 16);
            HookRadarWarnings(myAircraft);
            // Missiles my missile warning has picked up (same list that drives the in-game warning and map flash).
            HashSet<Unit>? incoming = null;
            var mw = myAircraft != null ? myAircraft.GetMissileWarningSystem() : null;
            if (mw != null && mw.knownMissiles.Count > 0)
            {
                incoming = new HashSet<Unit>();
                foreach (var m in mw.knownMissiles) if (m != null && !m.disabled) incoming.Add(m);
            }
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
                var incomingOnMe = incoming != null && incoming.Contains(u);
                var hasRadar = u.radar is Radar;
                if ((hasRadar || ReferenceEquals(u, myAircraft)) && !jamHooks.ContainsKey(u)) HookJamming(u);
                if (intel == IntelMode.Side && !own && !neutral)
                {
                    if (hasHq && hq!.trackingDatabase.TryGetValue(u.persistentID, out var info) && info != null)
                    {
                        pos = info.lastKnownPosition;
                        detected = true;
                    }
                    // The game draws the radar line to the emitter's true position even when it isn't tracked.
                    // Missiles your warning system sees coming at you are shown too (the game flags them on the map).
                    else if (radarOnMe == 0 && !incomingOnMe) continue;
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
                e.IncomingOnMe = incomingOnMe;
                if (hasRadar && ((Radar)u.radar).IsJammed())
                {
                    e.Jammed = true;
                    if (jammedBy.TryGetValue(u, out var jb) && jb.jammer != null && !jb.jammer.disabled && now - jb.t < 5f) e.JammedBy = IdOf(jb.jammer);
                }
                if (jammingMe.TryGetValue(u, out var jt) && now - jt < 2f) e.JammingMe = true;
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
            // Like the in-game map, only draw a jamming line from a jammer you can see.
            foreach (var e in into) if (e.JammedBy != null && !cur.ContainsKey(e.JammedBy)) e.JammedBy = null;
            if (pings.Count > 0)
            {
                List<Unit>? old = null;
                foreach (var kv in pings) if (kv.Key == null || kv.Value.until <= now) (old ??= new List<Unit>()).Add(kv.Key!);
                if (old != null) foreach (var u in old) pings.Remove(u);
            }
            return t;
        }

        private void HookJamming(Unit u)
        {
            System.Action<Unit.JamEventArgs> h = a =>
            {
                if (a.jammingUnit == null) return;
                jammedBy[u] = (a.jammingUnit, Time.timeSinceLevelLoad);
                if (ReferenceEquals(u, hooked)) jammingMe[a.jammingUnit] = Time.timeSinceLevelLoad;
            };
            u.onJam += h;
            jamHooks[u] = h;
        }

        private void UnhookJamming()
        {
            foreach (var kv in jamHooks) if (kv.Key != null) kv.Key.onJam -= kv.Value;
            jamHooks.Clear();
            jammedBy.Clear();
            jammingMe.Clear();
        }

        public string? SelectTarget(string op, string? id, IntelMode mode)
        {
            if (mode != IntelMode.Side) return "target selection only works in Detected mode";
            if (!GameManager.gameState.IsSingleOrMultiplayer()) return "not in a mission";
            if (!GameManager.GetLocalAircraft(out var me) || me == null || me.disabled) return "you are not flying an aircraft";
            var hud = SceneSingleton<CombatHUD>.i;
            if (hud == null || !ReferenceEquals(hud.aircraft, me) || me.weaponManager == null) return "the cockpit HUD isn't ready";
            if (op == "clear")
            {
                hud.DeselectAll(true);
                return null;
            }
            if (id == null || !prev.TryGetValue(id, out var u) || u == null || u.disabled) return "unit not found";
            if (!GameManager.GetLocalHQ(out var hq) || hq == null) return "you have no side yet";
            if (u.NetworkHQ == null || u.NetworkHQ == hq) return "only enemy units can be targeted";
            if (!hq.trackingDatabase.ContainsKey(u.persistentID)) return "your side hasn't detected it";
            var wm = me.weaponManager;
            if (op == "remove")
            {
                if (!wm.CheckIsTarget(u)) return null;
                var map = SceneSingleton<DynamicMap>.i;
                if (map != null && DynamicMap.TryGetMapIcon(u, out _)) map.DeselectIcon(u);
                if (wm.CheckIsTarget(u)) hud.DeSelectUnit(u);
                return null;
            }
            if (wm.CheckIsTarget(u)) return null;
            var selector = SceneSingleton<TargetListSelector>.i;
            if (selector != null && selector.CheckExclusions(u)) return "the selected weapon can't target it";
            hud.SelectUnit(u);
            return wm.CheckIsTarget(u) ? null : "the game didn't accept it (no HUD marker for it yet)";
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
