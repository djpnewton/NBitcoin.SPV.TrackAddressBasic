using System;
using System.IO;
using System.Reflection;
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

        public static string ChainFile => Path.Combine(Path.GetDirectoryName(Assembly.GetEntryAssembly().Location), ChainName());

        static string ChainName()
        {
            if (network == Network.Main)
                return "chainmain.data";
            return "chaintest.data";
        }

        static void Main(string[] args)
        {
            // parse address from command line
            var addr = BitcoinAddress.Create(args[0]);

            // use local trusted node
            NetworkAddress peer = new NetworkAddress(IPAddress.Parse("127.0.0.1"), network == Network.TestNet ? 18333 : 8333); ;

            // initial scan location
            var scanLocation = new BlockLocator();
            scanLocation.Blocks.Add(network.GetGenesis().GetHash());
            var skipBefore = DateTimeOffset.MinValue;
            //skipBefore = DateTimeOffset.Now.AddMonths(-1);

            // load chain
            var chain = new ConcurrentChain(network);
            if (File.Exists(ChainFile))
                chain.Load(File.ReadAllBytes(ChainFile));

            // connect node 
            ConnectNode(addr, peer, chain, scanLocation, skipBefore);

            int chainHeight = 0;
            while (true)
            {
                if (chain.Height != chainHeight)
                {
                    chainHeight = chain.Height;
                    Console.WriteLine("Chain height: {0}", chain.Height);
                    using (var fs = File.Open(ChainFile, FileMode.Create))
                        chain.WriteTo(fs);
                }
                System.Threading.Thread.Sleep(5000);
            }
        }

        private static void ConnectNode(BitcoinAddress addr, NetworkAddress peer, ConcurrentChain chain, BlockLocator scanLocation, DateTimeOffset skipBefore)
        {
            var script = addr.ScriptPubKey; // standard "pay to pubkey hash" script

            var parameters = new NodeConnectionParameters();

            // ping pong
            parameters.TemplateBehaviors.FindOrCreate<PingPongBehavior>();

            // chain behavior keep chain in sync
            parameters.TemplateBehaviors.Add(new ChainBehavior(chain));

            // tracker behavior tracks our address
            parameters.TemplateBehaviors.Add(new TrackerBehavior(new Tracker(), chain));

            var addressManager = new AddressManager();
            addressManager.Add(peer, IPAddress.Loopback);

            parameters.TemplateBehaviors.Add(new AddressManagerBehavior(addressManager));
            var group = new NodesGroup(network, parameters);
            group.AllowSameGroup = true;
            group.MaximumNodeConnection = 1;
            group.Requirements.SupportSPV = true;
            group.Connect();
            group.ConnectedNodes.Added += (s, e) =>
            {
                var node = e.Node;
                node.MessageReceived += (node1, message) =>
                {
                    if (message.Message.Command != "headers" && message.Message.Command != "merkleblock")
                    {
                        if (message.Message.Payload is TxPayload)
                        {
                            var txPayload = (TxPayload) message.Message.Payload;
                            foreach (var output in txPayload.Object.Outputs)
                                if (output.ScriptPubKey == script)
                                    Console.WriteLine("tx {0}", txPayload.Object.GetHash());
                        }
                        else
                            Console.WriteLine(message.Message.Command);
                    }
                };

                node.Disconnected += n =>
                {
                    // TrackerBehavior has probably disconnected the node because of too many false positives...
                    Console.WriteLine("Disconnected!");

                    // save progress
                    var _trackerBehavior = n.Behaviors.Find<TrackerBehavior>();
                    scanLocation = _trackerBehavior.CurrentProgress;
                };

                // start tracker scanning
                var trackerBehavior = node.Behaviors.Find<TrackerBehavior>();
                Console.WriteLine("Tracking {0} ({1})", addr, script);
                trackerBehavior.Tracker.Add(script);
                trackerBehavior.Tracker.NewOperation += (Tracker sender, Tracker.IOperation trackerOperation) =>
                {
                    Console.WriteLine("tracker operation: {0}", trackerOperation.ToString());
                };

                trackerBehavior.Scan(scanLocation, skipBefore);
                trackerBehavior.SendMessageAsync(new MempoolPayload());

                trackerBehavior.RefreshBloomFilter();
            };
        }
    }
}
