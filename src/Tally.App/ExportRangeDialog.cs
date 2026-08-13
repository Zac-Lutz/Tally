using System.Drawing;
using Tally.Core;

namespace Tally.App;

/// <summary>
/// Asks which slice of the day to export before the file is written. Defaults to the whole day;
/// narrowing it is for filing a day in parts — the morning at lunch, the afternoon at close.
/// <para>
/// The entry count and hours are recalculated against the real slots as the range changes, so the
/// choice is made against what it will actually produce rather than against the clock alone.
/// </para>
/// </summary>
public sealed class ExportRangeDialog : Form
{
    private static readonly Color Bg = Color.FromArgb(0x16, 0x18, 0x1c);
    private static readonly Color InputBg = Color.FromArgb(0x2a, 0x2e, 0x35);
    private static readonly Color Accent = Color.FromArgb(0x2f, 0xd4, 0xb6);
    private static readonly Color AccentFg = Color.FromArgb(0x08, 0x20, 0x1c);
    private static readonly Color Fg = Color.FromArgb(0xe6, 0xe9, 0xec);
    private static readonly Color MutedFg = Color.FromArgb(0x9a, 0xa4, 0xae);
    private static readonly Color WarnFg = Color.FromArgb(0xd6, 0x9e, 0x2e);

    private readonly IReadOnlyList<SuggestionSlot> _all;
    private readonly CheckBox _wholeDay = new();
    private readonly DateTimePicker _from = new();
    private readonly DateTimePicker _to = new();
    private readonly Label _fromLabel;
    private readonly Label _toLabel;
    private readonly Label _summary;
    private readonly Label _replaceNote;

    /// <summary>The chosen slice — both null when the whole day is selected.</summary>
    public TimeOnly? From => _wholeDay.Checked ? null : Snap(_from.Value);

    public TimeOnly? To => _wholeDay.Checked ? null : Snap(_to.Value);

    private ExportRangeDialog(IReadOnlyList<SuggestionSlot> all)
    {
        _all = all;

        Text = "Export timesheet";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        BackColor = Bg;
        ForeColor = Fg;
        ClientSize = new Size(420, 268);
        try { Icon = new Icon(Path.Combine(AppContext.BaseDirectory, "Assets", "tally.ico")); } catch { /* default icon */ }

        Controls.Add(new Label
        {
            Text = "How much of the day should this export cover?",
            ForeColor = Fg,
            AutoSize = true,
            Font = new Font("Segoe UI Semibold", 9.5f, FontStyle.Bold),
            Location = new Point(20, 18),
        });

        _wholeDay.Text = "Everything so far";
        _wholeDay.Checked = true;
        _wholeDay.ForeColor = Fg;
        _wholeDay.AutoSize = true;
        _wholeDay.Location = new Point(20, 50);
        _wholeDay.CheckedChanged += (_, _) => Sync();
        Controls.Add(_wholeDay);

        _fromLabel = Field("From", 86);
        _toLabel = Field("to", 86);
        _fromLabel.Location = new Point(20, 86);
        _toLabel.Location = new Point(196, 86);
        Controls.Add(_fromLabel);
        Controls.Add(_toLabel);

        // Bounded by the day's own first and last entry, so the pickers open on a sensible range
        // rather than at midnight, and a plain OK can never exclude work by accident.
        var first = all.Count > 0 ? all.Min(s => s.Start).ToLocalTime().DateTime : DateTime.Today;
        var last = all.Count > 0 ? all.Max(s => s.End).ToLocalTime().DateTime : DateTime.Now;

        StylePicker(_from, first, new Point(62, 82));
        StylePicker(_to, last, new Point(224, 82));
        Controls.Add(_from);
        Controls.Add(_to);

        _summary = new Label
        {
            ForeColor = Accent,
            AutoSize = false,
            Font = new Font("Segoe UI Semibold", 10f, FontStyle.Bold),
            Location = new Point(20, 126),
            Size = new Size(380, 22),
        };
        Controls.Add(_summary);

        Controls.Add(new Label
        {
            Text = "An entry belongs to the window it started in, so two exports never count the same meeting twice.",
            ForeColor = MutedFg,
            AutoSize = false,
            Location = new Point(20, 150),
            Size = new Size(380, 32),
        });

        _replaceNote = new Label
        {
            Text = "Importing replaces that day's suggestions in att — log the entries you want before uploading a later slice.",
            ForeColor = WarnFg,
            AutoSize = false,
            Location = new Point(20, 182),
            Size = new Size(380, 32),
        };
        Controls.Add(_replaceNote);

        var export = MakeButton("Export…");
        export.SetBounds(236, 226, 92, 30);
        export.DialogResult = DialogResult.OK;
        var cancel = MakeButton("Cancel");
        cancel.SetBounds(336, 226, 64, 30);
        cancel.DialogResult = DialogResult.Cancel;
        Controls.Add(export);
        Controls.Add(cancel);
        AcceptButton = export;
        CancelButton = cancel;

        Sync();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        DarkTitleBar.Apply(Handle);   // dark title bar, matching the live window
    }

    /// <summary>The slots this dialog's current range would export.</summary>
    public IReadOnlyList<SuggestionSlot> Selected
        => _all.Where(s => SuggestionSlotBuilder.InWindow(s, From, To)).ToList();

    private void Sync()
    {
        var custom = !_wholeDay.Checked;
        _from.Enabled = _to.Enabled = custom;
        _fromLabel.ForeColor = _toLabel.ForeColor = custom ? MutedFg : Color.FromArgb(0x5a, 0x62, 0x6a);
        _replaceNote.Visible = custom;

        var slots = Selected;
        _summary.Text = slots.Count == 0
            ? "Nothing starts inside that window."
            : $"{slots.Count} {(slots.Count == 1 ? "entry" : "entries")} · {slots.Sum(s => s.Reported.TotalHours):0.00} h";
        _summary.ForeColor = slots.Count == 0 ? WarnFg : Accent;
    }

    private void StylePicker(DateTimePicker picker, DateTime value, Point location)
    {
        picker.Format = DateTimePickerFormat.Custom;
        picker.CustomFormat = "hh:mm tt";   // minute precision, no seconds
        picker.ShowUpDown = true;
        picker.Value = value;
        picker.SetBounds(location.X, location.Y, 116, 24);
        picker.ValueChanged += (_, _) => Sync();
    }

    private static TimeOnly Snap(DateTime value) => new(value.Hour, value.Minute);

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

    /// <summary>
    /// Asks for the range. Returns the chosen window, or null if the user cancelled or the
    /// selection turned out to be empty.
    /// </summary>
    public static SuggestionSlotOptions? Ask(IWin32Window? owner, IReadOnlyList<SuggestionSlot> all)
    {
        using var dialog = new ExportRangeDialog(all);
        if (dialog.ShowDialog(owner) != DialogResult.OK || dialog.Selected.Count == 0)
            return null;

        return new SuggestionSlotOptions { WindowStart = dialog.From, WindowEnd = dialog.To };
    }
}
