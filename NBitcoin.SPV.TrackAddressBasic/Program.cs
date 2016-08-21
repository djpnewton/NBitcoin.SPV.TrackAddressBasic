using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Net;
using NBitcoin;
using NBitcoin.Protocol;
using NBitcoin.Protocol.Behaviors;
using NBitcoin.SPV;

namespace NBitcoin.SPV.TrackAddressBasic
{
    class Program
    {
        static Network network = Network.TestNet;

        static void Main(string[] args)
        {
            // parse key from command line
            var key = NBitcoin.Key.Parse(args[0]);

            // connect to local trusted node
            var ipaddr = IPAddress.Parse("127.0.0.1");
            var peer = new NetworkAddress(ipaddr, 18333);

            // initial scan location
            var scanLocation = new BlockLocator();
            scanLocation.Blocks.Add(network.GetGenesis().GetHash());

            // connect node 
            var chain = new ConcurrentChain(network);
            Node node = null;
            NodeEventHandler nodeDisconnect = null;
            nodeDisconnect = (Node node1) =>
            {
                // TrackerBehavior has probably disconnected the node because of too many false positives...
                Console.WriteLine("Disconnected!");

                // save progress
                var trackerBehavior = node1.Behaviors.Find<TrackerBehavior>();
                scanLocation = trackerBehavior.CurrentProgress;

                Task.Run(() =>
                {
                    System.Threading.Thread.Sleep(1000);

                    // reconnect
                    node = ConnectNode(key, peer, chain, scanLocation, nodeDisconnect);
                    HandshakeNode(node, peer);
                });
            };
            node = ConnectNode(key, peer, chain, scanLocation, nodeDisconnect);
            HandshakeNode(node, peer);

            int chainHeight = 0;
            while (true)
            {
                if (chain.Height != chainHeight)
                {
                    chainHeight = chain.Height;
                    Console.WriteLine("Chain height: {0}", chain.Height);
                }
                System.Threading.Thread.Sleep(5000);
            }
        }

        private static Node ConnectNode(Key key, NetworkAddress peer, ConcurrentChain chain, BlockLocator scanLocation, NodeEventHandler nodeDisconnect)
        {
            var parameters = new NodeConnectionParameters();

            // ping pong
            parameters.TemplateBehaviors.FindOrCreate<PingPongBehavior>();

            // chain behavior keep chain in sync
            parameters.TemplateBehaviors.Add(new ChainBehavior(chain));

            // tracker behavior tracks our address
            Console.WriteLine("Tracking {0}", key.PubKey.GetAddress(network));
            var tracker = new Tracker();
            tracker.Add(key.ScriptPubKey);
            var trackerBehavior = new TrackerBehavior(tracker, chain);
            parameters.TemplateBehaviors.Add(trackerBehavior);
            tracker.NewOperation += (Tracker sender, Tracker.IOperation trackerOperation) =>
            {
                Console.WriteLine("tracker operation: {0}", trackerOperation.ToString());
            };

            var node = Node.Connect(network, peer, parameters);
            trackerBehavior.Detach();
            trackerBehavior.Attach(node);

            // debug fluff
            node.MessageReceived += (node1, message) =>
            {
                if (message.Message.Command != "headers" && message.Message.Command != "merkleblock")
                {
                    if (message.Message.Payload is TxPayload)
                    {
                        var txPayload = (TxPayload)message.Message.Payload;
                        Console.WriteLine("tx {0}", txPayload.Object.GetHash());
                    }
                        else
                    Console.WriteLine(message.Message.Command);
                }
            };

            // start tracker scanning
            node.StateChanged += (node1, oldState) =>
            {
                if (node1.State == NodeState.HandShaked)
                {
                    trackerBehavior.Scan(scanLocation, DateTimeOffset.MinValue);
                    trackerBehavior.SendMessageAsync(new MempoolPayload());

                    trackerBehavior.RefreshBloomFilter();
                }
            };

            node.Disconnected += nodeDisconnect;

            return node;
        }

        private static void HandshakeNode(Node node, NetworkAddress peer)
        {
            node.VersionHandshake();
            Console.WriteLine("Successful handshake with {0}", peer.Endpoint);
        }
    }
}
