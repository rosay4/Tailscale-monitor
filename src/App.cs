using System;
using System.IO;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Win32;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

[assembly: TargetFramework(".NETFramework,Version=v4.8")]

namespace TailnetMonitor
{
    public static class Program
    {
        [STAThread]
        public static int Main()
        {
            bool first;
            using (var mutex = new Mutex(true, @"Local\TailnetMonitor.Instance", out first))
            using (var show = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\TailnetMonitor.Show"))
            {
                if (!first) { show.Set(); return 0; }
                var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                var settings = new SettingsStore(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TailnetMonitor", "settings.json"));
                var engine = new MonitorEngine(new LocalApi(), settings);
                var window = new MonitorWindow(engine);
                bool quitting = false;
                Func<Task> quit = async delegate
                {
                    if (quitting) return;
                    quitting = true;
                    await engine.StopAsync();
                    app.Shutdown();
                };
                Action open = delegate { window.Show(); window.WindowState = WindowState.Normal; window.Activate(); window.Refresh(); };
                using (var icon = CreateIcon())
                using (var tray = new Forms.NotifyIcon { Icon = icon, Visible = true, Text = "Tailnet 连接监测器" })
                using (var menu = new Forms.ContextMenuStrip())
                using (var menuFont = new Drawing.Font("Microsoft YaHei UI", 9f))
                {
                    window.Icon = Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty, System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());
                    menu.Font = menuFont;
                    menu.BackColor = Drawing.Color.White;
                    menu.ForeColor = Drawing.Color.FromArgb(30, 30, 30);
                    menu.ShowImageMargin = false;
                    menu.Padding = new Forms.Padding(6, 3, 6, 3);
                    menu.Renderer = new TrayMenuRenderer();
                    menu.SizeChanged += delegate
                    {
                        using (var path = TrayMenuRenderer.RoundBorder(menu.Width, menu.Height))
                        {
                            var previous = menu.Region;
                            menu.Region = new Drawing.Region(path);
                            if (previous != null) previous.Dispose();
                        }
                    };
                    menu.Items.Add("打开监测器", null, delegate { open(); });
                    menu.Items.Add(new Forms.ToolStripSeparator());
                    var pause = menu.Items.Add("暂停全部", null, delegate { engine.ToggleAll(); });
                    menu.Items.Add(new Forms.ToolStripSeparator());
                    menu.Items.Add("退出", null, async delegate { await quit(); });
                    foreach (Forms.ToolStripItem item in menu.Items)
                        if (!(item is Forms.ToolStripSeparator)) { item.Padding = new Forms.Padding(17, 3, 17, 3); item.Margin = new Forms.Padding(0, 1, 0, 1); }
                    tray.ContextMenuStrip = menu;
                    tray.MouseClick += delegate(object sender, Forms.MouseEventArgs e) { if (e.Button == Forms.MouseButtons.Left) open(); };
                    tray.BalloonTipClicked += delegate { open(); };
                    engine.Notification += delegate(string title, string message, bool error)
                    {
                        tray.ShowBalloonTip(5000, title, message, error ? Forms.ToolTipIcon.Warning : Forms.ToolTipIcon.Info);
                    };
                    engine.Changed += delegate
                    {
                        pause.Text = engine.PauseAllLabel;
                        tray.Text = String.Format("Tailnet · {0} 台设备 · {1} 台异常", engine.DeviceCount, engine.FaultCount);
                    };
                    window.Closing += delegate(object sender, System.ComponentModel.CancelEventArgs e)
                    {
                        if (!quitting) { e.Cancel = true; window.Hide(); }
                    };
                    PowerModeChangedEventHandler power = delegate(object sender, PowerModeChangedEventArgs e)
                    {
                        if (e.Mode == PowerModes.Suspend || e.Mode == PowerModes.Resume)
                            app.Dispatcher.BeginInvoke(new Action(delegate { engine.Suspend(e.Mode == PowerModes.Suspend); }));
                    };
                    SystemEvents.PowerModeChanged += power;
                    var registration = ThreadPool.RegisterWaitForSingleObject(show, delegate
                    {
                        app.Dispatcher.BeginInvoke(open);
                    }, null, Timeout.Infinite, false);
                    var pruneTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
                    pruneTimer.Tick += delegate { engine.Prune(); if (window.IsVisible) window.RefreshDetails(); };
                    window.Loaded += delegate { engine.Start(); pruneTimer.Start(); };
                    app.Exit += delegate
                    {
                        pruneTimer.Stop();
                        SystemEvents.PowerModeChanged -= power;
                        registration.Unregister(null);
                        tray.Visible = false;
                    };
                    app.Run(window);
                }
                mutex.ReleaseMutex();
                return 0;
            }
        }

        static Drawing.Icon CreateIcon()
        {
            return Drawing.Icon.ExtractAssociatedIcon(typeof(Program).Assembly.Location);
        }
    }

    sealed class TrayMenuRenderer : Forms.ToolStripProfessionalRenderer
    {
        public static Drawing.Drawing2D.GraphicsPath RoundBorder(int width, int height)
        {
            var path = new Drawing.Drawing2D.GraphicsPath();
            int right = Math.Max(12, width - 1), bottom = Math.Max(12, height - 1);
            path.AddArc(0, 0, 12, 12, 180, 90);
            path.AddArc(right - 12, 0, 12, 12, 270, 90);
            path.AddArc(right - 12, bottom - 12, 12, 12, 0, 90);
            path.AddArc(0, bottom - 12, 12, 12, 90, 90);
            path.CloseFigure();
            return path;
        }

        protected override void OnRenderMenuItemBackground(Forms.ToolStripItemRenderEventArgs e)
        {
            if (!e.Item.Selected) return;
            using (var brush = new Drawing.SolidBrush(Drawing.Color.FromArgb(240, 240, 240)))
                e.Graphics.FillRectangle(brush, 0, 0, e.Item.Width, e.Item.Height);
        }

        protected override void OnRenderToolStripBorder(Forms.ToolStripRenderEventArgs e)
        {
            using (var pen = new Drawing.Pen(Drawing.Color.FromArgb(215, 215, 215)))
            using (var path = RoundBorder(e.ToolStrip.Width, e.ToolStrip.Height))
            {
                e.Graphics.SmoothingMode = Drawing.Drawing2D.SmoothingMode.AntiAlias;
                e.Graphics.DrawPath(pen, path);
            }
        }

        protected override void OnRenderSeparator(Forms.ToolStripSeparatorRenderEventArgs e)
        {
            using (var pen = new Drawing.Pen(Drawing.Color.FromArgb(215, 215, 215)))
                e.Graphics.DrawLine(pen, 6, e.Item.Height / 2, e.Item.Width - 6, e.Item.Height / 2);
        }
    }
}
