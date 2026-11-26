using System.Collections.Generic;
using System.Linq;
using BepInEx.Configuration;
using BepInEx.Logging;
using Mirage;
using Mirage.Serialization;
using NuclearOption.Networking;

namespace NOPlannerLink
{
    /// <summary>Client → host: "I run NOPlannerLink" (sent once per connection).</summary>
    public struct NoplHello { public byte Version; }

    /// <summary>Host → modded client: whether "Everything" intel is allowed in this session.</summary>
    public struct NoplPermission { public bool AllowAll; }

    /// <summary>
    /// Decides whether "Everything" intel (units outside your faction's database) may be shown.
    /// Mod-manager policy: such features only work when the HOST runs the mod and enabled them.
    ///  - Single-player: allowed.
    ///  - Host: allowed when its config AllowEverything = true.
    ///  - Client: allowed only after the host's mod sent NoplPermission { AllowAll = true }.
    /// Unmodded players are never sent anything: the host replies only to clients that said hello
    /// (Mirage logs unknown messages and charges a small error cost, so we avoid sending them).
    /// </summary>
    internal sealed class HostGate
    {
        private readonly ConfigEntry<bool> allowEverything;
        private readonly ManualLogSource log;
        private object? serverRegisteredOn, clientRegisteredOn;
        private INetworkPlayer? helloSentFor;
        private readonly HashSet<INetworkPlayer> moddedClients = new HashSet<INetworkPlayer>();
        private bool lastAnnounced;
        private bool permissionFromHost;

        public bool IsHost { get; private set; }
        public bool AllowAll { get; private set; }

        static HostGate()
        {
            // The game's Mirage weaver doesn't process this DLL, so register serializers by hand.
            Writer<NoplHello>.Write = (w, m) => w.WriteByte(m.Version);
            Reader<NoplHello>.Read = r => new NoplHello { Version = r.ReadByte() };
            Writer<NoplPermission>.Write = (w, m) => w.WriteByte(m.AllowAll ? (byte)1 : (byte)0);
            Reader<NoplPermission>.Read = r => new NoplPermission { AllowAll = r.ReadByte() != 0 };
            MessagePacker.RegisterMessage<NoplHello>();
            MessagePacker.RegisterMessage<NoplPermission>();
        }

        public HostGate(ConfigEntry<bool> allowEverything, ManualLogSource log)
        {
            this.allowEverything = allowEverything;
            this.log = log;
        }

        public void Tick()
        {
            var state = GameManager.gameState;
            if (!NetworkManagerNuclearOption.IsLoaded)
            {
                IsHost = false;
                AllowAll = state == GameState.SinglePlayer;
                return;
            }
            var nm = NetworkManagerNuclearOption.i;
            var server = nm.Server;
            var client = nm.Client;

            if (server != null && server.Active)
            {
                IsHost = true;
                if (!ReferenceEquals(serverRegisteredOn, server.MessageHandler))
                {
                    server.MessageHandler.RegisterHandler<NoplHello>(OnHello, false);
                    serverRegisteredOn = server.MessageHandler;
                    moddedClients.Clear();
                }
                var allow = state == GameState.SinglePlayer || allowEverything.Value;
                AllowAll = allow;
                moddedClients.RemoveWhere(p => !server.AllPlayers.Contains(p));
                if (allow != lastAnnounced)
                {
                    lastAnnounced = allow;
                    foreach (var p in moddedClients.ToArray()) SendPermission(p);
                }
                return;
            }

            IsHost = false;
            lastAnnounced = false;
            if (client != null && client.IsConnected && client.Player != null && client.Player.IsAuthenticated)
            {
                if (!ReferenceEquals(clientRegisteredOn, client.MessageHandler))
                {
                    client.MessageHandler.RegisterHandler<NoplPermission>(OnPermission, false);
                    clientRegisteredOn = client.MessageHandler;
                }
                if (!ReferenceEquals(helloSentFor, client.Player))
                {
                    permissionFromHost = false;
                    helloSentFor = client.Player;
                    client.Send(new NoplHello { Version = 1 });
                    log.LogInfo("Asked host whether 'Everything' intel is allowed");
                }
                AllowAll = permissionFromHost;
            }
            else
            {
                helloSentFor = null;
                permissionFromHost = false;
                AllowAll = state == GameState.SinglePlayer;
            }
        }

        private void OnHello(INetworkPlayer player, NoplHello msg)
        {
            moddedClients.Add(player);
            SendPermission(player);
        }

        private void SendPermission(INetworkPlayer player)
        {
            var allow = GameManager.gameState == GameState.SinglePlayer || allowEverything.Value;
            player.Send(new NoplPermission { AllowAll = allow });
        }

        private void OnPermission(INetworkPlayer player, NoplPermission msg)
        {
            permissionFromHost = msg.AllowAll;
            log.LogInfo("Host says 'Everything' intel is " + (msg.AllowAll ? "allowed" : "not allowed"));
        }
    }
}
