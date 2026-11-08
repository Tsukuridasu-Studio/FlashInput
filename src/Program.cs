using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace FlashInput
{
    internal static class Program
    {
        // named mutex so people can't accidentally launch two trays at once
        private const string SingleInstanceName = "Tsukuridasu.FlashInput.SingleInstance";

        [STAThread]
        private static void Main()
        {
            using var mutex = new Mutex(initiallyOwned: true, SingleInstanceName, out bool isFirstInstance);
            if (!isFirstInstance)
            {
                // already running, nothing to do here
                return;
            }

            ApplicationConfiguration.Initialize();

            // last-resort net: if something blows up on the UI thread we still want
            // the controller released before the process dies
            TrayContext? context = null;
            Application.ThreadException += (_, e) =>
            {
                MessageBox.Show(e.Exception.Message, "FlashInput", MessageBoxButtons.OK, MessageBoxIcon.Error);
                context?.Shutdown();
            };

            try
            {
                context = new TrayContext();
                Application.Run(context); // no main form, just the tray
            }
            finally
            {
                context?.Dispose();
            }
        }
    }

    /// <summary>
    /// Owns the tray icon and the ControllerBridge. No window ever gets shown.
    /// </summary>
    internal sealed class TrayContext : ApplicationContext
    {
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyIcon(IntPtr hIcon);

        private readonly ControllerBridge _bridge;
        private readonly NotifyIcon _tray;
        private readonly ContextMenuStrip _menu;
        private readonly ToolStripMenuItem _controllerMenu;
        private readonly ToolStripMenuItem _toggleItem;
        private readonly Icon _icon;
        private readonly IntPtr _iconHandle;
        private bool _disposed;

        public TrayContext()
        {
            _bridge = new ControllerBridge();
            _bridge.Faulted += OnBridgeFaulted;

            (_icon, _iconHandle) = BuildTrayIcon();

            // step 1: pick your pad. the list is rebuilt every time the submenu opens
            _controllerMenu = new ToolStripMenuItem("Select Controller");
            _controllerMenu.DropDownOpening += (_, _) => RebuildControllerList();
            // placeholder so the little arrow shows up before the first open
            _controllerMenu.DropDownItems.Add(new ToolStripMenuItem("Scanning...") { Enabled = false });

            // step 2: start / stop
            _toggleItem = new ToolStripMenuItem("Toggle Emulation") { CheckOnClick = false };
            _toggleItem.Click += OnToggleClicked;

            var exitItem = new ToolStripMenuItem("Exit");
            exitItem.Click += OnExitClicked;

            _menu = new ContextMenuStrip();
            _menu.Items.Add(_controllerMenu);
            _menu.Items.Add(_toggleItem);
            _menu.Items.Add(new ToolStripSeparator());
            _menu.Items.Add(exitItem);

            _tray = new NotifyIcon
            {
                Icon = _icon,
                ContextMenuStrip = _menu,
                Visible = true
            };

            // double-click the icon = quick toggle
            _tray.DoubleClick += OnToggleClicked;

            RefreshState();
        }

        // ---------- controller list ----------

        private void RebuildControllerList()
        {
            // dispose the old items (and unhook their handlers) so they don't pile up every time you open the menu
            var old = new ToolStripItem[_controllerMenu.DropDownItems.Count];
            _controllerMenu.DropDownItems.CopyTo(old, 0);
            _controllerMenu.DropDownItems.Clear();
            foreach (var item in old)
            {
                if (item is ToolStripMenuItem m) m.Click -= OnControllerPicked;
                item.Dispose();
            }

            var devices = WinMmJoystick.Enumerate();
            var current = _bridge.SelectedDevice;

            if (devices.Count == 0)
            {
                _controllerMenu.DropDownItems.Add(
                    new ToolStripMenuItem("No controllers found - plug one in") { Enabled = false });
                return;
            }

            foreach (var dev in devices)
            {
                var item = new ToolStripMenuItem(dev.DisplayName)
                {
                    Tag = dev,
                    Checked = current is not null && current.Id == dev.Id
                };
                item.Click += OnControllerPicked;
                _controllerMenu.DropDownItems.Add(item);
            }
        }

        private void OnControllerPicked(object? sender, EventArgs e)
        {
            if (sender is not ToolStripMenuItem { Tag: JoystickInfo dev }) return;

            // switching pads mid-run: stop, swap, start again
            bool wasRunning = _bridge.IsRunning;
            _bridge.Stop();
            _bridge.SelectedDevice = dev;

            if (wasRunning)
            {
                try { _bridge.Start(); }
                catch (Exception ex) { ShowError($"Couldn't restart emulation: {ex.Message}"); }
            }

            _tray.ShowBalloonTip(2000, "FlashInput", $"Selected: {dev.Name}", ToolTipIcon.Info);
            RefreshState();
        }

        // ---------- toggle / state ----------

        private void OnToggleClicked(object? sender, EventArgs e)
        {
            try
            {
                if (_bridge.IsRunning)
                    _bridge.Stop();
                else
                    _bridge.Start();
            }
            catch (Exception ex)
            {
                ShowError($"Couldn't toggle emulation: {ex.Message}");
            }

            RefreshState();
        }

        private void OnBridgeFaulted(object? sender, string message)
        {
            if (_disposed) return;

            _bridge.Stop(); // free the virtual pad right away, don't leave it plugged in
            ShowError(message);
            RefreshState();
        }

        private void RefreshState()
        {
            bool on = _bridge.IsRunning;
            _toggleItem.Checked = on;
            // NotifyIcon.Text has a 63 char limit, keep it short
            _tray.Text = on ? "FlashInput - emulation ON" : "FlashInput - emulation OFF";
        }

        private void ShowError(string text) =>
            _tray.ShowBalloonTip(3000, "FlashInput", text, ToolTipIcon.Error);

        private void OnExitClicked(object? sender, EventArgs e) => Shutdown();

        /// <summary>
        /// Stops emulation, frees the virtual pad + driver connection, removes the tray icon, quits.
        /// Safe to call more than once.
        /// </summary>
        public void Shutdown()
        {
            Dispose();
            ExitThread();
        }

        protected override void Dispose(bool disposing)
        {
            if (_disposed)
            {
                base.Dispose(disposing);
                return;
            }
            _disposed = true;

            if (disposing)
            {
                _bridge.Faulted -= OnBridgeFaulted;

                // hide the icon first, otherwise a ghost icon lingers until you hover over it
                _tray.Visible = false;

                // Dispose stops the translation thread, unplugs the virtual pad, frees the ViGEm client
                _bridge.Dispose();

                _tray.Dispose();
                _menu.Dispose(); // takes the submenu items with it
                _icon.Dispose();
            }

            // GetHicon() hands us a GDI handle that .NET won't free for us
            if (_iconHandle != IntPtr.Zero)
                DestroyIcon(_iconHandle);

            base.Dispose(disposing);
        }

        // draws a tiny lightning bolt on a dark circle, nothing fancy
        private static (Icon icon, IntPtr handle) BuildTrayIcon()
        {
            using var bmp = new Bitmap(32, 32);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);

                using var bg = new SolidBrush(Color.FromArgb(30, 30, 34));
                g.FillEllipse(bg, 1, 1, 30, 30);

                var bolt = new[]
                {
                    new PointF(18, 4), new PointF(8, 18), new PointF(15, 18),
                    new PointF(13, 28), new PointF(24, 13), new PointF(17, 13)
                };
                using var boltBrush = new SolidBrush(Color.FromArgb(255, 214, 10));
                g.FillPolygon(boltBrush, bolt);
            }

            IntPtr hIcon = bmp.GetHicon();
            return (Icon.FromHandle(hIcon), hIcon);
        }
    }
}