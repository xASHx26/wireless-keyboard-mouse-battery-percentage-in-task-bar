using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace PeripheralBattery
{
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            if (args.SequenceEqual(new[] { "--list" }))
            {
                try
                {
                    foreach (Peripheral device in DeviceReader.Read())
                        Console.WriteLine("{0} | {1} | {2} | {3}",
                            device.Kind, device.Name, device.Connection, device.Status);
                    return 0;
                }
                catch (Exception error)
                {
                    Console.Error.WriteLine(error.Message);
                    return 1;
                }
            }

            bool firstInstance;
            using (var instance = new Mutex(true, @"Local\PeripheralBatteryTray", out firstInstance))
            {
                if (!firstInstance) return 0;
                try
                {
                    Application.EnableVisualStyles();
                    Application.SetCompatibleTextRenderingDefault(false);
                    using (var tray = new BatteryTray()) Application.Run(tray);
                }
                finally
                {
                    instance.ReleaseMutex();
                }
            }
            return 0;
        }
    }

    internal sealed class AppSettings
    {
        private static readonly string SettingsFile = System.IO.Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory, "settings.ini");

        public bool ShowMouseIcon = true;
        public bool ShowKeyboardIcon = true;
        public bool AutoHideUnavailable = false;
        public bool HideUnavailableDevices = false;
        public readonly HashSet<string> ExcludedDevices = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public static AppSettings Load()
        {
            var s = new AppSettings();
            try
            {
                if (System.IO.File.Exists(SettingsFile))
                {
                    foreach (string line in System.IO.File.ReadAllLines(SettingsFile))
                    {
                        int eq = line.IndexOf('=');
                        if (eq <= 0) continue;
                        string key = line.Substring(0, eq).Trim();
                        string val = line.Substring(eq + 1).Trim();
                        if (key.Equals("ShowMouseIcon", StringComparison.OrdinalIgnoreCase))
                            s.ShowMouseIcon = val.Equals("true", StringComparison.OrdinalIgnoreCase);
                        else if (key.Equals("ShowKeyboardIcon", StringComparison.OrdinalIgnoreCase))
                            s.ShowKeyboardIcon = val.Equals("true", StringComparison.OrdinalIgnoreCase);
                        else if (key.Equals("AutoHideUnavailable", StringComparison.OrdinalIgnoreCase))
                            s.AutoHideUnavailable = val.Equals("true", StringComparison.OrdinalIgnoreCase);
                        else if (key.Equals("HideUnavailableDevices", StringComparison.OrdinalIgnoreCase))
                            s.HideUnavailableDevices = val.Equals("true", StringComparison.OrdinalIgnoreCase);
                        else if (key.Equals("ExcludedDevice", StringComparison.OrdinalIgnoreCase) && val.Length > 0)
                            s.ExcludedDevices.Add(val);
                    }
                }
            }
            catch { }
            return s;
        }

        public void Save()
        {
            try
            {
                var sb = new System.Text.StringBuilder();
                sb.AppendLine("ShowMouseIcon=" + (ShowMouseIcon ? "true" : "false"));
                sb.AppendLine("ShowKeyboardIcon=" + (ShowKeyboardIcon ? "true" : "false"));
                sb.AppendLine("AutoHideUnavailable=" + (AutoHideUnavailable ? "true" : "false"));
                sb.AppendLine("HideUnavailableDevices=" + (HideUnavailableDevices ? "true" : "false"));
                foreach (string dev in ExcludedDevices)
                    sb.AppendLine("ExcludedDevice=" + dev);
                System.IO.File.WriteAllText(SettingsFile, sb.ToString());
            }
            catch { }
        }

        public static bool IsStartupEnabled()
        {
            try
            {
                using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", false))
                {
                    return key != null && key.GetValue("PeripheralBattery") != null;
                }
            }
            catch { return false; }
        }

        public static void SetStartupEnabled(bool enable)
        {
            try
            {
                using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true))
                {
                    if (key == null) return;
                    if (enable)
                    {
                        string exePath = "\"" + System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "PeripheralBattery.exe") + "\"";
                        key.SetValue("PeripheralBattery", exePath);
                    }
                    else
                    {
                        key.DeleteValue("PeripheralBattery", false);
                    }
                }
            }
            catch { }
        }
    }

    internal sealed class BatteryTray : ApplicationContext
    {
        private readonly AppSettings settings = AppSettings.Load();
        private readonly TrayIndicator mouse = new TrayIndicator("Mouse");
        private readonly TrayIndicator keyboard = new TrayIndicator("Keyboard");
        private readonly System.Windows.Forms.Timer poll = new System.Windows.Forms.Timer();
        private readonly ToolStripMenuItem refresh = new ToolStripMenuItem("Refresh battery levels");
        private readonly ContextMenuStrip menu = new ContextMenuStrip { ShowCheckMargin = true, ShowImageMargin = false };
        private List<Peripheral> devices = new List<Peripheral>();
        private string scanStatus = "Checking connected devices…";
        private bool preventClose = false;

        public BatteryTray()
        {
            mouse.Icon.ContextMenuStrip = menu;
            keyboard.Icon.ContextMenuStrip = menu;
            menu.Opening += (sender, args) => { PopulateMenu(); args.Cancel = false; };
            menu.ItemClicked += (sender, args) =>
            {
                if (args.ClickedItem != null && (args.ClickedItem.Tag as string == "keepOpen" || (args.ClickedItem.Tag as string ?? "").StartsWith("device")))
                {
                    preventClose = true;
                }
            };
            menu.Closing += (sender, args) =>
            {
                if (args.CloseReason == ToolStripDropDownCloseReason.ItemClicked && preventClose)
                {
                    args.Cancel = true;
                    preventClose = false;
                }
            };
            refresh.Click += (sender, args) => Refresh();
            poll.Interval = 60000;
            poll.Tick += (sender, args) => Refresh();
            Application.Idle += Start;
        }

        private void Start(object sender, EventArgs args)
        {
            Application.Idle -= Start;
            poll.Start();
            Refresh();
        }

        private bool IsDeviceExcluded(Peripheral d)
        {
            if (settings.ExcludedDevices.Contains(d.Name)) return true;
            if (!string.IsNullOrEmpty(d.Id) && settings.ExcludedDevices.Contains(d.Id)) return true;
            return false;
        }

        private void UpdateTrayVisibility()
        {
            bool hasMouseBattery = devices.Any(d => d.Kind == "Mouse" && d.Level.HasValue && !IsDeviceExcluded(d));
            bool hasKeyboardBattery = devices.Any(d => d.Kind == "Keyboard" && d.Level.HasValue && !IsDeviceExcluded(d));

            bool mouseVisible = settings.ShowMouseIcon;
            bool keyboardVisible = settings.ShowKeyboardIcon;

            if (settings.AutoHideUnavailable)
            {
                if (!hasMouseBattery) mouseVisible = false;
                if (!hasKeyboardBattery) keyboardVisible = false;
            }

            // Ensure at least one icon remains visible so user can access menu
            if (!mouseVisible && !keyboardVisible)
            {
                if (hasMouseBattery || settings.ShowMouseIcon) mouseVisible = true;
                else keyboardVisible = true;
            }

            mouse.Icon.Visible = mouseVisible;
            keyboard.Icon.Visible = keyboardVisible;
        }

        private void RefreshUi()
        {
            var activeDevices = devices.Where(d => !IsDeviceExcluded(d)).ToList();
            mouse.Update(activeDevices, scanStatus);
            keyboard.Update(activeDevices, scanStatus);
            UpdateTrayVisibility();
        }

        private async void Refresh()
        {
            if (!refresh.Enabled) return;
            refresh.Enabled = false;
            scanStatus = "Checking connected devices…";
            try
            {
                devices = await Task.Run(() => DeviceReader.Read());
                scanStatus = devices.Count == 0 ? "No mouse or keyboard detected" : null;
            }
            catch (Exception error)
            {
                Debug.WriteLine(error);
                devices = new List<Peripheral>();
                scanStatus = "Windows device scan failed";
            }

            if (menu.IsDisposed) return;
            RefreshUi();
            refresh.Enabled = true;
            if (menu.Visible) PopulateMenu();
        }

        private void PopulateMenu()
        {
            while (menu.Items.Count > 0)
            {
                ToolStripItem item = menu.Items[0];
                menu.Items.RemoveAt(0);
                if (item != refresh) item.Dispose();
            }

            if (scanStatus != null)
            {
                var scanItem = new ToolStripMenuItem(scanStatus) { Enabled = false };
                menu.Items.Add(scanItem);
            }

            var shownDevices = settings.HideUnavailableDevices
                ? devices.Where(d => d.Level.HasValue).ToList()
                : devices;

            if (shownDevices.Count > 0)
            {
                var devHeader = new ToolStripMenuItem("Devices (checked stay active):") { Enabled = false };
                menu.Items.Add(devHeader);

                foreach (Peripheral device in shownDevices)
                {
                    Peripheral captured = device;
                    bool isIncluded = !IsDeviceExcluded(captured);
                    string name = captured.Name.Replace("&", "&&");
                    string label = captured.Kind + " · " + name + " · " + captured.Status;
                    var devItem = new ToolStripMenuItem(label)
                    {
                        Checked = isIncluded,
                        CheckOnClick = false,
                        Tag = captured.Level.HasValue ? "device_hasbat" : "device_nobat"
                    };
                    devItem.Click += (sender, args) =>
                    {
                        string key = !string.IsNullOrEmpty(captured.Id) ? captured.Id : captured.Name;
                        bool willBeIncluded;
                        if (settings.ExcludedDevices.Contains(key) || settings.ExcludedDevices.Contains(captured.Name))
                        {
                            settings.ExcludedDevices.Remove(key);
                            settings.ExcludedDevices.Remove(captured.Name);
                            willBeIncluded = true;
                        }
                        else
                        {
                            settings.ExcludedDevices.Add(key);
                            willBeIncluded = false;
                        }
                        devItem.Checked = willBeIncluded;
                        settings.Save();
                        RefreshUi();
                    };
                    menu.Items.Add(devItem);
                }
            }

            menu.Items.Add(new ToolStripSeparator());

            // Tray Icons options
            var trayHeader = new ToolStripMenuItem("Tray icons (checked stay visible):") { Enabled = false };
            menu.Items.Add(trayHeader);

            var mouseItem = new ToolStripMenuItem("Mouse tray icon")
            {
                Checked = settings.ShowMouseIcon,
                CheckOnClick = false,
                Tag = "keepOpen"
            };
            mouseItem.Click += (sender, args) =>
            {
                if (settings.ShowMouseIcon && !settings.ShowKeyboardIcon)
                {
                    MessageBox.Show("At least one tray icon must remain enabled.", "Peripheral Battery", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                settings.ShowMouseIcon = !settings.ShowMouseIcon;
                mouseItem.Checked = settings.ShowMouseIcon;
                settings.Save();
                UpdateTrayVisibility();
            };
            menu.Items.Add(mouseItem);

            var kbItem = new ToolStripMenuItem("Keyboard tray icon")
            {
                Checked = settings.ShowKeyboardIcon,
                CheckOnClick = false,
                Tag = "keepOpen"
            };
            kbItem.Click += (sender, args) =>
            {
                if (settings.ShowKeyboardIcon && !settings.ShowMouseIcon)
                {
                    MessageBox.Show("At least one tray icon must remain enabled.", "Peripheral Battery", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                settings.ShowKeyboardIcon = !settings.ShowKeyboardIcon;
                kbItem.Checked = settings.ShowKeyboardIcon;
                settings.Save();
                UpdateTrayVisibility();
            };
            menu.Items.Add(kbItem);

            var autoHideItem = new ToolStripMenuItem("Auto-hide icon if battery unavailable")
            {
                Checked = settings.AutoHideUnavailable,
                CheckOnClick = false,
                Tag = "keepOpen"
            };
            autoHideItem.Click += (sender, args) =>
            {
                settings.AutoHideUnavailable = !settings.AutoHideUnavailable;
                autoHideItem.Checked = settings.AutoHideUnavailable;
                settings.Save();
                UpdateTrayVisibility();
            };
            menu.Items.Add(autoHideItem);

            var hideNoBatItem = new ToolStripMenuItem("Hide devices without battery in menu")
            {
                Checked = settings.HideUnavailableDevices,
                CheckOnClick = false,
                Tag = "keepOpen"
            };
            hideNoBatItem.Click += (sender, args) =>
            {
                settings.HideUnavailableDevices = !settings.HideUnavailableDevices;
                hideNoBatItem.Checked = settings.HideUnavailableDevices;
                settings.Save();
                foreach (ToolStripItem it in menu.Items)
                {
                    if (it.Tag as string == "device_nobat")
                    {
                        it.Visible = !settings.HideUnavailableDevices;
                    }
                }
            };
            menu.Items.Add(hideNoBatItem);

            var startupItem = new ToolStripMenuItem("Start with Windows")
            {
                Checked = AppSettings.IsStartupEnabled(),
                CheckOnClick = false,
                Tag = "keepOpen"
            };
            startupItem.Click += (sender, args) =>
            {
                bool newState = !AppSettings.IsStartupEnabled();
                AppSettings.SetStartupEnabled(newState);
                startupItem.Checked = newState;
            };
            menu.Items.Add(startupItem);

            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(refresh);
            menu.Items.Add("Taskbar settings", null, (sender, args) =>
            {
                try
                {
                    Process.Start(new ProcessStartInfo("ms-settings:taskbar") { UseShellExecute = true });
                }
                catch (System.ComponentModel.Win32Exception)
                {
                    MessageBox.Show("Open Settings → Personalization → Taskbar to show the battery icons.",
                        "Peripheral Battery");
                }
            });
            menu.Items.Add("Exit", null, (sender, args) => ExitThread());
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Application.Idle -= Start;
                poll.Dispose();
                mouse.Dispose();
                keyboard.Dispose();
                menu.Dispose();
                refresh.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    internal sealed class TrayIndicator : IDisposable
    {
        private readonly string kind;
        private Icon drawing;
        private int? displayedLevel;

        public NotifyIcon Icon { get; private set; }

        public TrayIndicator(string kind)
        {
            this.kind = kind;
            drawing = Draw(null);
            Icon = new NotifyIcon { Icon = drawing, Text = kind + " battery: checking…", Visible = true };
        }

        public void Update(List<Peripheral> devices, string scanStatus)
        {
            var matches = devices.Where(d => d.Kind == kind).ToList();
            int? level = matches.Select(d => d.Level).Min();
            if (level != displayedLevel)
            {
                var next = Draw(level);
                Icon.Icon = next;
                drawing.Dispose();
                drawing = next;
                displayedLevel = level;
            }

            string tooltip;
            if (matches.Count == 1)
            {
                string status = " · " + matches[0].Status;
                string name = matches[0].Name;
                int maxNameLength = 63 - kind.Length - 2 - status.Length;
                if (name.Length > maxNameLength) name = name.Substring(0, maxNameLength - 1) + "…";
                tooltip = kind + ": " + name + status;
            }
            else if (matches.Count > 1)
            {
                tooltip = kind + " batteries: " + (level.HasValue ? "lowest " + level + "%" : "unavailable")
                    + " · Right-click for details";
            }
            else
            {
                tooltip = kind + " battery: " + (scanStatus ?? "no device detected");
            }
            Icon.Text = tooltip;
        }

        private static readonly string[] KeyboardSymbol =
        {
            "......####....",
            "......#.......",
            ".############.",
            "#............#",
            "#.#.#.#.#.#..#",
            "#............#",
            "#..#.#.#.#.#.#",
            "#............#",
            "#...######...#",
            ".############."
        };

        private static readonly string[] MouseSymbol =
        {
            ".....###..",
            ".....#....",
            "...####...",
            "..#..#.#..",
            ".#...#..#.",
            ".########.",
            ".#......#.",
            ".#......#.",
            "..#....#..",
            "...####..."
        };

        private Icon Draw(int? level)
        {
            using (var bitmap = new Bitmap(16, 16))
            using (var graphics = Graphics.FromImage(bitmap))
            {
                graphics.Clear(Color.Black);
                string[] symbol = kind == "Mouse" ? MouseSymbol : KeyboardSymbol;
                PaintPixels(graphics, symbol, (16 - symbol[0].Length) / 2, 0, Brushes.White);

                graphics.FillRectangle(Brushes.White, 0, 11, 16, 5);
                graphics.FillRectangle(Brushes.Black, 1, 12, 14, 3);
                if (level.HasValue)
                {
                    int fill = (int)Math.Round(level.Value * 14 / 100.0);
                    if (level.Value > 0) fill = Math.Max(fill, 1);
                    graphics.FillRectangle(Brushes.White, 1, 12, fill, 3);
                }
                else
                {
                    // A dash distinguishes an unavailable reading from an empty battery.
                    graphics.FillRectangle(Brushes.White, 6, 13, 4, 1);
                }
                IntPtr handle = bitmap.GetHicon();
                try
                {
                    using (Icon borrowed = System.Drawing.Icon.FromHandle(handle))
                        return (Icon)borrowed.Clone();
                }
                finally
                {
                    DestroyIcon(handle);
                }
            }
        }

        private static void PaintPixels(Graphics graphics, string[] rows, int left, int top, Brush brush)
        {
            for (int y = 0; y < rows.Length; y++)
            {
                for (int x = 0; x < rows[y].Length; x++)
                {
                    if (rows[y][x] == '#') graphics.FillRectangle(brush, left + x, top + y, 1, 1);
                }
            }
        }

        public void Dispose()
        {
            Icon.Visible = false;
            Icon.Dispose();
            drawing.Dispose();
        }

        [DllImport("user32.dll")]
        private static extern bool DestroyIcon(IntPtr handle);
    }
}







