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

    internal sealed class BatteryTray : ApplicationContext
    {
        private readonly TrayIndicator mouse = new TrayIndicator("Mouse");
        private readonly TrayIndicator keyboard = new TrayIndicator("Keyboard");
        private readonly System.Windows.Forms.Timer poll = new System.Windows.Forms.Timer();
        private readonly ToolStripMenuItem refresh = new ToolStripMenuItem("Refresh battery levels");
        private readonly ContextMenuStrip menu = new ContextMenuStrip { ShowImageMargin = false };
        private List<Peripheral> devices = new List<Peripheral>();
        private string scanStatus = "Checking connected devices…";

        public BatteryTray()
        {
            mouse.Icon.ContextMenuStrip = menu;
            keyboard.Icon.ContextMenuStrip = menu;
            menu.Opening += (sender, args) => { PopulateMenu(); args.Cancel = false; };
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
            mouse.Update(devices, scanStatus);
            keyboard.Update(devices, scanStatus);
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

            if (scanStatus != null) menu.Items.Add(scanStatus).Enabled = false;
            foreach (Peripheral device in devices)
            {
                // Escape ampersands so product names are displayed literally.
                string name = device.Name.Replace("&", "&&");
                menu.Items.Add(device.Kind + " · " + name + " · " + device.Status).Enabled = false;
            }
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







