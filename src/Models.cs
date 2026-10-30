using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;

namespace TailnetMonitor
{
    public sealed class ProbeSample
    {
        public DateTime Time;
        public double? Milliseconds;
        public string Detail;
        public static ProbeSample Passed(double milliseconds, string detail)
        {
            return new ProbeSample { Time = DateTime.UtcNow, Milliseconds = milliseconds, Detail = detail };
        }
        public static ProbeSample Failed(string detail)
        {
            return new ProbeSample { Time = DateTime.UtcNow, Detail = detail };
        }
    }

    public enum HealthChange { None, Failed, Recovered }

    public sealed class HealthTrack
    {
        public readonly List<ProbeSample> Samples = new List<ProbeSample>();
        public bool Down { get; private set; }
        public int Failures { get; private set; }
        public int Successes { get; private set; }
        public ProbeSample Latest { get; private set; }

        public HealthChange Add(ProbeSample sample)
        {
            Latest = sample;
            Samples.Add(sample);
            Prune(sample.Time);
            if (sample.Milliseconds.HasValue)
            {
                Failures = 0;
                Successes++;
                if (Down && Successes >= 2) { Down = false; return HealthChange.Recovered; }
            }
            else
            {
                Successes = 0;
                Failures++;
                if (!Down && Failures >= 3) { Down = true; return HealthChange.Failed; }
            }
            return HealthChange.None;
        }

        public void Prune(DateTime now) { Samples.RemoveAll(s => s.Time < now.AddMinutes(-30)); }
        public void BreakStreak() { Failures = Successes = 0; Latest = null; }
        public string State
        {
            get
            {
                if (Latest == null) return "等待探测";
                if (Down) return Successes > 0 ? "恢复确认 " + Successes + "/2" : "无法连接";
                return Failures > 0 ? "异常 " + Failures + "/3" : "连接正常";
            }
        }
        public string Tone
        {
            get { return Latest == null ? "#7D8AA0" : Down && Successes == 0 ? "#DB5363" : Failures > 0 || Down ? "#BD7C20" : "#159A77"; }
        }
        public string Latency
        {
            get { return Latest == null || !Latest.Milliseconds.HasValue ? "—" : Latest.Milliseconds.Value.ToString("0.0") + " ms"; }
        }
        public double? FailureRate
        {
            get
            {
                int start = Math.Max(0, Samples.Count - 50), count = Samples.Count - start, failures = 0;
                for (int i = start; i < Samples.Count; i++) if (!Samples[i].Milliseconds.HasValue) failures++;
                return count == 0 ? (double?)null : 100.0 * failures / count;
            }
        }
        public string Summary
        {
            get
            {
                int start = Math.Max(0, Samples.Count - 50), count = Samples.Count - start, good = 0;
                if (count == 0) return "等待样本";
                double total = 0, maximum = 0;
                for (int i = start; i < Samples.Count; i++)
                    if (Samples[i].Milliseconds.HasValue) { double value = Samples[i].Milliseconds.Value; total += value; maximum = Math.Max(maximum, value); good++; }
                return "失败率 " + (100.0 * (count - good) / count).ToString("0.#") + "%  ·  均值 " +
                    (good == 0 ? "—" : (total / good).ToString("0.0") + " ms") + "  ·  最高 " +
                    (good == 0 ? "—" : maximum.ToString("0.0") + " ms");
            }
        }
    }

    public sealed class DevicePreference
    {
        public int Port;
        public bool Paused;
    }

    public sealed class SettingsData
    {
        public Dictionary<string, Dictionary<string, DevicePreference>> Profiles =
            new Dictionary<string, Dictionary<string, DevicePreference>>();
    }

    public sealed class SettingsStore
    {
        readonly string path;
        public SettingsData Data { get; private set; }
        public string Warning { get; private set; }
        public SettingsStore(string path)
        {
            this.path = path;
            Data = new SettingsData();
            if (!File.Exists(path)) return;
            try { Data = new JavaScriptSerializer().Deserialize<SettingsData>(File.ReadAllText(path)); }
            catch (Exception ex)
            {
                if (!(ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException)) throw;
                Warning = "设置读取失败：" + ex.Message;
            }
        }
        public DevicePreference Get(string profile, string id)
        {
            Dictionary<string, DevicePreference> devices;
            if (!Data.Profiles.TryGetValue(profile, out devices))
                Data.Profiles[profile] = devices = new Dictionary<string, DevicePreference>();
            DevicePreference preference;
            if (!devices.TryGetValue(id, out preference)) devices[id] = preference = new DevicePreference();
            return preference;
        }
        public void Save()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var temp = path + ".tmp";
            File.WriteAllText(temp, new JavaScriptSerializer().Serialize(Data));
            if (File.Exists(path)) File.Replace(temp, path, null);
            else File.Move(temp, path);
        }
    }

    public sealed class ServiceOption
    {
        public int Port { get; set; }
        public string Name { get; set; }
        public static readonly ServiceOption[] All = {
            new ServiceOption { Port = 0, Name = "未选择服务" },
            new ServiceOption { Port = 22, Name = "SSH · 22" },
            new ServiceOption { Port = 3389, Name = "远程桌面 · 3389" },
            new ServiceOption { Port = 80, Name = "HTTP · 80" },
            new ServiceOption { Port = 443, Name = "HTTPS · 443" }
        };
    }

    public sealed class DeviceState : INotifyPropertyChanged
    {
        public int ProbeSequence { get; private set; }
        public void PingStarted()
        {
            ProbeSequence++;
            if (PropertyChanged != null) PropertyChanged(this, new PropertyChangedEventArgs("ProbeSequence"));
        }
        public string Id;
        public string Name { get; set; }
        public string Address { get; set; }
        public string OS { get; set; }
        public bool Visible;
        public bool ControlOnline;
        public string Context = "";
        public DevicePreference Preference;
        public readonly HealthTrack Network = new HealthTrack();
        public HealthTrack Tcp = new HealthTrack();
        public Action<DeviceState, int> ChangePort;
        public int Port { get { return Preference.Port; } set { if (value != Port) ChangePort(this, value); } }
        public bool GlobalPaused;
        public string PauseLabel { get { return GlobalPaused || Preference.Paused ? "继续" : "暂停"; } }
        public string NetworkState { get { return Context.Length > 0 ? Context : Network.State; } }
        public string NetworkTone { get { return Context.Length > 0 ? "#7D8AA0" : Network.Tone; } }
        public string NetworkLatency { get { return Context.Length > 0 ? "—" : Network.Latest != null && !Network.Latest.Milliseconds.HasValue ? Network.Latest.Detail : Network.Latency; } }
        public string TcpState { get { return Port == 0 ? "未选择服务" : Context.Length > 0 ? Context : Tcp.State; } }
        public string TcpTone { get { return Port == 0 || Context.Length > 0 ? "#7D8AA0" : Tcp.Tone; } }
        public string TcpLatency { get { return Port == 0 || Context.Length > 0 ? "—" : Tcp.Latest != null && !Tcp.Latest.Milliseconds.HasValue ? Tcp.Latest.Detail : Tcp.Latency; } }
        public string Route { get { return Context.Length == 0 && Network.Latest != null && Network.Latest.Milliseconds.HasValue ? Network.Latest.Detail : "等待路径"; } }
        public string Description { get { return OS + "  ·  " + (ControlOnline ? "控制面在线" : "控制面离线") + "（仅供参考）"; } }
        public bool HasFault { get { return Context.Length == 0 && (Network.Down || (Port != 0 && Tcp.Down)); } }
        public bool Healthy { get { return Context.Length == 0 && Network.Latest != null && Network.Latest.Milliseconds.HasValue && !Network.Down && (Port == 0 || Tcp.Latest != null && Tcp.Latest.Milliseconds.HasValue && !Tcp.Down); } }
        public string LastUpdate { get { return Network.Latest == null ? "尚无样本" : Network.Latest.Time.ToLocalTime().ToString("HH:mm:ss"); } }
        public event PropertyChangedEventHandler PropertyChanged;
        public void Refresh() { if (PropertyChanged != null) PropertyChanged(this, new PropertyChangedEventArgs(null)); }
    }

    public sealed class MonitorEvent
    {
        public DateTime Time;
        public string DeviceId;
        public string Text;
        public string Tone { get; set; }
        public string TimeText { get { return Time.ToLocalTime().ToString("HH:mm:ss"); } }
        public string Message { get { return Text; } }
    }
}
