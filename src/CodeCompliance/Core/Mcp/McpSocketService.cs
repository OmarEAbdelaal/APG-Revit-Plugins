using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace CodeCompliance.Core.Mcp
{
    /// <summary>
    /// TCP JSON-RPC 2.0 endpoint the MCP server (Node.js, launched by Claude) connects to.
    /// Request:  {"jsonrpc":"2.0","method":"command_name","params":{...},"id":"..."}
    /// Response: {"jsonrpc":"2.0","result":...,"id":"..."} or {"jsonrpc":"2.0","error":{...},"id":"..."}
    ///
    /// Commands run on the socket thread; Revit work happens inside the commands through
    /// ExternalEvents (that is how the RevitMCPSDK command base class works), so the service
    /// itself never touches the Revit API after <see cref="Start"/>.
    /// </summary>
    public sealed class McpSocketService
    {
        // JSON-RPC error codes (same values as RevitMCPSDK)
        private const int ParseError = -32700;
        private const int InvalidRequest = -32600;
        private const int MethodNotFound = -32601;
        private const int InternalError = -32603;

        private static McpSocketService? _instance;
        public static McpSocketService Instance => _instance ??= new McpSocketService();

        private TcpListener? _listener;
        private Thread? _listenerThread;
        private volatile bool _isRunning;
        private McpCommandHost? _host;

        private McpSocketService() { }

        public bool IsRunning => _isRunning;
        public int Port { get; private set; } = McpSettings.DefaultPort;
        public int CommandCount => _host?.Count ?? 0;
        public McpCommandHost? Host => _host;
        public DateTime? StartedAt { get; private set; }
        public int RequestsServed { get; private set; }

        /// <summary>
        /// Loads the command sets and starts listening. Must be called in a Revit API
        /// context (IExternalCommand or a Revit event) because commands create ExternalEvents.
        /// Throws SocketException when the port is already in use.
        /// </summary>
        public void Start(UIApplication uiApp, McpSettings settings)
        {
            if (_isRunning)
                return;

            McpPaths.EnsureDirectories();
            string revitVersion = uiApp.Application.VersionNumber;
            var host = new McpCommandHost(revitVersion);
            host.LoadAll(uiApp, settings);
            _host = host;

            IPAddress address = settings.AllowRemoteConnections ? IPAddress.Any : IPAddress.Loopback;
            var listener = new TcpListener(address, settings.Port);
            listener.Start();
            _listener = listener;
            Port = settings.Port;
            _isRunning = true;
            StartedAt = DateTime.Now;
            RequestsServed = 0;

            _listenerThread = new Thread(ListenForClients) { IsBackground = true, Name = "RevitMCP-Listener" };
            _listenerThread.Start();
            McpLog.Info("Socket service started on " + address + ":" + Port + " with " + host.Count + " commands");
        }

        public void Stop()
        {
            if (!_isRunning)
                return;
            _isRunning = false;
            try
            {
                _listener?.Stop();
            }
            catch
            {
                // ignore
            }
            _listener = null;
            if (_listenerThread != null && _listenerThread.IsAlive)
                _listenerThread.Join(1000);
            _listenerThread = null;
            McpLog.Info("Socket service stopped");
        }

        private void ListenForClients()
        {
            try
            {
                while (_isRunning && _listener != null)
                {
                    TcpClient client = _listener.AcceptTcpClient();
                    var worker = new Thread(HandleClient) { IsBackground = true, Name = "RevitMCP-Client" };
                    worker.Start(client);
                }
            }
            catch (SocketException)
            {
                // listener stopped
            }
            catch (ObjectDisposedException)
            {
                // listener stopped
            }
            catch (Exception ex)
            {
                McpLog.Error("Listener failed", ex);
            }
        }

        private void HandleClient(object? state)
        {
            var client = (TcpClient)state!;
            try
            {
                using (NetworkStream stream = client.GetStream())
                {
                    var pending = new StringBuilder();
                    var buffer = new byte[16384];
                    while (_isRunning && client.Connected)
                    {
                        int read;
                        try
                        {
                            read = stream.Read(buffer, 0, buffer.Length);
                        }
                        catch (IOException)
                        {
                            break;
                        }
                        if (read == 0)
                            break;

                        pending.Append(Encoding.UTF8.GetString(buffer, 0, read));

                        // A request may arrive in several TCP chunks and a client may send several
                        // requests: cut complete top-level JSON values off the front of the buffer.
                        string message;
                        while (TryTakeJson(pending, out message))
                        {
                            string response = Process(message);
                            byte[] bytes = Encoding.UTF8.GetBytes(response);
                            stream.Write(bytes, 0, bytes.Length);
                            stream.Flush();
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                McpLog.Error("Client connection failed", ex);
            }
            finally
            {
                try
                {
                    client.Close();
                }
                catch
                {
                    // ignore
                }
            }
        }

        /// <summary>Removes and returns the first complete JSON object/array from the buffer.</summary>
        internal static bool TryTakeJson(StringBuilder pending, out string json)
        {
            json = "";
            int depth = 0;
            bool inString = false, escape = false, started = false;
            for (int i = 0; i < pending.Length; i++)
            {
                char c = pending[i];
                if (inString)
                {
                    if (escape)
                        escape = false;
                    else if (c == '\\')
                        escape = true;
                    else if (c == '"')
                        inString = false;
                    continue;
                }
                if (c == '"')
                {
                    inString = true;
                    started = true;
                }
                else if (c == '{' || c == '[')
                {
                    depth++;
                    started = true;
                }
                else if (c == '}' || c == ']')
                {
                    depth--;
                    if (depth == 0 && started)
                    {
                        json = pending.ToString(0, i + 1).Trim();
                        pending.Remove(0, i + 1);
                        return true;
                    }
                }
            }
            return false;
        }

        private string Process(string requestJson)
        {
            string? id = null;
            try
            {
                JObject request;
                try
                {
                    request = JObject.Parse(requestJson);
                }
                catch (JsonException)
                {
                    return Error(null, ParseError, "Invalid JSON");
                }

                JToken? idToken = request["id"];
                id = idToken == null || idToken.Type == JTokenType.Null ? null : idToken.ToString();
                string? method = request.Value<string>("method");
                if (request.Value<string>("jsonrpc") != "2.0" || string.IsNullOrEmpty(method))
                    return Error(id, InvalidRequest, "Invalid JSON-RPC request");

                McpCommandHost? host = _host;
                if (host == null || !host.Contains(method!))
                {
                    string available = host == null ? "(none)" : string.Join(", ", host.CommandNames);
                    return Error(id, MethodNotFound, "Method " + method + " not found. Available: " + available);
                }

                JObject parameters = request["params"] as JObject ?? new JObject();
                McpLog.Info("-> " + method);
                try
                {
                    JToken result = host.Execute(method!, parameters, id ?? "");
                    RequestsServed++;
                    var ok = new JObject { ["jsonrpc"] = "2.0", ["result"] = result, ["id"] = id };
                    return ok.ToString(Formatting.None);
                }
                catch (Exception ex)
                {
                    McpLog.Error("Command " + method + " failed", ex);
                    int code = InternalError;
                    JToken? data = null;
                    // RevitMCPSDK.CommandExecutionException carries ErrorCode / ErrorData
                    PropertyInfo? codeProp = ex.GetType().GetProperty("ErrorCode");
                    if (codeProp != null && codeProp.GetValue(ex) is int c)
                        code = c;
                    object? errorData = ex.GetType().GetProperty("ErrorData")?.GetValue(ex);
                    if (errorData != null)
                    {
                        try
                        {
                            data = JToken.FromObject(errorData);
                        }
                        catch
                        {
                            data = errorData.ToString();
                        }
                    }
                    return Error(id, code, ex.Message, data);
                }
            }
            catch (Exception ex)
            {
                McpLog.Error("Request processing failed", ex);
                return Error(id, InternalError, "Internal error: " + ex.Message);
            }
        }

        private static string Error(string? id, int code, string message, JToken? data = null)
        {
            var error = new JObject { ["code"] = code, ["message"] = message };
            if (data != null)
                error["data"] = data;
            var response = new JObject { ["jsonrpc"] = "2.0", ["error"] = error, ["id"] = id };
            return response.ToString(Formatting.None);
        }
    }
}
