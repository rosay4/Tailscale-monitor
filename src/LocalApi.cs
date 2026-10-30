using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Net;
using System.Net.Sockets;
using System.Security.Principal;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace TailnetMonitor
{
    public sealed class TailnetInfo
    {
        public string Name { get; set; }
        public string MagicDNSSuffix { get; set; }
    }

    public sealed class PeerInfo
    {
        public string ID { get; set; }
        public string HostName { get; set; }
        public string DNSName { get; set; }
        public string OS { get; set; }
        public long UserID { get; set; }
        public string[] TailscaleIPs { get; set; }
        public bool Online { get; set; }
        public bool Active { get; set; }

        public string Address
        {
            get
            {
                var ips = TailscaleIPs ?? new string[0];
                foreach (var ip in ips) if (ip.IndexOf(':') < 0) return ip;
                return ips.Length == 0 ? "" : ips[0];
            }
        }
    }

    public sealed class TailnetStatus
    {
        public string Version { get; set; }
        public string BackendState { get; set; }
        public PeerInfo Self { get; set; }
        public TailnetInfo CurrentTailnet { get; set; }
        public Dictionary<string, PeerInfo> Peer { get; set; }
        public string[] Health { get; set; }

        public string ProfileKey
        {
            get
            {
                if (Self == null) return "";
                return Self.UserID + ":" + (CurrentTailnet == null ? "" : CurrentTailnet.MagicDNSSuffix);
            }
        }
    }

    public sealed class PingReply
    {
        public string Err { get; set; }
        public double LatencySeconds { get; set; }
        public string Endpoint { get; set; }
        public string PeerRelay { get; set; }
        public int DERPRegionID { get; set; }
        public string DERPRegionCode { get; set; }
    }

    public sealed class LocalApiException : Exception
    {
        public LocalApiException(string message) : base(message) { }
    }

    public interface ITailnetSource
    {
        Task<TailnetStatus> StatusAsync(CancellationToken token);
        Task<ProbeSample> PingAsync(string address, CancellationToken token);
    }

    public sealed class LocalApi : ITailnetSource
    {
        public const string PipeName = @"ProtectedPrefix\Administrators\Tailscale\tailscaled";
        readonly string pipeName;

        public LocalApi(string pipeName = PipeName) { this.pipeName = pipeName; }

        public async Task<TailnetStatus> StatusAsync(CancellationToken token)
        {
            return await RequestAsync<TailnetStatus>("GET", "/localapi/v0/status", 2500, token);
        }

        public async Task<ProbeSample> PingAsync(string address, CancellationToken token)
        {
            try
            {
                var reply = await RequestAsync<PingReply>("POST", "/localapi/v0/ping?ip=" +
                    Uri.EscapeDataString(address) + "&type=disco", 2000, token);
                if (!String.IsNullOrEmpty(reply.Err)) return ProbeSample.Failed(reply.Err);
                var route = !String.IsNullOrEmpty(reply.PeerRelay) ? "Peer Relay · " + reply.PeerRelay :
                    reply.DERPRegionID != 0 ? "DERP · " + reply.DERPRegionCode :
                    !String.IsNullOrEmpty(reply.Endpoint) ? "直连 · " + reply.Endpoint : "未知路径";
                return ProbeSample.Passed(reply.LatencySeconds * 1000, route);
            }
            catch (TimeoutException) { return ProbeSample.Failed("响应超时（2 秒）"); }
        }

        async Task<T> RequestAsync<T>(string method, string path, int timeout, CancellationToken token)
        {
            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(token))
            using (var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Identification))
            {
                deadline.CancelAfter(timeout);
                using (deadline.Token.Register(pipe.Dispose))
                {
                    bool connected = false;
                    try
                    {
                        await pipe.ConnectAsync(1000, deadline.Token).ConfigureAwait(false);
                        connected = true;
                        // HTTP/1.0 + close gives an EOF-delimited body, without chunked encoding.
                        var request = Encoding.ASCII.GetBytes(method + " " + path + " HTTP/1.0\r\n" +
                            "Host: local-tailscaled.sock\r\nConnection: close\r\nContent-Length: 0\r\n\r\n");
                        await pipe.WriteAsync(request, 0, request.Length, deadline.Token).ConfigureAwait(false);
                        using (var buffer = new MemoryStream())
                        {
                            await pipe.CopyToAsync(buffer, 4096, deadline.Token).ConfigureAwait(false);
                            deadline.Token.ThrowIfCancellationRequested();
                            return DecodeResponse<T>(buffer.ToArray());
                        }
                    }
                    catch (UnauthorizedAccessException)
                    {
                        throw new LocalApiException("没有访问 Tailscale 本地服务的权限，请检查当前用户权限。");
                    }
                    catch (Exception ex)
                    {
                        if (!(ex is IOException || ex is OperationCanceledException || ex is ObjectDisposedException || ex is TimeoutException)) throw;
                        token.ThrowIfCancellationRequested();
                        if (connected && deadline.IsCancellationRequested) throw new TimeoutException();
                        throw new LocalApiException("无法访问 Tailscale 本地服务，请确认 Tailscale 正在运行。");
                    }
                }
            }
        }

        internal static T DecodeResponse<T>(byte[] response)
        {
            string text = Encoding.UTF8.GetString(response);
            int separator = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            if (separator < 0) throw new LocalApiException("Tailscale 本地接口返回了无效响应。");
            var body = text.Substring(separator + 4);
            var firstLine = text.Substring(0, text.IndexOf("\r\n", StringComparison.Ordinal));
            if (!firstLine.Contains(" 200 "))
                throw new LocalApiException("Tailscale 接口：" + body.Trim());
            return new JavaScriptSerializer().Deserialize<T>(body);
        }

        public static async Task<ProbeSample> TcpAsync(string address, int port, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var ip = IPAddress.Parse(address);
            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(token))
            using (var socket = new Socket(ip.AddressFamily, SocketType.Stream, ProtocolType.Tcp))
            {
                deadline.CancelAfter(2000);
                using (deadline.Token.Register(socket.Dispose))
                {
                    var timer = Stopwatch.StartNew();
                    try
                    {
                        await Task.Factory.FromAsync((callback, state) => socket.BeginConnect(ip, port, callback, state),
                            socket.EndConnect, null).ConfigureAwait(false);
                        token.ThrowIfCancellationRequested();
                        if (deadline.IsCancellationRequested) return ProbeSample.Failed("建连超时（2 秒）");
                        return ProbeSample.Passed(timer.Elapsed.TotalMilliseconds, "TCP " + port);
                    }
                    catch (Exception ex)
                    {
                        if (!(ex is SocketException || ex is ObjectDisposedException)) throw;
                        token.ThrowIfCancellationRequested();
                        var socketError = ex as SocketException;
                        return ProbeSample.Failed(deadline.IsCancellationRequested ? "建连超时（2 秒）" :
                            socketError != null && socketError.SocketErrorCode == SocketError.ConnectionRefused ?
                            "端口拒绝连接" : "TCP 连接失败");
                    }
                }
            }
        }
    }
}
