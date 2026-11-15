using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Whatts;

sealed class MainForm : Form
{
    // Surface and ink. Text always wears ink; color is reserved for marks (graph, dots).
    static readonly Color Bg = Color.FromArgb(0x1B, 0x1B, 0x1F);
    static readonly Color Ink = Color.FromArgb(0xF0, 0xEF, 0xEC);
    static readonly Color LabelInk = Color.FromArgb(0xA3, 0xA2, 0x9A);
    static readonly Color Muted = Color.FromArgb(0x85, 0x84, 0x7C);
    static readonly Color Grid = Color.FromArgb(0x2C, 0x2C, 0x2A);
    static readonly Color Axis = Color.FromArgb(0x45, 0x45, 0x42);

    // Polarity: a CVD-validated blue/orange diverging pair (power into vs out of the battery).
    // Sign and position above/below zero carry the same meaning, so color is never alone.
    static readonly Color PowerIn = Color.FromArgb(0x39, 0x87, 0xE5);
    static readonly Color PowerOut = Color.FromArgb(0xD9, 0x59, 0x26);
    static readonly Color PowerInOnLight = Color.FromArgb(0x2A, 0x78, 0xD6);
    static readonly Color PowerOutOnLight = Color.FromArgb(0xEB, 0x68, 0x34);

    // Status (paired with the number itself, never color alone).
    static readonly Color Warning = Color.FromArgb(0xFA, 0xB2, 0x19);
    static readonly Color Critical = Color.FromArgb(0xD0, 0x3B, 0x3B);

    const int BaseWidth = 300, GraphHeight = 64;
    const string Signed1 = "+0.0;−0.0;0.0";
    const string Signed2 = "+0.00;−0.00;0.00";
    const string Signed3 = "+0.000;−0.000;0.000";
    const string Missing = "—";
    const string Dot = "  ·  ";

    readonly Settings settings = Settings.Load();
    readonly ThermalReader thermal = new();
    readonly PowerHistory history = new(TimeSpan.FromMinutes(10));
    readonly ChargeSession session = new();
    readonly System.Windows.Forms.Timer timer = new() { Interval = 1000 };
    readonly NotifyIcon tray = new() { Text = "Whatts" };
    readonly ToolStripMenuItem showItem = new("Show window");
    readonly ToolStripMenuItem topMostItem = new("Always on top");
    readonly ToolStripMenuItem autostartItem = new("Start with Windows");
    bool startHidden;

    Font headFont = null!, subFont = null!, rowFont = null!, sectionFont = null!, smallFont = null!;
    string headline = "…", subline = "";
    Color? headMark;
    bool showGraph;
    List<Row> rows = [];
    readonly List<(Rectangle Bounds, string Key)> sectionHits = [];
    Rectangle graphBounds;
    int? hoverX;
    IntPtr trayIconHandle;
    string trayKey = "";

    readonly record struct Row(string Label, string Value, Color? Mark = null, string? SectionKey = null)
    {
        public static Row Section(string title, string key) => new(title, "", null, key);
    }

    public MainForm(bool startHidden)
    {
        this.startHidden = startHidden;
        Text = "Whatts";
        Icon = LoadAppIcon();
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        BackColor = Bg;
        DoubleBuffered = true;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None; // everything is custom-drawn and scaled by DeviceDpi
        TopMost = settings.TopMost;
        BuildFonts();

        topMostItem.Checked = TopMost;
        showItem.Click += (_, _) => ShowFromTray();
        topMostItem.Click += (_, _) =>
        {
            TopMost = !TopMost;
            topMostItem.Checked = settings.TopMost = TopMost;
            settings.Save();
        };
        autostartItem.Click += (_, _) => Autostart.Set(!Autostart.IsEnabled);

        var menu = new ContextMenuStrip();
        menu.Items.AddRange([showItem, topMostItem, autostartItem, new ToolStripSeparator(),
            new ToolStripMenuItem("Exit", null, (_, _) => Close())]);
        menu.Opening += (_, _) => autostartItem.Checked = Autostart.IsEnabled;
        ContextMenuStrip = menu;

        tray.ContextMenuStrip = menu;
        tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) ShowFromTray(); };
        tray.Visible = true;

        timer.Tick += (_, _) => Poll();
    }

    static Icon? LoadAppIcon()
    {
        using var stream = typeof(MainForm).Assembly.GetManifestResourceStream("Whatts.ico");
        return stream is null ? null : new Icon(stream);
    }

    // ---- window lifecycle ----

    // Started with --tray (at sign-in): create the window but keep it hidden.
    protected override void SetVisibleCore(bool value)
    {
        if (startHidden)
        {
            startHidden = false;
            value = false;
            if (!IsHandleCreated) CreateHandle();
        }
        base.SetVisibleCore(value);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        int dark = 1;
        DwmSetWindowAttribute(Handle, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
        RegisterGlobalHotkey();
        if (!timer.Enabled)
        {
            Poll();
            timer.Start();
        }
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        UnregisterHotKey(Handle, HotkeyId);
        base.OnHandleDestroyed(e);
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        if (settings.X is int x && settings.Y is int y &&
            Screen.AllScreens.Any(s => s.WorkingArea.Contains(x + Px(40), y + Px(10))))
        {
            Location = new Point(x, y);
        }
        else
        {
            var area = Screen.FromControl(this).WorkingArea;
            Location = new Point(area.Right - Width - Px(16), area.Bottom - Height - Px(16));
        }
        KeepOnScreen();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (WindowState == FormWindowState.Minimized) Hide(); // minimize to tray
    }

    protected override void OnResizeEnd(EventArgs e) // also fires after the user drags the window
    {
        base.OnResizeEnd(e);
        SaveLocation();
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        BuildFonts();
        trayKey = "";
        ResizeToContent();
        Invalidate();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        SaveLocation();
        timer.Stop();
        tray.Visible = false;
        tray.Dispose();
        if (trayIconHandle != IntPtr.Zero) DestroyIcon(trayIconHandle);
        thermal.Dispose();
        base.OnFormClosed(e);
    }

    public void ShowFromTray()
    {
        Show();
        WindowState = FormWindowState.Normal;
        KeepOnScreen();
        Activate();
    }

    void ToggleWindow()
    {
        if (Visible && WindowState == FormWindowState.Normal && GetForegroundWindow() == Handle)
            WindowState = FormWindowState.Minimized; // hides to tray via OnResize
        else
            ShowFromTray();
    }

    void SaveLocation()
    {
        if (!Visible || WindowState != FormWindowState.Normal) return;
        settings.X = Left;
        settings.Y = Top;
        settings.Save();
    }

    void KeepOnScreen()
    {
        if (!Visible || WindowState != FormWindowState.Normal) return;
        var area = Screen.FromControl(this).WorkingArea;
        int left = Math.Max(area.Left, Math.Min(Left, area.Right - Width));
        int top = Math.Max(area.Top, Math.Min(Top, area.Bottom - Height));
        if (left != Left || top != Top) Location = new Point(left, top);
    }

    // ---- global hotkey ----

    const int WM_HOTKEY = 0x0312, HotkeyId = 1;
    const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_SHIFT = 0x4, MOD_WIN = 0x8, MOD_NOREPEAT = 0x4000;

    // First one that's free wins; Windows reserves most Win+letter combos, so keep a fallback.
    static readonly (uint Modifiers, string Name)[] Hotkeys =
    [
        (MOD_WIN | MOD_SHIFT, "Win+Shift+W"),
        (MOD_CONTROL | MOD_ALT, "Ctrl+Alt+W"),
    ];

    void RegisterGlobalHotkey()
    {
        string? name = null;
        foreach (var (mods, label) in Hotkeys)
        {
            if (RegisterHotKey(Handle, HotkeyId, mods | MOD_NOREPEAT, (uint)Keys.W))
            {
                name = label;
                break;
            }
        }
        showItem.ShortcutKeyDisplayString = name ?? "";
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_HOTKEY && m.WParam == HotkeyId)
        {
            ToggleWindow();
            return;
        }
        base.WndProc(ref m);
    }

    // ---- data ----

    void Poll()
    {
        var now = DateTime.UtcNow;
        var batteries = BatteryReader.ReadAll();
        var zones = thermal.Read();
        var total = batteries.Count == 0 ? null : Combine(batteries);

        if (total is not null)
        {
            session.Update(total, now);
            history.Add(now, total.Watts);
        }

        BuildRows(batteries, total, zones, now);
        UpdateTray(total);
        ResizeToContent();
        Invalidate();
    }

    void BuildRows(List<BatteryReading> batteries, BatteryReading? total, List<ThermalZone> zones, DateTime now)
    {
        var list = new List<Row>();
        showGraph = total is { RelativeUnits: false };

        if (total is null)
        {
            headline = "No battery";
            subline = "Windows isn't reporting one";
            headMark = null;
        }
        else
        {
            headline = total.Watts is double w ? w.ToString(Signed1) + " W" : Missing + " W";
            headMark = PolarityColor(total.State, light: false);
            subline = string.Join(Dot, new[]
            {
                StatusText(total),
                total.Percent is double p ? $"{p:0}%" : null,
                Eta(total),
            }.Where(s => s is not null));
        }

        for (int i = 0; i < batteries.Count; i++)
        {
            var b = batteries[i];
            list.Add(Row.Section(batteries.Count > 1 ? $"Battery {i + 1}" : "Battery", $"battery{i}"));

            if (b.RelativeUnits)
            {
                list.Add(new("Charge", Fmt(b.Percent, "0.0", " %")));
                list.Add(new("Voltage", Fmt(b.Volts, "0.000", " V")));
                list.Add(new("Units", "relative (no mW / mWh)"));
            }
            else
            {
                list.Add(new("Power", Fmt(b.Watts, Signed2, " W")));
                list.Add(new("Voltage", Fmt(b.Volts, "0.000", " V")));
                list.Add(new("Current", Fmt(b.Amps, Signed3, " A")));
                list.Add(new("Charge", b.Percent is double p && b.Capacity is uint c && b.FullCharged is uint f
                    ? $"{p:0.0} %{Dot}{c / 1000.0:0.0} / {f / 1000.0:0.0} Wh" : Missing));
                list.Add(new("Health", b.HealthPercent is double h
                    ? $"{h:0.0} %{Dot}design {b.Design / 1000.0:0.0} Wh" : Missing, HealthMark(b.HealthPercent)));
            }
        }

        if (total is { RelativeUnits: false })
        {
            bool onAc = session.OnAc;
            list.Add(Row.Section(onAc ? "Plugged-in session" : "On-battery session", "session"));
            list.Add(new("Duration", Span(session.Elapsed(now)) + (session.FromLaunch ? Dot + "since launch" : "")));
            list.Add(new("Energy", Fmt(session.EnergyWh(total), Signed2, " Wh")));
            list.Add(new("Average", session.AverageWatts(total, now) is double avg
                ? avg.ToString(Signed1) + " W" : $"after {ChargeSession.MinimumForAverage.TotalMinutes:0} min"));
            list.Add(new(onAc ? "Peak charging" : "Peak draw", session.PeakMagnitude is double pk
                ? (onAc ? pk : -pk).ToString(Signed1) + " W" : Missing));
            list.Add(new("Charge", session.StartPercent is double from && total.Percent is double to
                ? $"{from:0}% → {to:0}%" : Missing));
        }

        list.Add(Row.Section("Thermal zones", "thermal"));
        if (zones.Count == 0)
            list.Add(new("Sensors", "not exposed by firmware"));
        foreach (var z in zones)
            list.Add(new(z.Name, $"{z.Celsius:0.0} °C", TempMark(z.Celsius)));

        // Static facts go last and start collapsed, to keep the window short.
        for (int i = 0; i < batteries.Count; i++)
        {
            var b = batteries[i];
            list.Add(Row.Section(batteries.Count > 1 ? $"Battery {i + 1} details" : "Details", $"details{i}"));
            list.Add(new("Cycles", b.CycleCount?.ToString() ?? Missing));
            list.Add(new("Battery temp", b.TemperatureC is double t ? $"{t:0.0} °C" : "not reported",
                b.TemperatureC is double t2 ? TempMark(t2) : null));
            list.Add(new("Chemistry", Or(b.Chemistry)));
            list.Add(new("Model", Or(string.Join(" ", new[] { b.Manufacturer, b.Name }.Where(s => s.Length > 0)))));
        }

        rows = list;
    }

    static BatteryReading Combine(List<BatteryReading> bs) => bs.Count == 1 ? bs[0] : new BatteryReading
    {
        State = bs.Aggregate(PowerState.None, (s, b) => s | b.State),
        RelativeUnits = bs.Any(b => b.RelativeUnits),
        Capacity = SumAll(bs.Select(b => b.Capacity)),
        FullCharged = SumAll(bs.Select(b => b.FullCharged)),
        RateMw = bs.All(b => b.RateMw is null) ? null : bs.Sum(b => b.RateMw ?? 0),
    };

    static uint? SumAll(IEnumerable<uint?> values)
    {
        uint total = 0;
        foreach (var v in values)
        {
            if (v is null) return null;
            total += v.Value;
        }
        return total;
    }

    static string StatusText(BatteryReading b) =>
        b.State.HasFlag(PowerState.Charging) ? "Charging" :
        b.State.HasFlag(PowerState.Discharging) ? "On battery" :
        b.State.HasFlag(PowerState.OnLine) ? (b.Percent >= 99 ? "Plugged in, full" : "Plugged in, not charging") :
        "Idle";

    static string? Eta(BatteryReading b)
    {
        if (b.Capacity is not uint c || b.FullCharged is not uint f || b.RateMw is not int r || r == 0) return null;
        double hours = r > 0 ? (f > c ? (f - c) / (double)r : 0) : c / (double)-r;
        if (hours <= 0 || hours > 48) return null;
        string span = Span(TimeSpan.FromHours(hours));
        return r > 0 ? $"~{span} to full" : $"~{span} left";
    }

    static string Span(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours}h {t.Minutes:00}m" :
        t.TotalMinutes >= 1 ? $"{t.Minutes}m" :
        $"{Math.Max(0, t.Seconds)}s";

    static string Fmt(double? v, string format, string unit) => v is double d ? d.ToString(format) + unit : Missing;
    static string Or(string s) => s.Length > 0 ? s : Missing;

    static Color? PolarityColor(PowerState s, bool light) =>
        s.HasFlag(PowerState.Charging) ? (light ? PowerInOnLight : PowerIn) :
        s.HasFlag(PowerState.Discharging) ? (light ? PowerOutOnLight : PowerOut) :
        null;

    static Color? TempMark(double c) => c >= 90 ? Critical : c >= 75 ? Warning : null;
    static Color? HealthMark(double? h) => h switch { < 60 => Critical, < 80 => Warning, _ => null };

    // ---- layout & drawing ----

    int Px(float v) => (int)Math.Round(v * DeviceDpi / 96f);

    void BuildFonts()
    {
        foreach (var f in new[] { headFont, subFont, rowFont, sectionFont, smallFont }) f?.Dispose();
        headFont = new Font("Segoe UI Semibold", Px(30), GraphicsUnit.Pixel);
        subFont = new Font("Segoe UI", Px(13), GraphicsUnit.Pixel);
        rowFont = new Font("Segoe UI", Px(13), GraphicsUnit.Pixel);
        sectionFont = new Font("Segoe UI Semibold", Px(11), GraphicsUnit.Pixel);
        smallFont = new Font("Segoe UI", Px(11), GraphicsUnit.Pixel);
    }

    void ResizeToContent()
    {
        var size = new Size(Px(BaseWidth), RenderContent(null));
        if (ClientSize == size) return;
        ClientSize = size;
        KeepOnScreen();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        RenderContent(e.Graphics);
    }

    /// <summary>Lays out (and, given a Graphics, paints) the content; returns the total height.</summary>
    int RenderContent(Graphics? g)
    {
        const TextFormatFlags flags = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis;
        int pad = Px(16), width = Px(BaseWidth) - 2 * pad, y = Px(10);
        int labelWidth = width * 40 / 100;
        sectionHits.Clear();

        DrawText(g, headline, headFont, pad - Px(1), y, width, Ink, flags);
        y += headFont.Height;

        int subX = pad;
        if (headMark is Color mark)
        {
            FillDot(g, mark, pad + Px(4), y + subFont.Height / 2f, Px(4));
            subX += Px(14);
        }
        DrawText(g, subline, subFont, subX, y, width - (subX - pad), LabelInk, flags);
        y += subFont.Height + Px(10);

        if (showGraph)
        {
            graphBounds = new Rectangle(pad, y, width, Px(GraphHeight));
            if (g is not null) DrawGraph(g, graphBounds);
            y += graphBounds.Height + Px(4);
            DrawGraphCaption(g, pad, y, width, flags);
            y += smallFont.Height;
        }
        else
        {
            graphBounds = Rectangle.Empty;
        }

        int rowHeight = rowFont.Height + Px(4);
        bool collapsed = false;
        foreach (var r in rows)
        {
            if (r.SectionKey is string key)
            {
                collapsed = settings.Collapsed.Contains(key);
                y += Px(9);
                g?.FillRectangle(BrushFor(Grid), pad, y, width, Math.Max(1, Px(1)));
                y += Px(8);
                sectionHits.Add((new Rectangle(pad, y - Px(5), width, sectionFont.Height + Px(10)), key));
                DrawText(g, r.Label.ToUpperInvariant(), sectionFont, pad, y, width, Muted, flags);
                DrawChevron(g, pad + width - Px(5), y + sectionFont.Height / 2f, collapsed);
                y += sectionFont.Height + Px(5);
                continue;
            }
            if (collapsed) continue;

            DrawText(g, r.Label, rowFont, pad, y, labelWidth, LabelInk, flags);
            DrawText(g, r.Value, rowFont, pad + labelWidth, y, width - labelWidth, Ink, flags | TextFormatFlags.Right);
            if (r.Mark is Color dot && g is not null)
            {
                int textWidth = TextRenderer.MeasureText(g, r.Value, rowFont, Size.Empty, flags).Width;
                FillDot(g, dot, pad + width - textWidth - Px(9), y + rowFont.Height / 2f, Px(3.5f));
            }
            y += rowHeight;
        }
        return y + Px(12);
    }

    void DrawGraph(Graphics g, Rectangle r)
    {
        var samples = history.Samples;
        double max = 0, min = 0;
        foreach (var s in samples)
        {
            max = Math.Max(max, s.Watts);
            min = Math.Min(min, s.Watts);
        }
        double top = NiceCeiling(max), bottom = NiceCeiling(-min);
        if (top == 0 && bottom == 0) top = 5;

        int zeroY = r.Top + (int)Math.Round(r.Height * top / (top + bottom));
        float Y(double w) =>
            w > 0 ? zeroY - (float)(w / top) * (zeroY - r.Top) :
            w < 0 ? zeroY + (float)(-w / bottom) * (r.Bottom - zeroY) :
            zeroY;

        var now = DateTime.UtcNow;
        var start = now - history.Window;
        float X(DateTime t) => r.Left + (float)((t - start).TotalSeconds / history.Window.TotalSeconds) * r.Width;

        // Recessive grid: hairline at each scale end, a slightly stronger zero baseline.
        if (top > 0) g.FillRectangle(BrushFor(Grid), r.Left, r.Top, r.Width, 1);
        if (bottom > 0) g.FillRectangle(BrushFor(Grid), r.Left, r.Bottom - 1, r.Width, 1);
        g.FillRectangle(BrushFor(Axis), r.Left, zeroY, r.Width, 1);

        // Split into runs wherever samples are missing (sleep, a skipped poll).
        var runs = new List<List<PointF>>();
        List<PointF>? run = null;
        DateTime previous = default;
        foreach (var s in samples)
        {
            if (run is null || (s.Time - previous).TotalSeconds > 5)
            {
                run = [];
                runs.Add(run);
            }
            run.Add(new PointF(X(s.Time), Y(s.Watts)));
            previous = s.Time;
        }

        var oldMode = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        int halo = Px(3);
        var above = RectangleF.FromLTRB(r.Left - halo, r.Top - halo, r.Right + halo, zeroY + (bottom > 0 ? 0.5f : halo));
        var below = RectangleF.FromLTRB(r.Left - halo, zeroY + 0.5f, r.Right + halo, r.Bottom + halo);
        foreach (var (clip, color) in new[] { (above, PowerIn), (below, PowerOut) })
        {
            g.SetClip(clip);
            using var wash = new SolidBrush(Color.FromArgb(32, color));
            using var pen = new Pen(color, Px(2)) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round };
            foreach (var pts in runs)
            {
                if (pts.Count == 1)
                {
                    FillDot(g, color, pts[0].X, pts[0].Y, Px(1.5f));
                    continue;
                }
                var area = new List<PointF>(pts) { new(pts[^1].X, zeroY), new(pts[0].X, zeroY) };
                g.FillPolygon(wash, area.ToArray());
                g.DrawLines(pen, pts.ToArray());
            }
        }
        g.ResetClip();

        // Hover: crosshair + point on the nearest sample (the readout goes in the caption row).
        if (Nearest(r) is { } hit)
        {
            float hx = X(hit.Time), hy = Y(hit.Watts);
            using var cross = new Pen(Color.FromArgb(120, Ink), 1);
            g.DrawLine(cross, hx, r.Top, hx, r.Bottom);
            FillDot(g, Bg, hx, hy, Px(5));
            FillDot(g, hit.Watts >= 0 ? PowerIn : PowerOut, hx, hy, Px(3.5f));
        }
        g.SmoothingMode = oldMode;

        // Scale labels in ink, tucked into the corners.
        const TextFormatFlags small = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine;
        if (top > 0) TextRenderer.DrawText(g, $"{top:0} W", smallFont, new Point(r.Left + Px(2), r.Top + Px(3)), Muted, small);
        if (bottom > 0) TextRenderer.DrawText(g, $"−{bottom:0} W", smallFont, new Point(r.Left + Px(2), r.Bottom - smallFont.Height - Px(3)), Muted, small);
    }

    void DrawGraphCaption(Graphics? g, int x, int y, int width, TextFormatFlags flags)
    {
        if (g is null) return;
        if (Nearest(graphBounds) is { } hit)
        {
            var ago = DateTime.UtcNow - hit.Time;
            DrawText(g, hit.Watts.ToString(Signed2) + " W", smallFont, x, y, width, Ink, flags);
            DrawText(g, ago.TotalSeconds < 2 ? "now" : Span(ago) + (ago.TotalMinutes >= 1 ? $" {ago.Seconds:00}s" : "") + " ago",
                smallFont, x, y, width, Muted, flags | TextFormatFlags.Right);
        }
        else
        {
            DrawText(g, $"last {history.Window.TotalMinutes:0} min", smallFont, x, y, width, Muted, flags);
            DrawText(g, "now", smallFont, x, y, width, Muted, flags | TextFormatFlags.Right);
        }
    }

    (DateTime Time, double Watts)? Nearest(Rectangle r)
    {
        if (hoverX is not int hx || r.IsEmpty) return null;
        var t = DateTime.UtcNow - history.Window + TimeSpan.FromSeconds((hx - r.Left) / (double)r.Width * history.Window.TotalSeconds);
        (DateTime Time, double Watts)? best = null;
        foreach (var s in history.Samples)
            if (best is null || Math.Abs((s.Time - t).Ticks) < Math.Abs((best.Value.Time - t).Ticks))
                best = s;
        return best is { } b && Math.Abs((b.Time - t).TotalSeconds) <= 15 ? b : null;
    }

    static double NiceCeiling(double v)
    {
        if (v <= 0) return 0;
        foreach (double step in (double[])[5, 10, 15, 20, 30, 45, 60, 90, 120, 150, 200, 250, 300])
            if (v <= step) return step;
        return Math.Ceiling(v / 100) * 100;
    }

    void DrawChevron(Graphics? g, float cx, float cy, bool collapsed)
    {
        if (g is null) return;
        float s = Px(3.5f);
        PointF[] pts = collapsed
            ? [new(cx - s / 2, cy - s), new(cx + s / 2, cy), new(cx - s / 2, cy + s)]
            : [new(cx - s, cy - s / 2), new(cx, cy + s / 2), new(cx + s, cy - s / 2)];
        var oldMode = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var pen = new Pen(Muted, Math.Max(1f, Px(1.5f))) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round };
        g.DrawLines(pen, pts);
        g.SmoothingMode = oldMode;
    }

    static void FillDot(Graphics? g, Color color, float cx, float cy, float radius)
    {
        if (g is null) return;
        var oldMode = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var brush = new SolidBrush(color);
        g.FillEllipse(brush, cx - radius, cy - radius, radius * 2, radius * 2);
        g.SmoothingMode = oldMode;
    }

    static void DrawText(Graphics? g, string text, Font font, int x, int y, int width, Color color, TextFormatFlags flags)
    {
        if (g is not null)
            TextRenderer.DrawText(g, text, font, new Rectangle(x, y, width, font.Height + 4), color, flags);
    }

    static readonly Dictionary<Color, SolidBrush> brushCache = [];
    static SolidBrush BrushFor(Color c) =>
        brushCache.TryGetValue(c, out var b) ? b : brushCache[c] = new SolidBrush(c);

    // ---- mouse: graph hover, collapsible sections ----

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int? hx = graphBounds.Contains(e.Location) ? e.X : null;
        if (hx != hoverX)
        {
            hoverX = hx;
            Invalidate();
        }
        Cursor = sectionHits.Any(h => h.Bounds.Contains(e.Location)) ? Cursors.Hand : Cursors.Default;
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (hoverX is null) return;
        hoverX = null;
        Invalidate();
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (e.Button != MouseButtons.Left) return;
        string? key = sectionHits.FirstOrDefault(h => h.Bounds.Contains(e.Location)).Key;
        if (key is null) return;
        if (!settings.Collapsed.Remove(key)) settings.Collapsed.Add(key);
        settings.Save();
        ResizeToContent();
        Invalidate();
    }

    // ---- tray icon: live wattage as a number, with a polarity bar underneath ----

    void UpdateTray(BatteryReading? total)
    {
        string tip = $"Whatts · {headline} · {subline.Replace(Dot, " · ")}";
        tray.Text = tip.Length > 63 ? tip[..63] : tip; // NotifyIcon caps tooltip length

        bool light = TaskbarIsLight();
        Color ink = light ? Color.FromArgb(0x0B, 0x0B, 0x0B) : Color.White;
        Color? bar = total is null ? null : PolarityColor(total.State, light);
        string label = total?.Watts is double w ? Math.Round(Math.Abs(w)).ToString("0") : "–";

        string key = $"{label}|{light}|{bar?.ToArgb()}";
        if (key == trayKey) return;
        trayKey = key;

        int size = SystemInformation.SmallIconSize.Width;
        int barHeight = bar is null ? 0 : Math.Max(2, size / 8);
        int textHeight = size - (barHeight > 0 ? barHeight + 1 : 0);

        using var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var path = new GraphicsPath();
            using var family = new FontFamily("Segoe UI");
            path.AddString(label, family, (int)FontStyle.Bold, 100, PointF.Empty, StringFormat.GenericTypographic);
            var b = path.GetBounds();
            float scale = Math.Min((size - 1) / b.Width, textHeight / b.Height);
            using var m = new Matrix();
            m.Translate((size - b.Width * scale) / 2f, (textHeight - b.Height * scale) / 2f);
            m.Scale(scale, scale);
            m.Translate(-b.X, -b.Y);
            path.Transform(m);
            using var inkBrush = new SolidBrush(ink);
            g.FillPath(inkBrush, path);

            if (bar is Color c)
            {
                using var barBrush = new SolidBrush(c);
                g.FillRectangle(barBrush, 1, size - barHeight, size - 2, barHeight);
            }
        }

        IntPtr handle = bmp.GetHicon();
        var oldIcon = tray.Icon;
        IntPtr oldHandle = trayIconHandle;
        tray.Icon = Icon.FromHandle(handle);
        trayIconHandle = handle;
        oldIcon?.Dispose();
        if (oldHandle != IntPtr.Zero) DestroyIcon(oldHandle);
    }

    static bool TaskbarIsLight() =>
        Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
            "SystemUsesLightTheme", 0) is int v && v != 0;

    // ---- interop ----

    const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("user32.dll")]
    static extern bool DestroyIcon(IntPtr handle);

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint key);

    [DllImport("user32.dll")]
    static extern bool UnregisterHotKey(IntPtr hwnd, int id);

    [DllImport("user32.dll")]
    static extern IntPtr GetForegroundWindow();
}
