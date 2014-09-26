using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Text;
using Any.Logs;
using Any.Proxy.Loggers;

namespace Any.Proxy.Http
{
    public class Connection : IDisposable
    {
        private readonly byte[] _buffer = new byte[40960];
        private readonly Action<Connection> _destroyer;
        private readonly Socket _clientSocket;
        private Dictionary<string, string> _headerFields;
        private string _httpQuery = "";
        private string _httpRequestType;
        private string _httpVersion;
        private string _requestedPath;
        private IBridge _bridge;

        private readonly Func<string, string, int, bool, IBridge> _bridgeFactory;
        private readonly string _id;

        public Connection(Socket clientSocket, Func<string, string, int, bool, IBridge> bridgeFactory, Action<Connection> destroyer)
        {
            _httpRequestType = "";
            _httpVersion = "";
            _clientSocket = clientSocket;
            _destroyer = destroyer;
            _bridgeFactory = bridgeFactory;
            _id = Guid.NewGuid().ToString();
        }

        public string Id
        {
            get { return _id; }
        }

        public void Dispose()
        {
            try
            {
                _clientSocket.Shutdown(SocketShutdown.Both);
            }
            catch (Exception e)
            {
                Log.Out.Error(e, _id, "_clientSocket.Shutdown(SocketShutdown.Both)");
            }

            _clientSocket.Close();
            if (_bridge != null)
            {
                _bridge.Dispose();
            }
            _destroyer(this);
        }

        public void StartHandshake()
        {
            try
            {
                _clientSocket.BeginReceive(_buffer, 0, _buffer.Length, SocketFlags.None, OnReceiveQuery, _clientSocket);
            }
            catch (Exception e)
            {
                Log.Out.Error(e, _id, "StartHandshake");
                Dispose();
            }
        }

        private void OnReceiveQuery(IAsyncResult ar)
        {
            int Ret;
            try
            {
                Ret = _clientSocket.EndReceive(ar);
            }
            catch (Exception e)
            {
                Log.Out.Error(e, _id, "OnReceiveQuery");
                Ret = -1;
            }
            if (Ret <= 0)
            {
                //Connection is dead :(
                Dispose();
                return;
            }
            _httpQuery += Encoding.UTF8.GetString(_buffer, 0, Ret);

            //if received data is valid HTTP request...
            if (IsValidQuery(_httpQuery))
            {
                ProcessQuery(_httpQuery);
                //else, keep listening
            }
            else
            {
                try
                {
                    _clientSocket.BeginReceive(_buffer, 0, _buffer.Length, SocketFlags.None, OnReceiveQuery,
                        _clientSocket);
                }
                catch (Exception e)
                {
                    Log.Out.Error(e, _id, "OnReceiveQuery");
                    Dispose();
                }
            }
        }

        private bool IsValidQuery(string query)
        {
            int index = query.IndexOf("\r\n\r\n", StringComparison.InvariantCulture);
            if (index == -1)
            {
                return false;
            }

            _headerFields = ParseQuery(query);
            if (_httpRequestType.ToUpper().Equals("POST"))
            {
                try
                {
                    int length = int.Parse(_headerFields["Content-Length"]);
                    return query.Length >= index + 6 + length;
                }
                catch (Exception e)
                {
                    Log.Out.Error(e, _id, "IsValidQuery");
                    SendBadRequest();
                    return true;
                }
            }
            return true;
        }

        private void ProcessQuery(string query)
        {
            _headerFields = ParseQuery(query);
            if (_headerFields == null || !_headerFields.ContainsKey("Host"))
            {
                Log.Out.Error(_id, "ProcessQuery");
                SendBadRequest();
                return;
            }
            int port;
            string host;
            int ret;
            if (_httpRequestType.ToUpper().Equals("CONNECT"))
            {
                //HTTPS
                ret = _requestedPath.IndexOf(":", StringComparison.InvariantCulture);
                if (ret >= 0)
                {
                    host = _requestedPath.Substring(0, ret);
                    port = _requestedPath.Length > ret + 1 ? int.Parse(_requestedPath.Substring(ret + 1)) : 443;
                }
                else
                {
                    host = _requestedPath;
                    port = 443;
                }
            }
            else
            {
                ret = _headerFields["Host"].IndexOf(":", StringComparison.Ordinal);
                if (ret > 0)
                {
                    host = _headerFields["Host"].Substring(0, ret);
                    port = int.Parse(_headerFields["Host"].Substring(ret + 1));
                }
                else
                {
                    host = _headerFields["Host"];
                    port = 80;
                }
            }
            try
            {

                _bridge = _bridgeFactory(Id, host, port, _headerFields.ContainsKey("Proxy-Connection") && _headerFields["Proxy-Connection"].ToLower().Equals("keep-alive"));
                _bridge.HandshakeAsync().ContinueWith(_ =>
                {
                    if (_.Exception != null)
                    {
                        Log.Out.Error(_.Exception, _id, "ProcessQuery");
                        SendBadRequest();
                        return;
                    }
                    if (_httpRequestType.ToUpper().Equals("CONNECT"))
                    {
                        //HTTPS
                        string rq = _httpVersion + " 200 Connection established\r\nProxy-Agent: Any Proxy Server\r\n\r\n";
                        _clientSocket.WriteAsync(Encoding.UTF8.GetBytes(rq))
                            .ContinueWith(__ => _bridge.RelayAsync().ContinueWith(___ => Dispose()));
                    }
                    else
                    {
                        _bridge.WriteAsync(Encoding.UTF8.GetBytes(_httpQuery))
                            .ContinueWith(__ => _bridge.RelayFromAsync().ContinueWith(___ => Dispose()));
                    }
                });
            }
            catch
            {
                SendBadRequest();
            }
        }

        private Dictionary<string, string> ParseQuery(string query)
        {
            var retdict = new Dictionary<string, string>();
            string[] lines = query.Replace("\r\n", "\n").Split('\n');
            int cnt, ret;
            //Extract requested URL
            if (lines.Length > 0)
            {
                //Parse the Http Request Type
                ret = lines[0].IndexOf(' ');
                if (ret > 0)
                {
                    _httpRequestType = lines[0].Substring(0, ret);
                    lines[0] = lines[0].Substring(ret).Trim();
                }
                //Parse the Http Version and the Requested Path
                ret = lines[0].LastIndexOf(' ');
                if (ret > 0)
                {
                    _httpVersion = lines[0].Substring(ret).Trim();
                    _requestedPath = lines[0].Substring(0, ret);
                }
                else
                {
                    _requestedPath = lines[0];
                }
                //Remove http:// if present
                if (_requestedPath.Length >= 7 && _requestedPath.Substring(0, 7).ToLower().Equals("http://"))
                {
                    ret = _requestedPath.IndexOf('/', 7);
                    _requestedPath = ret == -1 ? "/" : _requestedPath.Substring(ret);
                }
            }
            for (cnt = 1; cnt < lines.Length; cnt++)
            {
                ret = lines[cnt].IndexOf(":", StringComparison.InvariantCulture);
                if (ret > 0 && ret < lines[cnt].Length - 1)
                {
                    try
                    {
                        retdict.Add(lines[cnt].Substring(0, ret), lines[cnt].Substring(ret + 1).Trim());
                    }
                    catch (Exception e)
                    {
                        Log.Out.Error(e, _id, "ParseQuery");
                    }
                }
            }
            return retdict;
        }

        private void SendBadRequest()
        {
            var brs = new StringBuilder();
              brs.Append("HTTP/1.1 400 Bad Request\r\nConnection: close\r\nContent-Type: text/html\r\n\r\n");
              brs.Append("<html>");
              brs.Append("<head><title>400 Bad Request</title></head>");
              brs.Append("<body><div align=\"center\">");
              brs.Append("<table border=\"0\" cellspacing=\"3\" cellpadding=\"3\" bgcolor=\"#90d5ec\"><tr><td>");
              brs.Append("<table border=\"0\" width=\"500\" cellspacing=\"3\" cellpadding=\"3\"><tr><td bgcolor=\"#e9f7fb\">");
              brs.Append("<tr><td bgcolor=\"#e9f7fb\">");
              brs.Append("<p align=\"center\"><strong><font size=\"2\" face=\"Verdana\">400 Bad Request</font></strong></p>");
              brs.Append("</td></tr>");
              brs.Append("<tr><td bgcolor=\"#e9f7fb\">");
              brs.Append("<font size=\"2\" face=\"Verdana\"> The proxy server could not understand the HTTP request!<br><br> Please contact your network administrator about this problem.</font>");
              brs.Append("</td></tr></table>");
              brs.Append("</td></tr></table>");
              brs.Append("</div></body>");
              brs.Append("</html>");
            _clientSocket.WriteAsync(Encoding.UTF8.GetBytes(brs.ToString())).ContinueWith(_ => Dispose());
        }
    }
}