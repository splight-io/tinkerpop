﻿#region License

/*
 * Licensed to the Apache Software Foundation (ASF) under one
 * or more contributor license agreements.  See the NOTICE file
 * distributed with this work for additional information
 * regarding copyright ownership.  The ASF licenses this file
 * to you under the Apache License, Version 2.0 (the
 * "License"); you may not use this file except in compliance
 * with the License.  You may obtain a copy of the License at
 *
 *     http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing,
 * software distributed under the License is distributed on an
 * "AS IS" BASIS, WITHOUT WARRANTIES OR CONDITIONS OF ANY
 * KIND, either express or implied.  See the License for the
 * specific language governing permissions and limitations
 * under the License.
 */

#endregion

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Gremlin.Net.Driver.Exceptions;
using Gremlin.Net.Process;
using Polly;

namespace Gremlin.Net.Driver
{
    internal class ConnectionPool : IDisposable
    {
        private const int ConnectionIndexOverflowLimit = int.MaxValue - 1000000;
        
        private readonly IConnectionFactory _connectionFactory;
        private readonly CopyOnWriteCollection<IConnection> _connections = new CopyOnWriteCollection<IConnection>();

        private readonly ConcurrentDictionary<IConnection, byte> _deadConnections =
            new ConcurrentDictionary<IConnection, byte>();
        private readonly int _poolSize;
        private readonly int _maxInProcessPerConnection;
        private int _connectionIndex;
        private int _poolState;
        private const int PoolIdle = 0;
        private const int PoolPopulationInProgress = 1;

        public ConnectionPool(IConnectionFactory connectionFactory, ConnectionPoolSettings settings)
        {
            _connectionFactory = connectionFactory;
            _poolSize = settings.PoolSize;
            _maxInProcessPerConnection = settings.MaxInProcessPerConnection;
            ReplaceDeadConnectionsAsync().WaitUnwrap();
        }
        
        public int NrConnections => _connections.Count;

        public IConnection GetAvailableConnection()
        {
            var connection = Policy.Handle<ServerUnavailableException>()
                .WaitAndRetry(3, attempt => TimeSpan.FromSeconds(Math.Pow(2, attempt)))
                .Execute(GetConnectionFromPool);

            return ProxiedConnection(connection);
        }

        /// <summary>
        ///     Replaces dead connections.
        /// </summary>
        /// <returns>True if the pool was repaired, false if repairing was not necessary.</returns>
        private async Task<bool> EnsurePoolIsHealthyAsync()
        {
            if (_deadConnections.IsEmpty) return false;
            var poolState = Interlocked.CompareExchange(ref _poolState, PoolPopulationInProgress, PoolIdle);
            if (poolState == PoolPopulationInProgress) return false;
            try
            {
                await ReplaceDeadConnectionsAsync().ConfigureAwait(false);
            }
            finally
            {
                // We need to remove the PoolPopulationInProgress flag again even if an exception occurred, so we don't block the pool population for ever
                Interlocked.CompareExchange(ref _poolState, PoolIdle, PoolPopulationInProgress);
            }

            return true;
        }
        
        private async Task ReplaceDeadConnectionsAsync()
        {
            RemoveDeadConnections();

            await FillPoolAsync().ConfigureAwait(false);
        }

        private void RemoveDeadConnections()
        {
            if (_deadConnections.IsEmpty) return;
            
            foreach (var deadConnection in _deadConnections.Keys)
            {
                if (_connections.TryRemove(deadConnection))
                {
                    DefinitelyDestroyConnection(deadConnection);
                }
            }

            _deadConnections.Clear();
        }
        
        private async Task FillPoolAsync()
        {
            var nrConnectionsToCreate = _poolSize - _connections.Count;
            var connectionCreationTasks = new List<Task<IConnection>>(nrConnectionsToCreate);
            try
            {
                for (var i = 0; i < nrConnectionsToCreate; i++)
                {
                    connectionCreationTasks.Add(CreateNewConnectionAsync());
                }

                var createdConnections = await Task.WhenAll(connectionCreationTasks).ConfigureAwait(false);
                _connections.AddRange(createdConnections);
            }
            catch (Exception)
            {
                // Dispose created connections if the connection establishment failed
                foreach (var creationTask in connectionCreationTasks)
                {
                    if (!creationTask.IsFaulted)
                        creationTask.Result?.Dispose();
                }

                throw;
            }
        }

        private async Task<IConnection> CreateNewConnectionAsync()
        {
            var newConnection = _connectionFactory.CreateConnection();
            await newConnection.ConnectAsync().ConfigureAwait(false);
            return newConnection;
        }

        private IConnection GetConnectionFromPool()
        {
            var connections = _connections.Snapshot;
            if (connections.Length == 0) throw new ServerUnavailableException();
            return TryGetAvailableConnection(connections);
        }
        
        private IConnection TryGetAvailableConnection(IConnection[] connections)
        {
            var index = Interlocked.Increment(ref _connectionIndex);
            ProtectIndexFromOverflowing(index);

            var closedConnections = 0;
            for (var i = 0; i < connections.Length; i++)
            {
                var connection = connections[(index + i) % connections.Length];
                if (connection.NrRequestsInFlight >= _maxInProcessPerConnection) continue;
                if (!connection.IsOpen)
                {
                    ReplaceConnection(connection);
                    closedConnections++;
                    continue;
                }
                return connection;
            }

            if (connections.Length > closedConnections) 
            {
                throw new ConnectionPoolBusyException(_poolSize, _maxInProcessPerConnection);
            }
            else
            {
                throw new ServerUnavailableException();
            }
        }

        private void ProtectIndexFromOverflowing(int currentIndex)
        {
            if (currentIndex > ConnectionIndexOverflowLimit)
                Interlocked.Exchange(ref _connectionIndex, 0);
        }

        private void ReplaceConnection(IConnection connection)
        {
            RemoveConnectionFromPool(connection);
            TriggerReplacementOfDeadConnections();
        }
        
        private void RemoveConnectionFromPool(IConnection connection)
        {
            _deadConnections.TryAdd(connection, 0);
        }

        private void TriggerReplacementOfDeadConnections()
        {
            ReplaceClosedConnectionsAsync().Forget();
        }

        private async Task ReplaceClosedConnectionsAsync()
        {
            var poolWasPopulated = await EnsurePoolIsHealthyAsync().ConfigureAwait(false);
            // Another connection could have been removed already, check if another population is necessary
            if (poolWasPopulated)
                await ReplaceClosedConnectionsAsync().ConfigureAwait(false);
        }

        private IConnection ProxiedConnection(IConnection connection)
        {
            return new ProxyConnection(connection, ReplaceConnectionIfItWasClosed);
        }

        private void ReplaceConnectionIfItWasClosed(IConnection connection)
        {
            if (connection.IsOpen) return;
            ReplaceConnection(connection);
        }

        private async Task CloseAndRemoveAllConnectionsAsync()
        {
            foreach (var connection in _connections.RemoveAndGetAll())
            {
                await connection.CloseAsync().ConfigureAwait(false);
                DefinitelyDestroyConnection(connection);
            }
        }

        private void DefinitelyDestroyConnection(IConnection connection)
        {
            connection.Dispose();
        }

        #region IDisposable Support

        private bool _disposed;

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                    CloseAndRemoveAllConnectionsAsync().WaitUnwrap();
                _disposed = true;
            }
        }

        #endregion
    }
}