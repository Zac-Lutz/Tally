using System.Drawing;
using System.Globalization;
using Tally.Core;

namespace Tally.App;

/// <summary>What the export dialog settled on: the (possibly edited) entries and the chosen window.</summary>
public sealed record ExportSelection(IReadOnlyList<ExportEntry> Entries, TimeOnly? From, TimeOnly? To);

/// <summary>
/// The export window: choose which slice of the day to file, see exactly the entries the file
/// will carry — times, hours, ticket, title, description — and edit any of them before anything
/// is written, so what arrives in att is ready to review and log.
/// <para>
/// The grid always shows the current range's entries; edits live on the entries themselves, so
/// narrowing or widening the range never loses them. A title or ticket edit recomposes the
/// description automatically until the description itself is hand-edited — then the reviewer's
/// text wins.
/// </para>
/// </summary>
public sealed class ExportRangeDialog : Form
{
    private static readonly Color Bg = Color.FromArgb(0x16, 0x18, 0x1c);
    private static readonly Color InputBg = Color.FromArgb(0x2a, 0x2e, 0x35);
    private static readonly Color GridBg = Color.FromArgb(0x1e, 0x21, 0x26);
    private static readonly Color Border = Color.FromArgb(0x2c, 0x31, 0x38);
    private static readonly Color Accent = Color.FromArgb(0x2f, 0xd4, 0xb6);
    private static readonly Color AccentFg = Color.FromArgb(0x08, 0x20, 0x1c);
    private static readonly Color Fg = Color.FromArgb(0xe6, 0xe9, 0xec);
    private static readonly Color MutedFg = Color.FromArgb(0x9a, 0xa4, 0xae);
    private static readonly Color WarnFg = Color.FromArgb(0xd6, 0x9e, 0x2e);

    private readonly List<ExportEntry> _all;
    private readonly HashSet<SuggestionSlot> _noteEdited = [];
    private readonly CheckBox _wholeDay = new();
    private readonly DateTimePicker _from = new();
    private readonly DateTimePicker _to = new();
    private readonly Label _fromLabel;
    private readonly Label _toLabel;
    private readonly Label _summary;
    private readonly Label _replaceNote;
    private readonly DataGridView _grid = new();
    private bool _rebinding;

    private const int ColTime = 0;
    private const int ColHours = 1;
    private const int ColTicket = 2;
    private const int ColTitle = 3;
    private const int ColNote = 4;

    /// <summary>The chosen slice — both null when the whole day is selected.</summary>
    public TimeOnly? From => _wholeDay.Checked ? null : Snap(_from.Value);

    public TimeOnly? To => _wholeDay.Checked ? null : Snap(_to.Value);

    private ExportRangeDialog(IReadOnlyList<ExportEntry> all)
    {
        _all = all.ToList();

        Text = "Export timesheet";
        FormBorderStyle = FormBorderStyle.Sizable;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = true;
        MinimizeBox = false;
        BackColor = Bg;
        ForeColor = Fg;
        ClientSize = new Size(940, 560);
        MinimumSize = new Size(720, 420);
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
        _wholeDay.Location = new Point(20, 46);
        _wholeDay.CheckedChanged += (_, _) => Sync();
        Controls.Add(_wholeDay);

        _fromLabel = Field("From", 50);
        _toLabel = Field("to", 50);
        _fromLabel.Location = new Point(190, 50);
        _toLabel.Location = new Point(366, 50);
        Controls.Add(_fromLabel);
        Controls.Add(_toLabel);

        // Bounded by the day's own first and last entry, so the pickers open on a sensible range
        // rather than at midnight, and a plain OK can never exclude work by accident.
        var first = _all.Count > 0 ? _all.Min(e => e.Slot.Start).ToLocalTime().DateTime : DateTime.Today;
        var last = _all.Count > 0 ? _all.Max(e => e.Slot.End).ToLocalTime().DateTime : DateTime.Now;

        StylePicker(_from, first, new Point(232, 46));
        StylePicker(_to, last, new Point(394, 46));
        Controls.Add(_from);
        Controls.Add(_to);

        _summary = new Label
        {
            ForeColor = Accent,
            AutoSize = false,
            Font = new Font("Segoe UI Semibold", 10f, FontStyle.Bold),
            Location = new Point(540, 48),
            Size = new Size(380, 22),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
        };
        Controls.Add(_summary);

        BuildGrid();
        Controls.Add(_grid);

        Controls.Add(new Label
        {
            Text = "This is exactly what the file will carry — edit hours, ticket, title, or description in place. Editing the title or ticket rewrites the description for you until you've edited the description yourself. An entry belongs to the window it started in, so two exports never count the same meeting twice.",
            ForeColor = MutedFg,
            AutoSize = false,
            Location = new Point(20, 466),
            Size = new Size(760, 46),
            Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
        });

        _replaceNote = new Label
        {
            Text = "Importing replaces that day's suggestions in att — log the entries you want before uploading a later slice.",
            ForeColor = WarnFg,
            AutoSize = false,
            Location = new Point(20, 512),
            Size = new Size(760, 20),
            Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
        };
        Controls.Add(_replaceNote);

        var export = MakeButton("Export…");
        export.SetBounds(756, 518, 92, 30);
        export.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
        export.DialogResult = DialogResult.OK;
        var cancel = MakeButton("Cancel");
        cancel.SetBounds(856, 518, 64, 30);
        cancel.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
        cancel.DialogResult = DialogResult.Cancel;
        Controls.Add(export);
        Controls.Add(cancel);
        AcceptButton = export;
        CancelButton = cancel;

        Sync();
    }

    private void BuildGrid()
    {
        _grid.SetBounds(20, 84, 900, 372);
        _grid.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        _grid.AllowUserToAddRows = false;
        _grid.AllowUserToDeleteRows = false;
        _grid.AllowUserToResizeRows = false;
        _grid.RowHeadersVisible = false;
        _grid.MultiSelect = false;
        _grid.BorderStyle = BorderStyle.None;
        _grid.BackgroundColor = GridBg;
        _grid.GridColor = Border;
        _grid.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
        _grid.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.Single;
        _grid.EnableHeadersVisualStyles = false;
        _grid.ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle
        {
            BackColor = Bg,
            ForeColor = MutedFg,
            SelectionBackColor = Bg,
            SelectionForeColor = MutedFg,
            Font = new Font("Segoe UI Semibold", 8.5f, FontStyle.Bold),
        };
        _grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize;
        _grid.DefaultCellStyle = new DataGridViewCellStyle
        {
            BackColor = GridBg,
            ForeColor = Fg,
            SelectionBackColor = InputBg,
            SelectionForeColor = Fg,
        };
        _grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None;

        _grid.Columns.Add(ReadOnlyColumn("Time", 130));
        _grid.Columns.Add(EditColumn("Hours", 56));
        _grid.Columns.Add(EditColumn("Ticket", 70));
        _grid.Columns.Add(EditColumn("Title", 240));
        var note = EditColumn("Description", 380);
        note.AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
        _grid.Columns.Add(note);

        _grid.CellValueChanged += OnCellValueChanged;
    }

    private static DataGridViewTextBoxColumn ReadOnlyColumn(string header, int width)
    {
        var column = EditColumn(header, width);
        column.ReadOnly = true;
        column.DefaultCellStyle.ForeColor = MutedFg;
        return column;
    }

    private static DataGridViewTextBoxColumn EditColumn(string header, int width)
        => new() { HeaderText = header, Width = width, SortMode = DataGridViewColumnSortMode.NotSortable };

    /// <summary>The entries this dialog's current range would export — edits included.</summary>
    public IReadOnlyList<ExportEntry> Selected
        => _all.Where(e => SuggestionSlotBuilder.InWindow(e.Slot, From, To)).ToList();

    private void Sync()
    {
        var custom = !_wholeDay.Checked;
        _from.Enabled = _to.Enabled = custom;
        _fromLabel.ForeColor = _toLabel.ForeColor = custom ? MutedFg : Color.FromArgb(0x5a, 0x62, 0x6a);
        _replaceNote.Visible = custom;

        var entries = Selected;
        _summary.Text = entries.Count == 0
            ? "Nothing starts inside that window."
            : $"{entries.Count} {(entries.Count == 1 ? "entry" : "entries")} · {entries.Sum(e => e.Hours):0.00} h";
        _summary.ForeColor = entries.Count == 0 ? WarnFg : Accent;

        Rebind(entries);
    }

    private void Rebind(IReadOnlyList<ExportEntry> entries)
    {
        _rebinding = true;
        try
        {
            _grid.Rows.Clear();
            foreach (var entry in entries)
            {
                var start = entry.Slot.Start.ToLocalTime();
                var end = entry.Slot.End.ToLocalTime();
                var row = _grid.Rows[_grid.Rows.Add(
                    $"{start:h:mmtt}–{end:h:mmtt}".ToLowerInvariant(),
                    entry.Hours.ToString("0.00", CultureInfo.InvariantCulture),
                    entry.Ticket ?? string.Empty,
                    entry.Title,
                    entry.Note)];
                row.Tag = _all.IndexOf(entry);
            }
        }
        finally
        {
            _rebinding = false;
        }
    }

    // An edit lands on the entry itself, so it survives range changes and is what Export returns.
    private void OnCellValueChanged(object? sender, DataGridViewCellEventArgs e)
    {
        if (_rebinding || e.RowIndex < 0)
            return;

        var row = _grid.Rows[e.RowIndex];
        if (row.Tag is not int index || index < 0 || index >= _all.Count)
            return;

        var entry = _all[index];
        var value = row.Cells[e.ColumnIndex].Value?.ToString() ?? string.Empty;

        switch (e.ColumnIndex)
        {
            case ColHours:
                if (double.TryParse(value, NumberStyles.Float, CultureInfo.CurrentCulture, out var hours) && hours > 0)
                    _all[index] = entry with { Hours = Math.Round(hours, 2) };
                break;
            case ColTicket:
                var ticket = value.Trim().TrimStart('#');
                entry = entry with { Ticket = ticket.Length > 0 ? ticket : null };
                _all[index] = _noteEdited.Contains(entry.Slot) ? entry : entry.WithComposedNote();
                break;
            case ColTitle:
                entry = entry with { Title = value.Trim() };
                _all[index] = _noteEdited.Contains(entry.Slot) ? entry : entry.WithComposedNote();
                break;
            case ColNote:
                _noteEdited.Add(entry.Slot);
                _all[index] = entry with { Note = value.Trim() };
                break;
            default:
                return;
        }

        RefreshRow(row, _all[index]);
        Sync2();
    }

    // Reflect normalization (rounded hours, recomposed note) back into the row without rebinding.
    private void RefreshRow(DataGridViewRow row, ExportEntry entry)
    {
        _rebinding = true;
        try
        {
            row.Cells[ColHours].Value = entry.Hours.ToString("0.00", CultureInfo.InvariantCulture);
            row.Cells[ColTicket].Value = entry.Ticket;
            row.Cells[ColTitle].Value = entry.Title;
            row.Cells[ColNote].Value = entry.Note;
        }
        finally
        {
            _rebinding = false;
        }
    }

    // The summary line alone — a cell edit must not rebuild the grid out from under the editor.
    private void Sync2()
    {
        var entries = Selected;
        _summary.Text = entries.Count == 0
            ? "Nothing starts inside that window."
            : $"{entries.Count} {(entries.Count == 1 ? "entry" : "entries")} · {entries.Sum(e => e.Hours):0.00} h";
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        DarkTitleBar.Apply(Handle);   // dark title bar, matching the live window
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
    /// Shows the export window. Returns the reviewed (possibly edited) entries and the chosen
    /// range, or null if the user cancelled or the selection turned out to be empty.
    /// </summary>
    public static ExportSelection? Ask(IWin32Window? owner, IReadOnlyList<ExportEntry> all)
    {
        using var dialog = new ExportRangeDialog(all);
        if (dialog.ShowDialog(owner) != DialogResult.OK || dialog.Selected.Count == 0)
            return null;

        return new ExportSelection(dialog.Selected, dialog.From, dialog.To);
    }
}
