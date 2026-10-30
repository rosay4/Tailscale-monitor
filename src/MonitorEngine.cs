using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace TailnetMonitor
{
    // State is owned by the WPF dispatcher; only pipe/socket I/O runs off-thread.
    public sealed class MonitorEngine : INotifyPropertyChanged
    {
        sealed class Worker
        {
            public CancellationTokenSource Stop;
            public Task Task;
        }

        readonly ITailnetSource source;
        readonly SettingsStore settings;
        readonly CancellationTokenSource stop = new CancellationTokenSource();
        readonly Dictionary<string, Worker> workers = new Dictionary<string, Worker>();
        Task discovery;
        string profile = "";
        bool ready;
        bool suspended;
        bool localFault;
        bool controlOnline;

        public ObservableCollection<DeviceState> Devices { get; private set; }
        public readonly List<MonitorEvent> Events = new List<MonitorEvent>();
        public ServiceOption[] Services { get { return ServiceOption.All; } }
        public string LocalTitle { get; private set; }
        public string LocalMessage { get; private set; }
        public string LocalTone { get { return ready && controlOnline ? "#159A77" : "#BD7C20"; } }
        public string SelfDisplay { get; private set; }
        public string TailnetDisplay { get; private set; }
        public bool GlobalPaused { get; private set; }
        public string PauseAllLabel { get { return GlobalPaused || Devices.Any(d => d.Preference.Paused) ? "继续监测" : "暂停全部"; } }
        public int DeviceCount { get { return Devices.Count; } }
        public int HealthyCount { get { return Devices.Count(d => d.Healthy); } }
        public int FaultCount { get { return ready ? Devices.Count(d => d.Visible && (d.Network.Down || (d.Port != 0 && d.Tcp.Down))) : 0; } }
        public string ActivitySummary
        {
            get
            {
                if (suspended) return "电脑休眠，已暂停采样";
                if (GlobalPaused || Devices.Count > 0 && Devices.All(d => d.Preference.Paused)) return "监测已暂停 · 设置与历史保留";
                if (!ready) return "等待本机 Tailscale 就绪";
                if (Devices.Any(d => d.Preference.Paused)) return "部分设备已暂停 · 每 2 秒采样";
                return "正在监测 · 每 2 秒采样";
            }
        }
        public event PropertyChangedEventHandler PropertyChanged;
        public event Action Changed;
        public event Action<string, string, bool> Notification;

        public MonitorEngine(ITailnetSource source, SettingsStore settings)
        {
            this.source = source;
            this.settings = settings;
            Devices = new ObservableCollection<DeviceState>();
            LocalTitle = "连接 Tailscale";
            LocalMessage = settings.Warning ?? "正在自动发现组网设备…";
            SelfDisplay = "本机信息读取中";
            TailnetDisplay = "本地连接监测";
        }

        public void Start() { discovery = DiscoverLoop(); }

        async Task DiscoverLoop()
        {
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    var clock = Stopwatch.StartNew();
                    if (!suspended)
                    {
                        try { ApplyStatus(await source.StatusAsync(stop.Token)); }
                        catch (LocalApiException ex) { LocalProblem(ex.Message); }
                        catch (TimeoutException) { LocalProblem("读取本机 Tailscale 状态超时。"); }
                    }
                    await Task.Delay(Math.Max(20, 5000 - (int)clock.ElapsedMilliseconds), stop.Token);
                }
            }
            catch (OperationCanceledException) { }
        }

        public void ApplyStatus(TailnetStatus status)
        {
            if (status.BackendState != "Running" || status.Self == null)
            {
                string message = status.BackendState == "NeedsLogin" ? "请先在 Tailscale 中登录账号。" :
                    status.BackendState == "Stopped" ? "请在 Tailscale 中连接组网。" :
                    "Tailscale 尚未就绪：" + status.BackendState;
                LocalProblem(message);
                return;
            }
            if (profile != status.ProfileKey)
            {
                foreach (var id in workers.Keys.ToArray()) StopDevice(id);
                Devices.Clear();
                Events.Clear();
                profile = status.ProfileKey;
                GlobalPaused = false;
            }
            ready = true;
            controlOnline = status.Self.Online;
            if (localFault)
            {
                localFault = false;
                Notify("Tailscale 已恢复", "本机服务恢复，继续监测设备。", false);
            }
            LocalTitle = controlOnline ? "Tailscale 已连接" : "Tailscale 控制面离线";
            SelfDisplay = status.Self.HostName + " · " + status.Self.Address;
            TailnetDisplay = status.CurrentTailnet == null ? "当前组网" : status.CurrentTailnet.MagicDNSSuffix;
            LocalMessage = status.Health != null && status.Health.Length > 0 ? String.Join("；", status.Health) :
                status.Self.Online ? "" : "控制面暂时离线，继续检测已有设备。";

            var visibleIds = new HashSet<string>();
            foreach (var peer in (status.Peer ?? new Dictionary<string, PeerInfo>()).Values)
            {
                if (peer.ID == status.Self.ID) continue;
                visibleIds.Add(peer.ID);
                var device = Devices.FirstOrDefault(d => d.Id == peer.ID);
                if (device == null)
                {
                    device = new DeviceState { Id = peer.ID, Preference = settings.Get(profile, peer.ID), ChangePort = SelectPort };
                    Devices.Add(device);
                }
                if (device.Address != peer.Address)
                {
                    StopDevice(device.Id);
                    device.Network.BreakStreak();
                    device.Tcp.BreakStreak();
                }
                device.Name = String.IsNullOrEmpty(peer.HostName) ? peer.DNSName : peer.HostName;
                device.Address = peer.Address;
                device.OS = peer.OS;
                device.ControlOnline = peer.Online;
                device.Visible = true;
            }
            foreach (var device in Devices) if (!visibleIds.Contains(device.Id)) device.Visible = false;
            SynchronizeWorkers();
            Refresh();
        }

        public void LocalProblem(string message)
        {
            bool first = !localFault;
            localFault = true;
            ready = false;
            controlOnline = false;
            LocalTitle = "本机 Tailscale 异常";
            LocalMessage = message;
            SynchronizeWorkers();
            if (first) Notify("本机 Tailscale 异常", message, true);
            Refresh();
        }

        void SynchronizeWorkers()
        {
            foreach (var device in Devices)
            {
                device.GlobalPaused = GlobalPaused;
                device.Context = !device.Visible ? "当前不可见" : suspended ? "休眠暂停" :
                    GlobalPaused || device.Preference.Paused ? "已暂停" : !ready ? "本机服务异常" :
                    String.IsNullOrEmpty(device.Address) ? "等待组网地址" : "";
                if (device.Context.Length != 0) StopDevice(device.Id);
                else if (!workers.ContainsKey(device.Id))
                {
                    var worker = new Worker { Stop = CancellationTokenSource.CreateLinkedTokenSource(stop.Token) };
                    workers.Add(device.Id, worker);
                    worker.Task = ProbeLoop(device, worker.Stop.Token);
                }
                device.Refresh();
            }
        }

        async Task ProbeLoop(DeviceState device, CancellationToken token)
        {
            DateTime previous = DateTime.UtcNow;
            try
            {
                while (!token.IsCancellationRequested)
                {
                    var now = DateTime.UtcNow;
                    if (now - previous > TimeSpan.FromSeconds(6))
                    {
                        device.Network.BreakStreak();
                        device.Tcp.BreakStreak();
                    }
                    previous = now;
                    var timer = Stopwatch.StartNew();
                    await Task.WhenAll(NetworkProbe(device, token), TcpProbe(device, token));
                    await Task.Delay(Math.Max(20, 2000 - (int)timer.ElapsedMilliseconds), token);
                }
            }
            catch (OperationCanceledException) { }
        }

        async Task NetworkProbe(DeviceState device, CancellationToken token)
        {
            try
            {
                device.PingStarted();
                var sample = await source.PingAsync(device.Address, token);
                token.ThrowIfCancellationRequested();
                Record(device, device.Network, sample, "组网");
            }
            catch (LocalApiException ex)
            {
                if (!token.IsCancellationRequested) LocalProblem(ex.Message);
            }
        }

        async Task TcpProbe(DeviceState device, CancellationToken token)
        {
            if (device.Port == 0) return;
            var sample = await LocalApi.TcpAsync(device.Address, device.Port, token);
            token.ThrowIfCancellationRequested();
            Record(device, device.Tcp, sample, "TCP " + device.Port);
        }

        void Record(DeviceState device, HealthTrack track, ProbeSample sample, string kind)
        {
            var change = track.Add(sample);
            if (change != HealthChange.None)
            {
                string message = device.Name + " · " + kind + (change == HealthChange.Failed ? "连接失败" : "连接恢复");
                Events.Insert(0, new MonitorEvent { Time = sample.Time, DeviceId = device.Id, Text = message,
                    Tone = change == HealthChange.Failed ? "#DB5363" : "#159A77" });
                Notify(change == HealthChange.Failed ? "设备连接异常" : "设备连接恢复", message, change == HealthChange.Failed);
            }
            Prune();
            device.Refresh();
            Refresh();
        }

        public void Prune()
        {
            var now = DateTime.UtcNow;
            Events.RemoveAll(e => e.Time < now.AddMinutes(-30));
            foreach (var device in Devices) { device.Network.Prune(now); device.Tcp.Prune(now); }
        }

        void StopDevice(string id)
        {
            Worker worker;
            if (!workers.TryGetValue(id, out worker)) return;
            workers.Remove(id);
            worker.Stop.Cancel();
            worker.Stop.Dispose();
            var device = Devices.FirstOrDefault(d => d.Id == id);
            if (device != null) { device.Network.BreakStreak(); device.Tcp.BreakStreak(); }
        }

        public void SelectPort(DeviceState device, int port)
        {
            if (!ServiceOption.All.Any(s => s.Port == port)) throw new ArgumentOutOfRangeException("port");
            StopDevice(device.Id);
            device.Preference.Port = port;
            device.Tcp = new HealthTrack();
            SaveSettings();
            SynchronizeWorkers();
            Refresh();
        }

        public void ToggleDevice(DeviceState device)
        {
            if (GlobalPaused)
            {
                GlobalPaused = false;
                foreach (var other in Devices) other.Preference.Paused = other != device;
            }
            else device.Preference.Paused = !device.Preference.Paused;
            SaveSettings();
            SynchronizeWorkers();
            Refresh();
        }
        public void ToggleAll()
        {
            if (GlobalPaused || Devices.Any(d => d.Preference.Paused))
            {
                GlobalPaused = false;
                foreach (var device in Devices) device.Preference.Paused = false;
                SaveSettings();
            }
            else GlobalPaused = true;
            SynchronizeWorkers();
            Refresh();
        }
        public void Suspend(bool value)
        {
            suspended = value;
            SynchronizeWorkers();
            Refresh();
        }

        void SaveSettings()
        {
            try { settings.Save(); }
            catch (Exception ex)
            {
                if (!(ex is IOException || ex is UnauthorizedAccessException)) throw;
                LocalMessage = "设置未能保存：" + ex.Message;
            }
        }
        void Notify(string title, string message, bool error)
        { if (Notification != null) Notification(title, message, error); }
        void Refresh()
        {
            if (PropertyChanged != null) PropertyChanged(this, new PropertyChangedEventArgs(null));
            if (Changed != null) Changed();
        }
        public async Task StopAsync()
        {
            var tasks = workers.Values.Select(w => w.Task).ToList();
            if (discovery != null) tasks.Add(discovery);
            stop.Cancel();
            foreach (var id in workers.Keys.ToArray()) StopDevice(id);
            await Task.WhenAll(tasks);
            stop.Dispose();
        }
    }
}
