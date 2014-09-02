﻿#region License
// 
//     CoiniumServ - Crypto Currency Mining Pool Server Software
//     Copyright (C) 2013 - 2014, CoiniumServ Project - http://www.coinium.org
//     http://www.coiniumserv.com - https://github.com/CoiniumServ/CoiniumServ
// 
//     This software is dual-licensed: you can redistribute it and/or modify
//     it under the terms of the GNU General Public License as published by
//     the Free Software Foundation, either version 3 of the License, or
//     (at your option) any later version.
// 
//     This program is distributed in the hope that it will be useful,
//     but WITHOUT ANY WARRANTY; without even the implied warranty of
//     MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
//     GNU General Public License for more details.
//    
//     For the terms of this license, see licenses/gpl_v3.txt.
// 
//     Alternatively, you can license this software under a commercial
//     license or white-label it as set out in licenses/commercial.txt.
// 
#endregion
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using CoiniumServ.Daemon;
using CoiniumServ.Daemon.Exceptions;
using CoiniumServ.Daemon.Responses;
using CoiniumServ.Miners;
using CoiniumServ.Payments;
using CoiniumServ.Persistance.Blocks;
using CoiniumServ.Persistance.Providers;
using CoiniumServ.Persistance.Providers.MySql;
using CoiniumServ.Persistance.Providers.Redis;
using CoiniumServ.Pools;
using CoiniumServ.Server.Mining.Stratum;
using CoiniumServ.Shares;
using CoiniumServ.Utils.Extensions;
using CoiniumServ.Utils.Helpers.Time;
using Dapper;
using Serilog;

namespace CoiniumServ.Persistance.Layers.Hybrid
{
    public class HybridStorageLayer : IStorageLayer
    {
        public bool IsEnabled { get; private set; }
        public bool SupportsShareStorage { get { return true; } }
        public bool SupportsBlockStorage { get { return true; } }
        public bool SupportsPaymentsStorage { get { return true; } }

        private readonly IDaemonClient _daemonClient;

        private readonly ILogger _logger;

        private readonly IRedisProvider _redisProvider;

        private readonly IMySqlProvider _mySqlProvider;

        private readonly string _coin;

        public HybridStorageLayer(IEnumerable<IStorageProvider> providers, IDaemonClient daemonClient, IPoolConfig poolConfig)
        {
            _daemonClient = daemonClient;
            _logger = Log.ForContext<HybridStorageLayer>().ForContext("Component", poolConfig.Coin.Name);
            _coin = poolConfig.Coin.Name.ToLower(); // pool's associated coin name.

            // try loading providers.
            try
            {
                foreach (var provider in providers)
                {
                    if (provider is IRedisProvider)
                        _redisProvider = (IRedisProvider) provider;
                    else if (provider is IMySqlProvider)
                        _mySqlProvider = (IMySqlProvider) provider;
                }

                IsEnabled = (_redisProvider != null && _mySqlProvider != null);
            }
            catch (Exception e)
            {
                _logger.Error("Error initializing hybrid storage layer; {0:l}", e.Message);
            }
        }

        public void AddShare(IShare share)
        {
            try
            {
                if (!IsEnabled || !_redisProvider.IsConnected)
                    return;

                //_client.StartPipe(); // batch the commands.

                // add the share to round 
                var currentKey = string.Format("{0}:shares:round:current", _coin);
                _redisProvider.Client.HIncrByFloat(currentKey, share.Miner.Username, share.Difficulty);

                // increment shares stats.
                var statsKey = string.Format("{0}:stats", _coin);
                _redisProvider.Client.HIncrBy(statsKey, share.IsValid ? "validShares" : "invalidShares", 1);

                // add to hashrate
                if (share.IsValid)
                {
                    var hashrateKey = string.Format("{0}:hashrate", _coin);
                    var entry = string.Format("{0}:{1}", share.Difficulty, share.Miner.Username);
                    _redisProvider.Client.ZAdd(hashrateKey, Tuple.Create(TimeHelpers.NowInUnixTime(), entry));
                }

                //_client.EndPipe(); // execute the batch commands.
            }
            catch (Exception e)
            {
                _logger.Error("An exception occured while comitting share: {0:l}", e.Message);
            }
        }

        public void RemoveShares(IPaymentRound round)
        {
            try
            {
                if (!IsEnabled || !_redisProvider.IsConnected)
                    return;

                var roundKey = string.Format("{0}:shares:round:{1}", _coin, round.Block.Height);

                _redisProvider.Client.Del(roundKey); // delete the associated shares.            
            }
            catch (Exception e)
            {
                _logger.Error("An exception occured while deleting shares: {0:l}", e.Message);
            }
        }

        public void MoveShares(IShare share)
        {
            try
            {
                if (!IsEnabled || !_redisProvider.IsConnected)
                    return;

                if (!share.IsBlockAccepted)
                    return;

                // rename round.
                var currentKey = string.Format("{0}:shares:round:current", _coin);
                var roundKey = string.Format("{0}:shares:round:{1}", _coin, share.Height);
                _redisProvider.Client.Rename(currentKey, roundKey);
            }
            catch (Exception e)
            {
                _logger.Error("An exception occured while moving shares for new block: {0:l}", e.Message);
            }
        }

        public void MoveSharesToCurrentRound(IPaymentRound round)
        {
            try
            {
                if (!IsEnabled || !_redisProvider.IsConnected)
                    return;

                if (round.Block.Status == BlockStatus.Confirmed || round.Block.Status == BlockStatus.Pending)
                    return;

                var currentRound = string.Format("{0}:shares:round:current", _coin); // current round key.
                var roundShares = string.Format("{0}:shares:round:{1}", _coin, round.Block.Height); // rounds shares key.

                //_client.StartPipeTransaction(); // batch the commands as atomic.

                // add shares to current round again.
                foreach (var pair in round.Shares)
                {
                    _redisProvider.Client.HIncrByFloat(currentRound, pair.Key, pair.Value);
                }

                _redisProvider.Client.Del(roundShares); // delete the associated shares.

                //_client.EndPipe(); // execute the batch commands.
            }
            catch (Exception e)
            {
                _logger.Error("An exception occured while moving shares: {0:l}", e.Message);
            }
        }

        public Dictionary<string, double> GetCurrentShares()
        {
            var shares = new Dictionary<string, double>();

            try
            {
                if (!IsEnabled || !_redisProvider.IsConnected)
                    return shares;

                var key = string.Format("{0}:shares:round:{1}", _coin, "current");
                var hashes = _redisProvider.Client.HGetAll(key);

                foreach (var hash in hashes)
                {
                    shares.Add(hash.Key, double.Parse(hash.Value, CultureInfo.InvariantCulture));
                }
            }
            catch (Exception e)
            {
                _logger.Error("An exception occured while getting shares for current round: {0:l}", e.Message);
            }

            return shares;
        }

        public Dictionary<uint, Dictionary<string, double>> GetShares(IList<IPaymentRound> rounds)
        {
            var sharesForRounds = new Dictionary<UInt32, Dictionary<string, double>>(); // dictionary of block-height <-> shares.

            try
            {
                if (!IsEnabled || !_redisProvider.IsConnected)
                    return sharesForRounds;

                foreach (var round in rounds)
                {
                    var roundKey = string.Format("{0}:shares:round:{1}", _coin, round.Block.Height);
                    var hashes = _redisProvider.Client.HGetAll(roundKey);

                    var shares = hashes.ToDictionary(x => x.Key, x => double.Parse(x.Value, CultureInfo.InvariantCulture));
                    sharesForRounds.Add(round.Block.Height, shares);
                }
            }
            catch (Exception e)
            {
                _logger.Error("An exception occured while getting shares for round: {0:l}", e.Message);
            }

            return sharesForRounds;
        }

        public void AddBlock(IShare share)
        {
            try
            {
                if (!IsEnabled || !_mySqlProvider.IsConnected)
                    return;

                _mySqlProvider.Connection.Execute(
                    @"insert blocks(height, blockHash, txHash, amount, time) values (@height, @blockHash, @txHash, @amount, @time)",
                    new
                    {
                        height = share.Block.Height,
                        blockHash = share.BlockHash.ToHexString(),
                        txHash = share.Block.Tx.First(),
                        amount = share.GenerationTransaction.TotalAmount,
                        time = share.Block.Time.UnixTimeToDateTime()
                    });
            }
            catch (Exception e)
            {
                _logger.Error("An exception occured while adding block; {0:l}", e.Message);
            }            
        }

        public void UpdateBlock(IPaymentRound round)
        {
            try
            {
                if (!IsEnabled || !_mySqlProvider.IsConnected)
                    return;

                _mySqlProvider.Connection.Execute(
                    @"update blocks orphaned = @orphaned, confirmed = @confirmed where height = @height",
                    new
                    {
                        orphaned = round.Block.Status == BlockStatus.Orphaned,
                        confirmed = round.Block.Status == BlockStatus.Confirmed,
                        height = round.Block.Status
                    });
            }
            catch (Exception e)
            {
                _logger.Error("An exception occured while updating block; {0:l}", e.Message);
            }
        }

        public IDictionary<string, int> GetTotalBlocks()
        {
            var blocks = new Dictionary<string, int> { { "total", 0 }, { "pending", 0 }, { "orphaned", 0 }, { "confirmed", 0 } };

            try
            {
                if (!IsEnabled || !_mySqlProvider.IsConnected)
                    return blocks;

                var result = _mySqlProvider.Connection.Query(@"select count(*),
                (select count(*) from blocks where orphaned = false and confirmed = false) as pending,
                (select count(*) from blocks where orphaned = true) as orphaned,
                (select count(*) from blocks where confirmed = true) as confirmed
                from blocks");

                var data = result.First();
                blocks["pending"] = (int) data.pending;
                blocks["orphaned"] = (int) data.orphaned;
                blocks["confirmed"] = (int) data.confirmed;                
            }
            catch (Exception e)
            {
                _logger.Error("An exception occured while getting block totals: {0:l}", e.Message);
            }

            return blocks;
        }

        public IEnumerable<IPersistedBlock> GetBlocks()
        {
            var blocks = new List<IPersistedBlock>();

            try
            {
                if (!IsEnabled || !_mySqlProvider.IsConnected)
                    return blocks;

                var results = _mySqlProvider.Connection.Query<PersistedBlock>(
                    "select height, orphaned, confirmed, blockHash, txHash, amount, time from blocks order by height DESC LIMIT 20");

                blocks.AddRange(results);
            }
            catch (Exception e)
            {
                _logger.Error("An exception occured while getting blocks: {0:l}", e.Message);
            }

            return blocks;
        }

        public IEnumerable<IPersistedBlock> GetBlocks(BlockStatus status)
        {
            var blocks = new List<IPersistedBlock>();

            try
            {
                if (!IsEnabled || !_mySqlProvider.IsConnected)
                    return blocks;

                string filter = string.Empty;

                switch (status)
                {
                    case BlockStatus.Pending:
                        filter = "orphaned = false and confirmed = false";
                        break;
                    case BlockStatus.Orphaned:
                        filter = "orphaned = true";
                        break;
                    case BlockStatus.Confirmed:
                        filter = "confirmed = true";
                        break;
                }

                var results = _mySqlProvider.Connection.Query<PersistedBlock>(string.Format(
                    "select height, orphaned, confirmed, blockHash, txHash, amount, time from blocks where {0} order by height DESC LIMIT 20", filter));

                blocks.AddRange(results);
            }
            catch (Exception e)
            {
                _logger.Error("An exception occured while getting blocks: {0:l} blocks: {1:l}", status.ToString().ToLower(), e.Message);
            }

            return blocks;
        }

        public Dictionary<string, double> GetPreviousBalances()
        {
            var previousBalances = new Dictionary<string, double>();

            try
            {
                if (!IsEnabled || !_redisProvider.IsConnected)
                    return previousBalances;

                var key = string.Format("{0}:balances", _coin);
                var hashes = _redisProvider.Client.HGetAll(key);
                previousBalances = hashes.ToDictionary(x => x.Key, x => double.Parse(x.Value, CultureInfo.InvariantCulture));
            }
            catch (Exception e)
            {
                _logger.Error("An exception occured while getting previous balances: {0:l}", e.Message);
            }

            return previousBalances;
        }

        public void SetBalances(IList<IWorkerBalance> workerBalances)
        {
            try
            {
                if (!IsEnabled || !_redisProvider.IsConnected)
                    return;

                var balancesKey = string.Format("{0}:balances", _coin);

                foreach (var workerBalance in workerBalances)
                {
                    //_client.StartPipeTransaction(); // batch the commands as atomic.

                    _redisProvider.Client.HDel(balancesKey, workerBalance.Worker); // first delete the existing key.

                    if (!workerBalance.Paid) // if outstanding balance exists, commit it.
                        _redisProvider.Client.HIncrByFloat(balancesKey, workerBalance.Worker, (double)workerBalance.Balance); // increment the value.

                    //_client.EndPipe(); // execute the batch commands.
                }
            }
            catch (Exception e)
            {
                _logger.Error("An exception occured while setting remaining balance: {0:l}", e.Message);
            }
        }

        public bool Authenticate(IMiner miner)
        {
            // within current implementation of hybrid storage layer, we don't have users registered to a pool but they
            // just mine with supplying a valid coin wallet address as username. So we just need to make sure the username
            // is valid address against the coin network.
            try
            {
                return _daemonClient.ValidateAddress(miner.Username).IsValid; // if so validate it against coin daemon as an address.
            }
            catch (RpcException)
            {
                return false;
            }
        }

        public void UpdateDifficulty(IStratumMiner miner)
        {
            // with-in our current hybrid-storage-layer, we don't need to write difficulty to persistance layer.
            return;
        }
    }
}