using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace TailnetMonitor
{
    public sealed class PauseDeviceButton : Button
    {
        protected override void OnClick()
        {
            base.OnClick();
            var device = DataContext as DeviceState;
            var window = Window.GetWindow(this);
            var monitor = window == null ? null : window.DataContext as MonitorEngine;
            if (device != null && monitor != null) monitor.ToggleDevice(device);
        }
    }

    public sealed class ProbePulse : FrameworkElement
    {
        public static readonly DependencyProperty SequenceProperty = DependencyProperty.Register("Sequence", typeof(int), typeof(ProbePulse),
            new PropertyMetadata(0, delegate(DependencyObject target, DependencyPropertyChangedEventArgs args)
            {
                if ((int)args.NewValue == 0) return;
                var pulse = (ProbePulse)target;
                pulse.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0.18, TimeSpan.FromMilliseconds(700)));
            }));
        public int Sequence { get { return (int)GetValue(SequenceProperty); } set { SetValue(SequenceProperty, value); } }
        protected override void OnRender(DrawingContext dc)
        {
            dc.DrawEllipse(Brushes.CornflowerBlue, null, new Point(4, 4), 4, 4);
        }
    }

    public sealed class MonitorWindow : Window
    {
        readonly MonitorEngine engine;
        readonly FrameworkElement root;
        readonly DataGrid devices;
        readonly LatencyChart chart;
        string shownEvents = "";

        public MonitorWindow(MonitorEngine engine)
        {
            this.engine = engine;
            Title = "Tailnet 连接监测器";
            Width = Math.Min(1240, SystemParameters.WorkArea.Width - 40);
            Height = Math.Min(900, SystemParameters.WorkArea.Height - 40);
            MinWidth = 940;
            MinHeight = 600;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Background = Brushes.White;
            FontFamily = new FontFamily("Segoe UI, Microsoft YaHei UI");
            UseLayoutRounding = true;
            TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
            using (var stream = typeof(MonitorWindow).Assembly.GetManifestResourceStream("Main.xaml"))
                root = (FrameworkElement)XamlReader.Load(stream);
            Content = root;
            DataContext = engine;
            devices = (DataGrid)root.FindName("Devices");
            chart = (LatencyChart)root.FindName("Chart");
            ((Button)root.FindName("PauseAll")).Click += delegate { engine.ToggleAll(); };
            devices.SelectionChanged += delegate { shownEvents = ""; RefreshDetails(); };
            engine.Changed += Refresh;
        }

        public void Refresh()
        {
            if (devices.SelectedItem == null && engine.Devices.Count > 0) devices.SelectedIndex = 0;
            var empty = (TextBlock)root.FindName("EmptyDevices");
            empty.Visibility = engine.Devices.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            empty.Text = engine.LocalTitle == "Tailscale 已连接" ? "当前账号没有可见的其他设备" : engine.LocalMessage;
            if (IsVisible) RefreshDetails();
        }

        public void RefreshDetails()
        {
            var device = devices.SelectedItem as DeviceState;
            ((TextBlock)root.FindName("SelectedDevice")).Text = device == null ? "选择设备以查看连接详情" : device.Name + " · " + device.Address;
            ((TextBlock)root.FindName("NetworkSummary")).Text = "组网 · " + (device == null ? "等待样本" : device.Network.Summary);
            ((TextBlock)root.FindName("TcpSummary")).Text = "TCP · " + (device == null || device.Port == 0 ? "未选择服务" : device.Tcp.Summary);
            chart.Device = device;
            chart.InvalidateVisual();
            var events = engine.Events.Where(e => device != null && e.DeviceId == device.Id).ToArray();
            string signature = (device == null ? "" : device.Id) + ":" + events.Length + ":" + (events.Length == 0 ? "" : events[0].Time.Ticks.ToString());
            if (signature != shownEvents)
            {
                ((ListBox)root.FindName("EventList")).ItemsSource = events;
                shownEvents = signature;
            }
            ((TextBlock)root.FindName("EmptyEvents")).Visibility = events.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    public sealed class LatencyChart : FrameworkElement
    {
        public DeviceState Device;
        static readonly Brush Blue = MakeBrush("#477AF5");
        static readonly Brush Teal = MakeBrush("#17AF9A");
        static readonly Brush Gray = MakeBrush("#A1ADC0");
        static readonly Pen GridPen = MakePen("#EDF1F7", 1);
        static Brush MakeBrush(string color) { var brush = (SolidColorBrush)new BrushConverter().ConvertFromString(color); brush.Freeze(); return brush; }
        static Pen MakePen(string color, double size) { var pen = new Pen(MakeBrush(color), size); pen.Freeze(); return pen; }
        void Label(DrawingContext dc, string text, double x, double y)
        {
            dc.DrawText(new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                new Typeface("Segoe UI, Microsoft YaHei UI"), 11, Gray, VisualTreeHelper.GetDpi(this).PixelsPerDip), new Point(x, y));
        }
        protected override void OnRender(DrawingContext dc)
        {
            base.OnRender(dc);
            if (ActualWidth < 60 || ActualHeight < 40) return;
            double left = 37, right = ActualWidth - 7, top = 6, bottom = ActualHeight - 23;
            var samples = Device == null ? new ProbeSample[0] : Device.Network.Samples.Concat(Device.Tcp.Samples).ToArray();
            var values = samples.Where(s => s.Milliseconds.HasValue).Select(s => s.Milliseconds.Value).ToArray();
            double maximum = values.Length == 0 ? 100 : Math.Max(10, Math.Ceiling(values.Max() / 10) * 10);
            for (int i = 0; i < 4; i++)
            {
                double y = top + (bottom - top) * i / 3;
                dc.DrawLine(GridPen, new Point(left, y), new Point(right, y));
                Label(dc, (maximum * (3 - i) / 3).ToString("0"), 0, y - 6);
            }
            var now = DateTime.UtcNow;
            var first = samples.Length == 0 ? now.AddMinutes(-1) : samples.Min(s => s.Time);
            var start = now.AddSeconds(-Math.Min(1800, Math.Max(60, (now - first).TotalSeconds)));
            Label(dc, start.ToLocalTime().ToString("HH:mm:ss"), left, bottom + 8);
            Label(dc, "现在 · ms", Math.Max(left + 70, right - 58), bottom + 8);
            if (Device == null || samples.Length == 0)
            {
                Label(dc, "等待探测样本", Math.Max(left + 12, (ActualWidth - 80) / 2), (top + bottom) / 2 - 5);
                return;
            }
            if (values.Length == 0)
            {
                Label(dc, "暂无成功的延迟样本", Math.Max(left + 12, (ActualWidth - 130) / 2), (top + bottom) / 2 - 5);
                return;
            }
            DrawSeries(dc, Device.Network.Samples, Blue, start, now, maximum, left, right, top, bottom);
            if (Device.Port != 0) DrawSeries(dc, Device.Tcp.Samples, Teal, start, now, maximum, left, right, top, bottom);
        }
        static void DrawSeries(DrawingContext dc, List<ProbeSample> samples, Brush brush, DateTime start, DateTime end,
            double maximum, double left, double right, double top, double bottom)
        {
            var pen = new Pen(brush, 1.8);
            Point? previous = null;
            DateTime previousTime = DateTime.MinValue;
            foreach (var sample in samples)
            {
                if (sample.Time < start) continue;
                double x = left + (right - left) * (sample.Time - start).TotalSeconds / (end - start).TotalSeconds;
                if (!sample.Milliseconds.HasValue) { previous = null; continue; }
                double y = bottom - (bottom - top) * sample.Milliseconds.Value / maximum;
                var point = new Point(x, y);
                if (previous.HasValue && sample.Time - previousTime < TimeSpan.FromSeconds(5)) dc.DrawLine(pen, previous.Value, point);
                dc.DrawEllipse(brush, null, point, 1.8, 1.8);
                previous = point;
                previousTime = sample.Time;
            }
        }
    }
}
