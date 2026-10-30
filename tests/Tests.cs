using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Threading;
using TailnetMonitor;

static class Tests
{
    static int assertions;
    static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
        assertions++;
    }

    [STAThread]
    static int Main(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        try
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            var task = args.Contains("--live") ? Live() : Run();
            task.ContinueWith(delegate { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); });
            Dispatcher.Run();
            task.GetAwaiter().GetResult();
            Console.WriteLine("PASS: " + assertions + " assertions");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }

    static async Task Run()
    {
        HealthTransitions();
        HistoryAndSettings();
        await PipeProtocol();
        await TcpConnections();
        await DiscoveryAndConcurrency();
    }

    static void HealthTransitions()
    {
        var track = new HealthTrack();
        Check(track.State == "等待探测" && track.FailureRate == null, "No initial success or fake zero latency");
        Check(track.Add(ProbeSample.Failed("timeout")) == HealthChange.None, "First failure is provisional");
        Check(track.Add(ProbeSample.Failed("timeout")) == HealthChange.None, "Second failure is provisional");
        Check(track.Add(ProbeSample.Failed("timeout")) == HealthChange.Failed && track.Down, "Third failure confirms fault");
        var display = new DeviceState { Preference = new DevicePreference() };
        display.Network.Add(ProbeSample.Failed("响应超时（2 秒）"));
        Check(display.NetworkLatency.Contains("超时"), "Failed probes show their reason in place of latency");
        Check(track.Add(ProbeSample.Failed("timeout")) == HealthChange.None, "Fault notification is deduplicated");
        Check(track.Latency == "—", "Timeout is not a zero-latency sample");
        Check(track.Add(ProbeSample.Passed(10, "DERP")) == HealthChange.None && track.Down, "First success is recovery confirmation");
        Check(track.Add(ProbeSample.Passed(0, "direct")) == HealthChange.Recovered && !track.Down, "Second success confirms recovery, real zero is valid");
        Check(track.Add(ProbeSample.Passed(5, "direct")) == HealthChange.None, "Recovery notification is deduplicated");
        track.Add(ProbeSample.Failed("timeout"));
        track.Add(ProbeSample.Failed("timeout"));
        track.BreakStreak();
        Check(track.Add(ProbeSample.Failed("timeout")) == HealthChange.None && !track.Down, "Pause/sleep interrupts consecutive failures");
        Check(track.State == "异常 1/3", "A fresh failure is visible immediately");
        for (int i = 0; i < 60; i++) track.Add(ProbeSample.Passed(i, "direct"));
        Check(track.FailureRate.Value == 0, "Failure rate uses the latest 50 probes");
        var old = ProbeSample.Passed(99, "old");
        old.Time = DateTime.UtcNow.AddMinutes(-31);
        track.Samples.Add(old);
        track.Prune(DateTime.UtcNow);
        Check(!track.Samples.Contains(old), "History is bounded to 30 minutes");
    }

    static void HistoryAndSettings()
    {
        string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "settings-test-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var store = new SettingsStore(path);
            var first = store.Get("account-a", "stable-device");
            Check(first.Port == 0 && !first.Paused, "New peers have automatic network monitoring and no TCP preset");
            first.Port = 22;
            first.Paused = true;
            store.Save();
            first.Port = 443;
            store.Save();
            var reloaded = new SettingsStore(path);
            Check(reloaded.Get("account-a", "stable-device").Port == 443 && reloaded.Get("account-a", "stable-device").Paused, "Settings survive atomic replacement");
            Check(reloaded.Get("account-b", "stable-device").Port == 0, "Preferences are scoped by account");
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    static async Task PipeProtocol()
    {
        string body = new JavaScriptSerializer().Serialize(Status("a", 2));
        var status = await WithReply(body, "200 OK", api => api.StatusAsync(CancellationToken.None));
        Check(status.Self.HostName == "本机" && status.Peer.Count == 2, "UTF-8 JSON travels over a real Windows named pipe");
        Check(status.Self.Address == "100.64.0.1", "IPv4 is preferred over IPv6");
        var reply = await WithReply("{\"LatencySeconds\":0.01234,\"Endpoint\":\"203.0.113.2:41641\"}", "200 OK",
            api => api.PingAsync("100.64.0.2", CancellationToken.None));
        Check(reply.Milliseconds.HasValue && Math.Abs(reply.Milliseconds.Value - 12.34) < 0.001 && reply.Detail.StartsWith("直连"), "Direct route and seconds-to-ms conversion");
        reply = await WithReply("{\"LatencySeconds\":0.2,\"DERPRegionID\":1,\"DERPRegionCode\":\"sin\"}", "200 OK",
            api => api.PingAsync("100.64.0.2", CancellationToken.None));
        Check(reply.Detail == "DERP · sin", "DERP is a successful, classified route");
        reply = await WithReply("{\"LatencySeconds\":0.05,\"PeerRelay\":\"relay:7777\",\"Endpoint\":\"192.0.2.1:7777\"}", "200 OK",
            api => api.PingAsync("100.64.0.2", CancellationToken.None));
        Check(reply.Detail.StartsWith("Peer Relay"), "Peer Relay takes precedence over a raw endpoint");
        reply = await WithReply("{\"Err\":\"no matching peer\"}", "200 OK",
            api => api.PingAsync("100.64.0.2", CancellationToken.None));
        Check(!reply.Milliseconds.HasValue, "Daemon ping errors remain failed samples");
        bool denied = false;
        try { await WithReply("access denied", "403 Forbidden", api => api.StatusAsync(CancellationToken.None)); }
        catch (LocalApiException) { denied = true; }
        Check(denied, "HTTP errors are local service errors");

        var name = "TailnetMonitor.Test." + Guid.NewGuid().ToString("N");
        using (var pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous))
        using (var cancellation = new CancellationTokenSource())
        {
            var waiting = pipe.WaitForConnectionAsync();
            var pending = new LocalApi(name).PingAsync("100.64.0.2", cancellation.Token);
            await waiting;
            cancellation.Cancel();
            bool canceled = false;
            try { await pending; } catch (OperationCanceledException) { canceled = true; }
            Check(canceled, "Cancellation closes pending pipe I/O without creating failed samples");
        }
        name = "TailnetMonitor.Test." + Guid.NewGuid().ToString("N");
        using (var pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous))
        {
            var waiting = pipe.WaitForConnectionAsync();
            var timer = Stopwatch.StartNew();
            var pending = new LocalApi(name).PingAsync("100.64.0.2", CancellationToken.None);
            await waiting;
            var timeout = await pending;
            Check(!timeout.Milliseconds.HasValue && timeout.Detail.Contains("超时") && timer.Elapsed.TotalSeconds < 3, "Unresponsive probes respect the two-second deadline");
        }
    }

    static async Task<T> WithReply<T>(string body, string status, Func<LocalApi, Task<T>> query)
    {
        string name = "TailnetMonitor.Test." + Guid.NewGuid().ToString("N");
        using (var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous))
        {
            var serving = Serve(server, body, status);
            var querying = query(new LocalApi(name));
            await Task.WhenAll(querying, serving);
            return querying.Result;
        }
    }

    static async Task Serve(NamedPipeServerStream server, string body, string status)
    {
        await server.WaitForConnectionAsync();
        string headers = "";
        byte[] buffer = new byte[1024];
        while (!headers.Contains("\r\n\r\n"))
        {
            int count = await server.ReadAsync(buffer, 0, buffer.Length);
            if (count == 0) throw new Exception("Missing request headers");
            headers += Encoding.ASCII.GetString(buffer, 0, count);
        }
        Check(headers.Contains("HTTP/1.0") && headers.Contains("Host: local-tailscaled.sock") && headers.Contains("Connection: close"), "Local API request framing");
        if (headers.Contains("/ping?")) Check(headers.StartsWith("POST ") && headers.Contains("type=disco"), "Ping is a POST DISCO request");
        byte[] response = Encoding.UTF8.GetBytes("HTTP/1.0 " + status + "\r\nContent-Type: application/json\r\n\r\n" + body);
        await server.WriteAsync(response, 0, response.Length);
        server.Disconnect();
    }

    static async Task TcpConnections()
    {
        var server = new TcpListener(IPAddress.Loopback, 0);
        server.Start();
        int port = ((IPEndPoint)server.LocalEndpoint).Port;
        var connected = await LocalApi.TcpAsync("127.0.0.1", port, CancellationToken.None);
        Check(connected.Milliseconds.HasValue, "A listening TCP port is reachable");
        using (var accepted = await server.AcceptTcpClientAsync())
        {
            Check(accepted.GetStream().ReadByte() == -1, "Probes close their TCP connection immediately");
        }
        server.Stop();
        var refused = await LocalApi.TcpAsync("127.0.0.1", port, CancellationToken.None);
        Check(!refused.Milliseconds.HasValue && (refused.Detail.Contains("拒绝") || refused.Detail.Contains("超时")), "A closed port fails with refusal or the OS-level connection deadline");
        using (var canceled = new CancellationTokenSource())
        {
            canceled.Cancel();
            bool threw = false;
            try { await LocalApi.TcpAsync("127.0.0.1", port, canceled.Token); }
            catch (OperationCanceledException) { threw = true; }
            Check(threw, "TCP cancellation is not counted as a failed probe");
        }
    }

    sealed class FakeSource : ITailnetSource
    {
        public TailnetStatus Current = Status("a", 10);
        public Task<TailnetStatus> StatusAsync(CancellationToken token) { return Task.FromResult(Current); }
        public async Task<ProbeSample> PingAsync(string address, CancellationToken token)
        {
            await Task.Delay(address == "100.64.0.2" ? 900 : 10, token);
            return ProbeSample.Passed(12, "直连 · 192.0.2.1:41641");
        }
    }

    static TailnetStatus Status(string account, int count)
    {
        var peers = new Dictionary<string, PeerInfo>();
        for (int i = 0; i < count; i++) peers["public-key-" + i] = new PeerInfo {
            ID = "stable-" + i, HostName = "device-" + i, OS = "linux", Online = i % 2 == 0,
            TailscaleIPs = new[] { "100.64.0." + (i + 2) }
        };
        return new TailnetStatus {
            BackendState = "Running", CurrentTailnet = new TailnetInfo { MagicDNSSuffix = account + ".ts.net" },
            Self = new PeerInfo { ID = "self", HostName = "本机", UserID = 1, Online = true, TailscaleIPs = new[] { "fd7a:115c:a1e0::1", "100.64.0.1" } }, Peer = peers
        };
    }

    static async Task DiscoveryAndConcurrency()
    {
        var path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "engine-test-" + Guid.NewGuid().ToString("N") + ".json");
        var fake = new FakeSource();
        var engine = new MonitorEngine(fake, new SettingsStore(path));
        Exception failure = null;
        try
        {
            fake.Current.Peer["self"] = fake.Current.Self;
            engine.Start();
            await Task.Delay(120);
            Check(engine.Devices.Count == 10, "Automatic discovery includes offline peers but excludes self");
            Check(engine.Devices.First(d => d.Address == "100.64.0.2").Network.Samples.Count == 0, "One slow target remains pending");
            Check(engine.Devices.Where(d => d.Address != "100.64.0.2").All(d => d.Network.Samples.Count == 1), "Nine other devices update before a slow target");
            Check(engine.Devices.All(d => d.Tcp.Samples.Count == 0 && d.TcpState == "未选择服务"), "No preset means no TCP traffic or TCP fault");
            var target = engine.Devices.First(d => d.Id == "stable-1");
            engine.ToggleDevice(target);
            engine.SelectPort(target, 22);
            Check(target.Context == "已暂停" && target.Port == 22, "Preset selection and per-device pause");
            fake.Current.Peer.Remove("public-key-1");
            engine.ApplyStatus(fake.Current);
            Check(!target.Visible && target.Context == "当前不可见" && target.Port == 22, "Disappeared peer is retained without an offline verdict");
            var updated = Status("a", 10);
            updated.Peer["public-key-1"].HostName = "renamed";
            updated.Peer["public-key-1"].TailscaleIPs = new[] { "100.64.9.9" };
            engine.ApplyStatus(updated);
            Check(target.Visible && target.Name == "renamed" && target.Address == "100.64.9.9" && target.Port == 22, "Stable ID retains settings through name and IP changes");
            engine.ApplyStatus(Status("b", 1));
            Check(engine.Devices.Count == 1 && engine.Devices[0].Port == 0 && engine.Events.Count == 0, "Switching account clears visible peers and history");
            engine.ApplyStatus(updated);
            target = engine.Devices.First(d => d.Id == "stable-1");
            Check(target.Port == 22 && target.Preference.Paused, "Returning to an account restores that account's settings");
            target.Network.Add(ProbeSample.Failed("响应超时（2 秒）"));
            target.Network.Add(ProbeSample.Failed("响应超时（2 秒）"));
            target.Network.Add(ProbeSample.Failed("响应超时（2 秒）"));
            Check(engine.FaultCount == 1, "A paused device retains its confirmed fault");
            Check(engine.PauseAllLabel == "继续监测", "A single paused device changes the global action to resume");
            engine.ToggleAll();
            Check(!target.Preference.Paused && target.Context == "" && engine.PauseAllLabel == "暂停全部", "Global resume clears individual pauses");
            engine.ToggleAll();
            Check(engine.Devices.All(d => d.Context == "已暂停"), "Global pause stops all probes");
            Check(engine.FaultCount == 1, "Global pause does not make a confirmed fault look resolved");
            Check(engine.Devices.All(d => d.PauseLabel == "继续"), "Global pause exposes per-device resume");
            engine.ToggleDevice(target);
            Check(target.Context == "" && engine.Devices.Where(d => d != target).All(d => d.Context == "已暂停"), "Resuming one device leaves others paused");
            engine.ToggleAll();
            Check(engine.Devices.All(d => d.Context == ""), "Global resume clears remaining individual pauses");
            engine.Suspend(true);
            Check(engine.Devices.All(d => d.Context == "休眠暂停"), "Suspend cancels pending work");
            engine.Suspend(false);
            engine.LocalProblem("test local service stopped");
            Check(engine.FaultCount == 0 && engine.Devices.All(d => d.Context == "本机服务异常" || d.Context == "已暂停"), "Local service faults do not mark every remote as offline");
            var offlineControl = Status("a", 1);
            offlineControl.Self.Online = false;
            engine.ApplyStatus(offlineControl);
            Check(engine.LocalTitle.Contains("控制面离线") && engine.LocalTone == "#BD7C20", "Offline control plane is shown as a warning despite a running backend");
        }
        catch (Exception ex) { failure = ex; }
        await engine.StopAsync();
        if (File.Exists(path)) File.Delete(path);
        if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    static async Task Live()
    {
        var api = new LocalApi();
        var status = await api.StatusAsync(CancellationToken.None);
        Check(status.BackendState == "Running", "Installed Tailscale daemon is running");
        Console.WriteLine("Live daemon: " + status.Version + "; visible peers: " + status.Peer.Count);
        var peer = status.Peer.Values.FirstOrDefault();
        if (peer != null)
        {
            var result = await api.PingAsync(peer.Address, CancellationToken.None);
            Console.WriteLine("Live target: " + peer.Address + "; result: " + (result.Milliseconds.HasValue ? result.Milliseconds.Value.ToString("0.0") + " ms; " + result.Detail : result.Detail));
            Check(result != null, "Real daemon completes a structured ping without shell parsing");
        }
    }
}
