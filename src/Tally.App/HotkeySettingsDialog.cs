using System.Drawing;

namespace Tally.App;

/// <summary>
/// A small dark dialog to rebind the timer start/stop hotkeys by pressing a key combination.
/// Use the static <see cref="Configure"/> entry point; it loads the current values, saves the
/// new ones to settings.json, and re-registers them live if a listener is supplied.
/// </summary>
public sealed class HotkeySettingsDialog : Form
{
    private static readonly Color Bg = Color.FromArgb(0x16, 0x18, 0x1c);
    private static readonly Color InputBg = Color.FromArgb(0x2a, 0x2e, 0x35);
    private static readonly Color Accent = Color.FromArgb(0x2f, 0xd4, 0xb6);
    private static readonly Color AccentFg = Color.FromArgb(0x08, 0x20, 0x1c);
    private static readonly Color Fg = Color.FromArgb(0xe6, 0xe9, 0xec);
    private static readonly Color MutedFg = Color.FromArgb(0x9a, 0xa4, 0xae);

    private readonly HotkeyCapture _start;
    private readonly HotkeyCapture _stop;
    private readonly Label _error;

    public string StartSpec => _start.Spec;
    public string StopSpec => _stop.Spec;

    private HotkeySettingsDialog(string startSpec, string stopSpec)
    {
        Text = "Timer hotkeys";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        MaximizeBox = false;
        MinimizeBox = false;
        BackColor = Bg;
        ForeColor = Fg;
        ClientSize = new Size(360, 210);
        try { Icon = new Icon(Path.Combine(AppContext.BaseDirectory, "Assets", "tally.ico")); } catch { /* default icon */ }

        _start = new HotkeyCapture(startSpec) { Location = new Point(120, 24), Width = 210 };
        _stop = new HotkeyCapture(stopSpec) { Location = new Point(120, 62), Width = 210 };

        Controls.Add(MakeLabel("Start timer", 27));
        Controls.Add(MakeLabel("Stop timer", 65));
        Controls.Add(_start);
        Controls.Add(_stop);

        var hint = new Label
        {
            Text = "Click a field, then press a combination (Ctrl/Alt/Shift + a key).",
            ForeColor = MutedFg,
            AutoSize = false,
            Location = new Point(20, 104),
            Size = new Size(320, 34),
        };
        Controls.Add(hint);

        _error = new Label { ForeColor = Color.FromArgb(0xff, 0x8a, 0x8a), AutoSize = false, Location = new Point(20, 138), Size = new Size(320, 18) };
        Controls.Add(_error);

        var save = new Button { Text = "Save", DialogResult = DialogResult.None, FlatStyle = FlatStyle.Flat, BackColor = Accent, ForeColor = AccentFg, Location = new Point(180, 168), Size = new Size(72, 28), Cursor = Cursors.Hand };
        save.FlatAppearance.BorderSize = 0;
        save.Click += OnSave;
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, FlatStyle = FlatStyle.Flat, BackColor = InputBg, ForeColor = Fg, Location = new Point(258, 168), Size = new Size(80, 28), Cursor = Cursors.Hand };
        cancel.FlatAppearance.BorderSize = 0;
        Controls.Add(save);
        Controls.Add(cancel);
        AcceptButton = save;
        CancelButton = cancel;
    }

    private void OnSave(object? sender, EventArgs e)
    {
        if (!HotkeySpec.TryParse(_start.Spec, out _, out _) || !HotkeySpec.TryParse(_stop.Spec, out _, out _))
        {
            _error.Text = "Each hotkey needs a modifier (Ctrl/Alt/Shift/Win) plus a key.";
            return;
        }

        if (string.Equals(_start.Spec, _stop.Spec, StringComparison.OrdinalIgnoreCase))
        {
            _error.Text = "Start and stop must be different.";
            return;
        }

        DialogResult = DialogResult.OK;   // closes the modal dialog
    }

    private static Label MakeLabel(string text, int y)
        => new() { Text = text, ForeColor = MutedFg, AutoSize = true, Location = new Point(20, y) };

    /// <summary>Opens the dialog; on Save, persists to settings.json and rebinds a live listener.</summary>
    public static void Configure(IWin32Window? owner, HotkeyListener? listener)
    {
        var settings = TallySettings.LoadOrCreate(TallyPaths.SettingsPath);
        using var dialog = new HotkeySettingsDialog(settings.TimerStartHotkey, settings.TimerStopHotkey);
        if (dialog.ShowDialog(owner) != DialogResult.OK)
            return;

        SettingsWriter.UpdateHotkeys(TallyPaths.SettingsPath, dialog.StartSpec, dialog.StopSpec);
        listener?.Rebind(dialog.StartSpec, dialog.StopSpec);
        Log.Info($"Timer hotkeys set to start='{dialog.StartSpec}' stop='{dialog.StopSpec}'");
    }

    /// <summary>A read-only field that records the next Ctrl/Alt/Shift/Win + key combination pressed.</summary>
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
            // Intercept here so combos like Alt+F4 are captured rather than acting on the dialog.
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
            var key = keyData & Keys.KeyCode;
            var name = KeyName(key);
            if (name is null)
                return false;

            var parts = new List<string>();
            if ((keyData & Keys.Control) != 0) parts.Add("Ctrl");
            if ((keyData & Keys.Alt) != 0) parts.Add("Alt");
            if ((keyData & Keys.Shift) != 0) parts.Add("Shift");
            if (parts.Count == 0)
                return false;   // require a modifier

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
