using System.Drawing;
using System.Globalization;
using Tally.Core;

namespace Tally.App;

/// <summary>The live window's colours, handed to the day picker so it draws in the same dark chrome.</summary>
internal sealed record DayPickerTheme(Color Background, Color Cell, Color Text, Color Muted, Color Accent, Color AccentText)
{
    /// <summary>
    /// The popup's edge. A dark panel over a dark page has nothing to separate the two, so the
    /// calendar needs a line of its own to read as something floating above the day rather than
    /// part of it.
    /// </summary>
    public Color Border { get; init; } = Color.FromArgb(0x44, 0x4b, 0x55);
}

/// <summary>
/// A month at a time, drawn rather than themed. Windows' own <c>MonthCalendar</c> ignores custom
/// colours on a modern desktop and comes up white, which is unreadable dropping out of a dark
/// window — so the grid is a handful of flat buttons over <see cref="MonthGrid"/>. Days outside
/// what was recorded are visible but dead, which answers "how far back does this go" without
/// anyone having to click to find out.
/// </summary>
internal static class DayPicker
{
    private const int CellWidth = 34;
    private const int CellHeight = 28;
    private const int Pad = 8;
    private const int HeaderHeight = 30;
    private const int HeadingsHeight = 22;

    /// <summary>
    /// Drops the picker under <paramref name="anchor"/> and calls <paramref name="picked"/> with the
    /// chosen day. Clicking anywhere else, or Escape, closes it without choosing.
    /// </summary>
    public static void Show(
        Control anchor, DateOnly selected, DateOnly earliest, DateOnly today,
        DayPickerTheme theme, Action<DateOnly> picked)
    {
        var floor = earliest > today ? today : earliest;
        var start = DayNavigation.Clamp(selected, floor, today);

        var width = (CellWidth * MonthGrid.DaysPerWeek) + (Pad * 2);
        var height = HeaderHeight + HeadingsHeight + (CellHeight * MonthGrid.Weeks) + Pad;

        var panel = new Panel { BackColor = theme.Background, Size = new Size(width, height) };
        panel.Paint += (_, e) => ControlPaint.DrawBorder(
            e.Graphics, panel.ClientRectangle, theme.Border, ButtonBorderStyle.Solid);
        var host = new ToolStripControlHost(panel) { AutoSize = false, Margin = Padding.Empty, Padding = Padding.Empty, Size = panel.Size };
        var drop = new ToolStripDropDown { AutoSize = true, Padding = Padding.Empty, BackColor = theme.Background, DropShadowEnabled = true };
        drop.Items.Add(host);
        drop.Closed += (_, _) => drop.Dispose();   // takes the host and the panel with it

        var month = new DateOnly(start.Year, start.Month, 1);
        var title = new Label
        {
            AutoSize = false,
            Size = new Size(width - (CellWidth * 2) - (Pad * 2), HeaderHeight - 4),
            Location = new Point(Pad + CellWidth, Pad - 2),
            ForeColor = theme.Text,
            Font = new Font("Segoe UI Semibold", 10f, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleCenter,
        };
        var prevMonth = HeaderButton("❮", "Previous month", new Point(Pad, Pad - 2), theme);
        var nextMonth = HeaderButton("❯", "Next month", new Point(width - Pad - CellWidth, Pad - 2), theme);
        panel.Controls.Add(title);
        panel.Controls.Add(prevMonth);
        panel.Controls.Add(nextMonth);

        for (var i = 0; i < MonthGrid.DaysPerWeek; i++)
        {
            panel.Controls.Add(new Label
            {
                Text = CultureInfo.InvariantCulture.DateTimeFormat
                    .GetShortestDayName(MonthGrid.Headings()[i]),
                AutoSize = false,
                Size = new Size(CellWidth, HeadingsHeight),
                Location = new Point(Pad + (i * CellWidth), HeaderHeight),
                ForeColor = theme.Muted,
                Font = new Font("Segoe UI", 8.25f),
                TextAlign = ContentAlignment.MiddleCenter,
            });
        }

        // The 42 day cells are created once and relabelled as the month changes, so paging never
        // rebuilds the control tree under the cursor.
        var cells = new Button[MonthGrid.Weeks * MonthGrid.DaysPerWeek];
        for (var i = 0; i < cells.Length; i++)
        {
            var cell = new Button
            {
                FlatStyle = FlatStyle.Flat,
                UseVisualStyleBackColor = false,
                Size = new Size(CellWidth - 2, CellHeight - 2),
                Location = new Point(Pad + ((i % MonthGrid.DaysPerWeek) * CellWidth), HeaderHeight + HeadingsHeight + ((i / MonthGrid.DaysPerWeek) * CellHeight)),
                Font = new Font("Segoe UI", 9f),
                Cursor = Cursors.Hand,
                TabStop = false,
            };
            cell.FlatAppearance.BorderSize = 0;
            cell.Click += (sender, _) =>
            {
                if (sender is Button { Tag: DateOnly day })
                {
                    drop.Close();
                    picked(day);
                }
            };
            cells[i] = cell;
            panel.Controls.Add(cell);
        }

        void Render()
        {
            title.Text = month.ToString("MMMM yyyy", CultureInfo.InvariantCulture);
            prevMonth.Enabled = month > new DateOnly(floor.Year, floor.Month, 1);
            nextMonth.Enabled = month < new DateOnly(today.Year, today.Month, 1);

            var grid = MonthGrid.Build(month.Year, month.Month);
            for (var i = 0; i < cells.Length; i++)
            {
                var day = grid[i];
                var cell = cells[i];
                var inMonth = day.Month == month.Month && day.Year == month.Year;
                var inRange = day >= floor && day <= today;

                // A neighbouring month's days are drawn blank rather than as numbers: this picker
                // moves one month at a time, so a number you cannot click is only a false target.
                cell.Text = inMonth ? day.Day.ToString(CultureInfo.InvariantCulture) : string.Empty;
                cell.Tag = day;
                cell.Enabled = inMonth && inRange;
                cell.Visible = inMonth;

                var isSelected = day == start;
                var isToday = day == today;
                cell.BackColor = isSelected ? theme.Accent : theme.Background;
                cell.ForeColor = isSelected ? theme.AccentText
                    : !inRange ? theme.Muted
                    : isToday ? theme.Accent
                    : theme.Text;
                cell.Font = new Font("Segoe UI", 9f, isToday || isSelected ? FontStyle.Bold : FontStyle.Regular);
                cell.FlatAppearance.MouseOverBackColor = isSelected ? theme.Accent : theme.Cell;
            }
        }

        prevMonth.Click += (_, _) => { month = month.AddMonths(-1); Render(); };
        nextMonth.Click += (_, _) => { month = month.AddMonths(1); Render(); };

        Render();
        drop.Show(anchor, new Point(0, anchor.Height + 2));
    }

    private static Button HeaderButton(string glyph, string description, Point location, DayPickerTheme theme)
    {
        var b = new Button
        {
            Text = glyph,
            FlatStyle = FlatStyle.Flat,
            UseVisualStyleBackColor = false,
            Size = new Size(CellWidth - 4, HeaderHeight - 6),
            Location = location,
            BackColor = theme.Background,
            ForeColor = theme.Text,
            Font = new Font("Segoe UI", 9f),
            Cursor = Cursors.Hand,
            AccessibleName = description,
            TabStop = false,
        };
        b.FlatAppearance.BorderSize = 0;
        b.FlatAppearance.MouseOverBackColor = theme.Cell;
        return b;
    }
}
