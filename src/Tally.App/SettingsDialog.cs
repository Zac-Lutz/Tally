using System.Drawing;

namespace Tally.App;

/// <summary>
/// The app's Settings dialog: rebind the timer start/stop hotkeys and configure one or more
/// auto-report times. Use the static <see cref="Configure"/> entry point; it loads current values,
/// saves changes to settings.json, rebinds a live hotkey listener, and calls <c>onSaved</c> so the
/// tray can reschedule auto-reports.
/// </summary>
public sealed class SettingsDialog : Form
{
    private static readonly Color Bg = Color.FromArgb(0x16, 0x18, 0x1c);
    private static readonly Color InputBg = Color.FromArgb(0x2a, 0x2e, 0x35);
    private static readonly Color Accent = Color.FromArgb(0x2f, 0xd4, 0xb6);
    private static readonly Color AccentFg = Color.FromArgb(0x08, 0x20, 0x1c);
    private static readonly Color Fg = Color.FromArgb(0xe6, 0xe9, 0xec);
    private static readonly Color MutedFg = Color.FromArgb(0x9a, 0xa4, 0xae);
    private static readonly Color Border = Color.FromArgb(0x2c, 0x31, 0x38);

    private readonly HotkeyCapture _start;
    private readonly HotkeyCapture _stop;
    private readonly ListBox _timesList = new();
    private readonly DateTimePicker _timePicker = new();
    private readonly List<TimeOnly> _times = [];
    private readonly NumericUpDown _retentionDays = new();
    private readonly CheckBox _keepForever = new();
    private readonly Label _error;

    public string StartSpec => _start.Spec;
    public string StopSpec => _stop.Spec;
    public IReadOnlyList<string> AutoReportTimes => _times.Select(t => t.ToString("HH:mm")).ToList();
    public int EventRetentionDays => _keepForever.Checked ? 0 : (int)_retentionDays.Value;

    private SettingsDialog(TallySettings settings)
    {
        Text = "Settings";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        MaximizeBox = false;
        MinimizeBox = false;
        BackColor = Bg;
        ForeColor = Fg;
        ClientSize = new Size(400, 560);
        try { Icon = new Icon(Path.Combine(AppContext.BaseDirectory, "Assets", "tally.ico")); } catch { /* default icon */ }

        Controls.Add(Header("Timer hotkeys", 16));
        _start = new HotkeyCapture(settings.TimerStartHotkey) { Location = new Point(150, 44), Width = 220 };
        _stop = new HotkeyCapture(settings.TimerStopHotkey) { Location = new Point(150, 78), Width = 220 };
        Controls.Add(Field("Start timer", 47));
        Controls.Add(Field("Stop timer", 81));
        Controls.Add(_start);
        Controls.Add(_stop);

        Controls.Add(new Panel { BackColor = Border, Location = new Point(20, 120), Size = new Size(360, 1) });

        Controls.Add(Header("Auto-generate reports at these times", 136));

        _timesList.SetBounds(20, 164, 200, 116);
        _timesList.BackColor = InputBg;
        _timesList.ForeColor = Fg;
        _timesList.BorderStyle = BorderStyle.FixedSingle;
        Controls.Add(_timesList);

        _timePicker.Format = DateTimePickerFormat.Custom;
        _timePicker.CustomFormat = "hh:mm tt";   // minute precision, no seconds
        _timePicker.ShowUpDown = true;
        _timePicker.SetBounds(238, 164, 110, 24);
        Controls.Add(_timePicker);

        var add = MakeButton("Add time");
        add.SetBounds(238, 196, 110, 28);
        add.Click += (_, _) => AddTime();
        Controls.Add(add);

        var remove = MakeButton("Remove");
        remove.SetBounds(238, 230, 110, 28);
        remove.Click += (_, _) => RemoveSelected();
        Controls.Add(remove);

        Controls.Add(new Label
        {
            Text = "Add several times for multiple snapshots a day. No times = auto-reports off.",
            ForeColor = MutedFg,
            AutoSize = false,
            Location = new Point(20, 292),
            Size = new Size(360, 34),
        });

        Controls.Add(new Panel { BackColor = Border, Location = new Point(20, 334), Size = new Size(360, 1) });

        Controls.Add(Header("Keep raw history", 348));

        Controls.Add(Field("Delete raw activity older than", 381));
        _retentionDays.SetBounds(196, 377, 56, 24);
        _retentionDays.Minimum = Tally.Core.RetentionPolicy.MinimumDays;
        _retentionDays.Maximum = 3650;
        _retentionDays.BackColor = InputBg;
        _retentionDays.ForeColor = Fg;
        _retentionDays.BorderStyle = BorderStyle.FixedSingle;
        Controls.Add(_retentionDays);
        var daysLabel = Field("days", 381);
        daysLabel.Location = new Point(258, 381);
        Controls.Add(daysLabel);

        _keepForever.Text = "Keep everything forever";
        _keepForever.ForeColor = Fg;
        _keepForever.AutoSize = true;
        _keepForever.Location = new Point(20, 407);
        _keepForever.CheckedChanged += (_, _) => _retentionDays.Enabled = !_keepForever.Checked;
        Controls.Add(_keepForever);

        Controls.Add(new Label
        {
            Text = "Saved reports and timers are never deleted.",
            ForeColor = MutedFg,
            AutoSize = false,
            Location = new Point(20, 433),
            Size = new Size(360, 18),
        });

        _error = new Label { ForeColor = Color.FromArgb(0xff, 0x8a, 0x8a), AutoSize = false, Location = new Point(20, 460), Size = new Size(360, 18) };
        Controls.Add(_error);

        var save = MakeButton("Save");
        save.SetBounds(238, 516, 72, 30);
        save.Click += OnSave;
        var cancel = MakeButton("Cancel");
        cancel.SetBounds(316, 516, 64, 30);
        cancel.DialogResult = DialogResult.Cancel;
        Controls.Add(save);
        Controls.Add(cancel);
        AcceptButton = save;
        CancelButton = cancel;

        foreach (var t in settings.ResolveAutoReportTimes())
            _times.Add(t);
        RefreshTimes();

        var retention = settings.ResolveEventRetentionDays();
        _keepForever.Checked = retention <= 0;
        _retentionDays.Value = retention <= 0
            ? TallySettings.DefaultEventRetentionDays
            : Math.Clamp(retention, (int)_retentionDays.Minimum, (int)_retentionDays.Maximum);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        DarkTitleBar.Apply(Handle);   // dark title bar, matching the live window
    }

    private void AddTime()
    {
        var picked = _timePicker.Value;
        var t = new TimeOnly(picked.Hour, picked.Minute);   // snap to the minute
        if (_times.Contains(t))
            return;
        _times.Add(t);
        _times.Sort();
        RefreshTimes();
    }

    private void RemoveSelected()
    {
        var i = _timesList.SelectedIndex;
        if (i >= 0)
        {
            _times.RemoveAt(i);
            RefreshTimes();
        }
    }

    private void RefreshTimes()
    {
        _timesList.Items.Clear();
        foreach (var t in _times)
            _timesList.Items.Add(t.ToString("h:mm tt"));
    }

    private void OnSave(object? sender, EventArgs e)
    {
        if (!HotkeySpec.TryParse(_start.Spec, out _, out _) || !HotkeySpec.TryParse(_stop.Spec, out _, out _))
        {
            _error.Text = "Each hotkey needs a modifier (Ctrl/Alt/Shift) plus a key.";
            return;
        }

        if (string.Equals(_start.Spec, _stop.Spec, StringComparison.OrdinalIgnoreCase))
        {
            _error.Text = "Start and stop hotkeys must be different.";
            return;
        }

        DialogResult = DialogResult.OK;   // closes the modal dialog
    }

    private static Label Header(string text, int y)
        => new() { Text = text, ForeColor = Fg, AutoSize = true, Font = new Font("Segoe UI Semibold", 9.5f, FontStyle.Bold), Location = new Point(20, y) };

    private static Label Field(string text, int y)
        => new() { Text = text, ForeColor = MutedFg, AutoSize = true, Location = new Point(20, y) };

    // Dark by default, accent on hover (dark text for contrast) — matching the live window.
    private static Button MakeButton(string text)
    {
        var b = new Button { Text = text, FlatStyle = FlatStyle.Flat, BackColor = InputBg, ForeColor = Fg, UseVisualStyleBackColor = false, Cursor = Cursors.Hand };
        b.FlatAppearance.BorderSize = 0;
        b.FlatAppearance.MouseOverBackColor = Accent;
        b.FlatAppearance.MouseDownBackColor = Accent;
        b.MouseEnter += (_, _) => b.ForeColor = AccentFg;
        b.MouseLeave += (_, _) => b.ForeColor = Fg;
        return b;
    }

    /// <summary>Opens the dialog; on Save persists to settings.json, rebinds a live listener, and calls onSaved.</summary>
    public static void Configure(IWin32Window? owner, HotkeyListener? listener, Action? onSaved = null)
    {
        var settings = TallySettings.LoadOrCreate(TallyPaths.SettingsPath);
        using var dialog = new SettingsDialog(settings);
        if (dialog.ShowDialog(owner) != DialogResult.OK)
            return;

        SettingsWriter.UpdateSettings(TallyPaths.SettingsPath, dialog.StartSpec, dialog.StopSpec, dialog.AutoReportTimes, dialog.EventRetentionDays);
        listener?.Rebind(dialog.StartSpec, dialog.StopSpec);
        onSaved?.Invoke();
        Log.Info($"Settings saved: hotkeys start='{dialog.StartSpec}' stop='{dialog.StopSpec}', auto-report times=[{string.Join(", ", dialog.AutoReportTimes)}], retention={dialog.EventRetentionDays}d");
    }

    /// <summary>A read-only field that records the next Ctrl/Alt/Shift + key combination pressed.</summary>
    private sealed class HotkeyCapture : TextBox
    {
        public string Spec { get; private set; }

        public HotkeyCapture(string initial)
        {
            Spec = initial;
            Text = initial;
            ReadOnly = true;
            BorderStyle = BorderStyle.FixedSingle;
            BackColor = InputBg;
            ForeColor = Fg;
            Cursor = Cursors.Hand;
            TextAlign = HorizontalAlignment.Center;
            GotFocus += (_, _) => Text = "Press keys…";
            LostFocus += (_, _) => Text = Spec;
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (Focused && TryBuildSpec(keyData, out var spec))
            {
                Spec = spec;
                Text = spec;
                return true;
            }

            return base.ProcessCmdKey(ref msg, keyData);
        }

        private static bool TryBuildSpec(Keys keyData, out string spec)
        {
            spec = string.Empty;
            var name = KeyName(keyData & Keys.KeyCode);
            if (name is null)
                return false;

            var parts = new List<string>();
            if ((keyData & Keys.Control) != 0) parts.Add("Ctrl");
            if ((keyData & Keys.Alt) != 0) parts.Add("Alt");
            if ((keyData & Keys.Shift) != 0) parts.Add("Shift");
            if (parts.Count == 0)
                return false;

            parts.Add(name);
            spec = string.Join("+", parts);
            return true;
        }

        private static string? KeyName(Keys key) => key switch
        {
            >= Keys.A and <= Keys.Z => key.ToString(),
            >= Keys.D0 and <= Keys.D9 => ((char)('0' + (key - Keys.D0))).ToString(),
            >= Keys.F1 and <= Keys.F24 => key.ToString(),
            _ => null,
        };
    }
}
