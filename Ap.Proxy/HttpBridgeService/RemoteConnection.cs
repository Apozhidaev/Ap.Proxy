﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using Ap.Logs;
using Ap.Proxy.Loggers;

namespace Ap.Proxy.HttpBridgeService
{
    public class RemoteConnection : IDisposable
    {
        private const int Threshold = 100;

        private static readonly Dictionary<string, RemoteConnection> Connections =
            new Dictionary<string, RemoteConnection>();

        private readonly byte[] _buffer;
        private readonly IPEndPoint _endPoint;
        private readonly Socket _socket;
        private DateTime _lastActive = DateTime.Now;
        private readonly string _id;

        private RemoteConnection(string connectionId, string host, int port)
        {
            IPAddress address = Dns.GetHostAddresses(host)[0];
            _endPoint = new IPEndPoint(address, port);
            _socket = new Socket(_endPoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            _socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, 1);
            _buffer = new byte[_socket.ReceiveBufferSize];
            _id = connectionId;
        }

        public string Id
        {
            get { return _id; }
        }

        public byte[] Buffer
        {
            get { return _buffer; }
        }

        #region Handshake

        public Task<string> HandshakeAsync()
        {
            _lastActive = DateTime.Now;
            var tcsHandshake = new TaskCompletionSource<string>();
            _socket.BeginConnect(_endPoint, ar =>
            {
                _lastActive = DateTime.Now;
                try
                {
                    _socket.EndConnect(ar);
                    tcsHandshake.SetResult(Id);
                }
                catch (Exception e)
                {
                    Log.Out.Error(e, _id, "HandshakeAsync");
                    tcsHandshake.SetException(e);
                }
            }, null);
            return tcsHandshake.Task;
        }

        #endregion

        #region RelayTo

        public Task<int> RelayToAsync(byte[] buffer)
        {
            _lastActive = DateTime.Now;
            var tcsRelayTo = new TaskCompletionSource<int>();
            _socket.BeginSend(buffer, 0, buffer.Length, SocketFlags.None, ar =>
            {
                _lastActive = DateTime.Now;
                try
                {
                    int ret = _socket.EndSend(ar);
                    tcsRelayTo.SetResult(ret);
                }
                catch (Exception e)
                {
                    Log.Out.Error(e, _id, "RelayToAsync");
                    tcsRelayTo.SetException(e);
                }
            }, null);
            return tcsRelayTo.Task;
        }

        #endregion

        #region RelayFrom

        public Task<int> RelayFromAsync()
        {
            _lastActive = DateTime.Now;
            var tcsRelayFrom = new TaskCompletionSource<int>();
            try
            {
                _socket.BeginReceive(_buffer, 0, _buffer.Length, SocketFlags.None, ar =>
                {
                    _lastActive = DateTime.Now;
                    try
                    {
                        int ret = _socket.EndReceive(ar);
                        tcsRelayFrom.SetResult(ret);
                    }
                    catch (Exception e)
                    {
                        Log.Out.Error(e, _id, "RelayFromAsync");
                        tcsRelayFrom.SetException(e);
                    }
                }, null);
            }
            catch (Exception e)
            {
                Log.Out.Error(e, _id, "RelayFromAsync");
                tcsRelayFrom.SetException(e);
            }
            return tcsRelayFrom.Task;
        }

        #endregion

        public void Dispose()
        {
            lock (Connections)
            {
                Connections.Remove(Id);
            }
            _socket.Shutdown(SocketShutdown.Both);
            _socket.Dispose();
        }

        public static RemoteConnection Open(string connectionId, string host, int port)
        {
            var connection = new RemoteConnection(connectionId, host, port);
            lock (Connections)
            {
                if (Connections.Count > Threshold)
                {
                    DateTime expDate = DateTime.Now.AddMinutes(-10);
                    IEnumerable<string> removeKeys =
                        Connections.Where(c => c.Value._lastActive < expDate).Select(c => c.Key);
                    foreach (string removeKey in removeKeys)
                    {
                        Connections[removeKey].Dispose();
                        Connections.Remove(removeKey);
                    }
                }
                Connections.Add(connection.Id, connection);
            }
            return connection;
        }

        public static RemoteConnection Find(string id)
        {
            lock (Connections)
            {
                if (Connections.ContainsKey(id))
                {
                    return Connections[id];
                }
                return null;
            }
        }
    }
}