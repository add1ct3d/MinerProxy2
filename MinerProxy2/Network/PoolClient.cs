﻿/* MinerProxy2 programmed by LostSoulfly.
   GNU General Public License v3.0 */

using MinerProxy2.Helpers;
using MinerProxy2.Interfaces;
using MinerProxy2.Miners;
using MinerProxy2.Network.Sockets;
using MinerProxy2.Pools;
using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Timers;

namespace MinerProxy2.Network
{
    public class PoolClient
    {
        private readonly object submittedShareLock = new object();
        private ICoinHandlerMiner coinHandler;
        private Timer getWorkTimer;
        private Timer poolReponseCheckTimer;
        public MinerManager minerManager = new MinerManager();
        public MinerServer minerServer;
        private Client poolClient;
        private bool poolConnected;
        public ICoinHandlerPool poolHandler;
        public PoolInstance poolInstance;
        private Timer statsTimer;
        private List<byte[]> submittedSharesHistory = new List<byte[]>();

        public string currentPoolTarget = string.Empty;
        public string currentPoolWork = string.Empty;
        public dynamic currentPoolWorkDynamic;
        public long acceptedSharesCount { get { return poolInstance.acceptedSharesCount; } set { poolInstance.acceptedSharesCount = value; } }

        public List<string> allowedIPAddresses { get { return poolInstance.allowedIPAddresses; } }
        public string poolEndPoint { get { return poolInstance.GetCurrentPool().poolEndPoint; } }

        public string poolHashrateId { get { return poolInstance.GetCurrentPool().poolHashrateId; } }
        public string poolPassword { get { return poolInstance.GetCurrentPool().poolPassword; } }
        public int poolProtocol { get { return poolInstance.GetCurrentPool().poolProtocol; } }
        public string poolWallet { get { return poolInstance.GetCurrentPool().poolWallet; } }
        
        public string poolWorkerName { get { return poolInstance.GetCurrentPool().poolWorkerName; } }
        public long rejectedSharesCount { get { return poolInstance.rejectedSharesCount; } set { poolInstance.rejectedSharesCount = value; } }

        public long submittedSharesCount { get { return poolInstance.submittedSharesCount; } set { poolInstance.submittedSharesCount = value; } }

        public PoolClient(PoolInstance poolInstance, ICoinHandlerPool pool, ICoinHandlerMiner miner)
        {
            this.poolHandler = pool;
            this.coinHandler = miner;
            this.poolInstance = poolInstance;

            //Create a new instance of the MinerServer, which creates an instance of Network.Sockets.Server
            minerServer = new MinerServer(poolInstance.localListenPort, this, minerManager, coinHandler);

            //Create a new instance of the Network.Sockets.Client
            poolClient = new Client(poolInstance.GetCurrentPool().poolAddress, poolInstance.GetCurrentPool().poolPort);

            //define pool events
            poolClient.OnServerConnected += PoolClient_OnServerConnected;
            poolClient.OnServerDataReceived += PoolClient_OnServerDataReceived;
            poolClient.OnServerDisconnected += PoolClient_OnServerDisconnected;
            poolClient.OnServerError += PoolClient_OnServerError;

            //setup coin miner handler
            coinHandler.SetMinerServer(minerServer);
            coinHandler.SetPoolClient(this);
            coinHandler.SetMinerManager(minerManager);

            //setup coin Pool handler
            poolHandler.SetMinerServer(minerServer);
            poolHandler.SetPoolClient(this);
            poolHandler.SetMinerManager(minerManager);

            //this.Start();
            minerServer.ListenForMiners();

            Log.Information("[{0}] waiting for miners before connecting to pool..", poolWorkerName);
        }

        private void PoolClient_OnServerConnected(object sender, ServerConnectedArgs e)
        {
            Log.Verbose("Pool connected: {0}.", e.socket.RemoteEndPoint.ToString());
            poolInstance.numberOfConnectAttempts = 0;
            poolInstance.poolConnectedTime = DateTime.Now;
            StartPoolStats();
            StartGetWorkTimer();
            StartPoolResponseTimer();
            if (!poolConnected)
            {
                poolConnected = true;
                poolHandler.DoPoolLogin(this);
                poolHandler.DoPoolGetWork(this);
            }
        }

        private void StartPoolResponseTimer()
        {
            int tickRate = 30000;

            poolReponseCheckTimer = new Timer(tickRate);
            poolReponseCheckTimer.AutoReset = true;

            poolReponseCheckTimer.Elapsed += delegate
            {
                minerManager.CheckAndCorrectShareResponseTimes();
            };

            poolReponseCheckTimer.Start();
        }

        private void PoolClient_OnServerDataReceived(object sender, ServerDataReceivedArgs e)
        {
            //Log.Information(Encoding.ASCII.GetString(e.Data));
            poolHandler.PoolDataReceived(e.Data, this);
        }

        private void PoolClient_OnServerDisconnected(object sender, ServerDisonnectedArgs e)
        {
            poolConnected = false;
            Stop();
            IsPoolConnectionRequired();
        }

        private void PoolClient_OnServerError(object sender, ServerErrorArgs e)
        {
            //Log.Error(e.exception, "Server error!");
            poolConnected = false;
            Stop();
            poolInstance.numberOfConnectAttempts++;
            System.Threading.Thread.Sleep(1000);
            if (poolInstance.numberOfConnectAttempts >= 5)
                poolInstance.GetFailoverPool();

            IsPoolConnectionRequired();
        }

        private void StartGetWorkTimer()
        {
            int tickRate = poolInstance.poolGetWorkIntervalInMs;

            getWorkTimer = new Timer(tickRate);
            getWorkTimer.AutoReset = true;

            getWorkTimer.Elapsed += delegate
            {
                //Log.Debug("Requesting work from pool..");
                poolHandler.DoPoolGetWork(this);
            };

            getWorkTimer.Start();
        }

        private void StartPoolStats()
        {
            statsTimer = new Timer(poolInstance.poolStatsIntervalInMs);
            statsTimer.AutoReset = true;

            statsTimer.Elapsed += delegate
            {
                if (IsPoolConnectionRequired() == false)
                    return;

                string hashPrint = minerManager.GetCurrentHashrateReadable();

                if (hashPrint.Length > 0)
                    hashPrint = "Hashrate: " + hashPrint;

                TimeSpan time = poolInstance.poolConnectedTime - DateTime.Now;
                Log.Information("[{0}] {1} Miners: {2} Shares: {3}/{4}/{5} {6}",
                    this.poolWorkerName, time.ToString("hh\\:mm"), minerManager.ConnectedMiners,
                    poolInstance.submittedSharesCount, poolInstance.acceptedSharesCount,
                    poolInstance.rejectedSharesCount, hashPrint);

                lock (minerManager.MinerManagerLock)
                {
                    //Serilog.Log.Information(string.Format("{0, -10} {1, 6} {2, 6} {3, 6} {4, -15}", "MINER", "SUBMIT", "ACCEPT", "REJECT", "HASHRATE"));
                    //Serilog.Log.Information(string.Format("{0, -10} {1, 6} {2, 6} {3, 6} {4, -15}", "-----", "------", "------", "------", "--------"));
                    minerManager.GetMinerList().ForEach<Miner>(m => m.PrintShares(poolWorkerName));
                }
                poolHandler.DoSendHashrate(this);
            };

            statsTimer.Start();
        }

        private void StopGetWorkTimer()
        {
            if (getWorkTimer == null)
                return;

            if (getWorkTimer.Enabled)
                getWorkTimer.Stop();
        }

        private void StopPoolStats()
        {
            if (statsTimer == null)
                return;

            if (statsTimer.Enabled)
                statsTimer.Stop();
        }

        public void ClearSubmittedSharesHistory()
        {
            Log.Verbose("Clearing submitted shares history.");
            lock (submittedShareLock) { submittedSharesHistory.Clear(); }
        }

        public void DoPoolGetWork()
        {
            poolHandler.DoPoolGetWork(this);
        }

        public bool HasShareBeenSubmitted(byte[] share)
        {
            bool submitted;

            lock (submittedShareLock)
            {
                //search the list to see if this share has been
                submitted = submittedSharesHistory.Any(item => item == share);

                //If it wasn't found in the list, we add it
                if (!submitted)
                    submittedSharesHistory.Add(share);
            }

            return submitted;
        }

        public bool IsPoolConnectionRequired()
        {
            Log.Verbose("{0} number of connections: {1}", poolWorkerName, minerServer.GetNumberOfConnections);

            if (poolConnected && minerServer.GetNumberOfConnections == 0)
            {
                Stop();
                Log.Information("[{0}] Waiting for miners..", poolWorkerName);
                return false;
            }

            if (minerServer.GetNumberOfConnections == 0)
            {
                return false;
            }

            if (poolConnected)
                return true;

            Start();
            return true;
        }

        public void SendToPool(byte[] data)
        {
            //Log.Debug("PoolClient SendToPool");
            this.poolClient.SendToPool(data);
        }

        public void SendToPool(string data)
        {
            //Log.Debug("PoolClient SendToPool");
            this.poolClient.SendToPool(data.GetBytes());
        }

        public void Start()
        {
            Log.Information("[{0}] Connecting to {1}.", this.poolWorkerName, this.poolEndPoint);
            poolClient.Connect(poolInstance.GetCurrentPool().poolAddress, poolInstance.GetCurrentPool().poolPort);
        }

        public void Stop()
        {
            StopPoolStats();
            StopGetWorkTimer();
            StopPoolResponseTimer();

            if (poolConnected)
            {
                Log.Information("Disconnecting from {0}.", this.poolEndPoint);
                poolConnected = false;
                currentPoolWork = string.Empty;
                currentPoolTarget = string.Empty;
                currentPoolWorkDynamic = null;
                poolClient.Close();
                return;
            }

            ClearSubmittedSharesHistory();
        }

        private void StopPoolResponseTimer()
        {
            if (poolReponseCheckTimer == null)
                return;

            if (poolReponseCheckTimer.Enabled)
                poolReponseCheckTimer.Stop();
        }

        public void SubmitShareToPool(byte[] data, Miner miner)
        {
            Log.Verbose("{0} submitting share.", miner.workerIdentifier);
            if (submittedSharesHistory.Any(item => item == data))
            {
                Log.Warning("Share already exists, not sending to pool.");
                return;
            }
            poolInstance.submittedSharesCount++;
            minerManager.AddSubmittedShare(miner);
            SendToPool(data);
        }
    }
}