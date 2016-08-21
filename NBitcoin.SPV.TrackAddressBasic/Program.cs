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

            var parameters = new NodeConnectionParameters();

            // ping pong
            parameters.TemplateBehaviors.FindOrCreate<PingPongBehavior>();

            // chain behavior keep chain in sync
            var chain = new ConcurrentChain(network);
            parameters.TemplateBehaviors.Add(new ChainBehavior(chain));

            // tracker behavior tracks our addresses
            var tracker = new Tracker();
            var trackerBehavior = new TrackerBehavior(tracker, chain);
            parameters.TemplateBehaviors.Add(trackerBehavior);

            // initial scan location
            var scanLocation = new BlockLocator();
            scanLocation.Blocks.Add(network.GetGenesis().GetHash());

            // connect node 
            var node = Node.Connect(network, peer, parameters);
            trackerBehavior.Attach(node); // TODO: not sure why we need to do this manually

            // debug fluff
            node.MessageReceived += (node1, message) =>
            {
                if (message.Message.Command != "headers" && message.Message.Command != "merkleblock")
                    Console.WriteLine(message.Message.Command);
            };

            // setup tracker with our addresse
            node.StateChanged += (node1, oldState) =>
            {
                if (node1.State == NodeState.HandShaked)
                {
                    trackerBehavior.Scan(scanLocation, DateTimeOffset.MinValue);
                    trackerBehavior.SendMessageAsync(new MempoolPayload());

                    tracker.Add(key.ScriptPubKey);

                    trackerBehavior.RefreshBloomFilter();
                }
            };

            node.Disconnected += (Node node1) =>
            {
                Console.WriteLine("Disconnected!");
            };

            // more debug fluff
            tracker.NewOperation += (Tracker sender, Tracker.IOperation trackerOperation) =>
            {
                Console.WriteLine("tracker operation: {0}", trackerOperation.ToString());
            };

            // do node handshake
            node.VersionHandshake();
            Console.WriteLine("Successful handshake with {0}", peer.Endpoint);

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
    }
}
