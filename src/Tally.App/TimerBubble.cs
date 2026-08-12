using System.Drawing;
using System.Runtime.InteropServices;
using Tally.Core;

namespace Tally.App;

/// <summary>
/// A small always-on-top, borderless, draggable window that shows the running manual timer when
/// the main app window isn't visible. Bottom-right by default; remembers where it's dragged.
/// </summary>
public sealed class TimerBubble : Form
{
    private static readonly Color BubbleBg = Color.FromArgb(0x1e, 0x21, 0x26);
    private static readonly Color Accent = Color.FromArgb(0x2f, 0xd4, 0xb6);
    private static readonly Color NameFg = Color.FromArgb(0xc7, 0xcd, 0xd3);
    private static readonly Color StopFg = Color.FromArgb(0xff, 0x8a, 0x8a);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateRoundRectRgn(int left, int top, int right, int bottom, int w, int h);

    private readonly ManualTimerService _timer;
    private readonly System.Windows.Forms.Timer _tick = new() { Interval = 1000 };
    private readonly Label _name;
    private readonly Label _elapsed;
    private Point _dragCursorStart;
    private Point _dragFormStart;
    private bool _dragging;
    private bool _positioned;

    /// <summary>Raised when the user double-clicks the bubble to reopen the app window.</summary>
    public event Action? RestoreRequested;

    /// <summary>Raised when the user clicks the bubble's stop button.</summary>
    public event Action? StopRequested;

    public TimerBubble(ManualTimerService timer)
    {
        _timer = timer;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        BackColor = BubbleBg;
        Size = new Size(250, 60);
        StartPosition = FormStartPosition.Manual;

        var stripe = new Panel { BackColor = Accent, Dock = DockStyle.Left, Width = 4 };
        _name = new Label
        {
            AutoSize = false,
            AutoEllipsis = true,
            ForeColor = NameFg,
            Font = new Font("Segoe UI", 8.5f),
            Location = new Point(16, 9),
            Size = new Size(180, 16),
        };
        _elapsed = new Label
        {
            AutoSize = false,
            ForeColor = Color.White,
            Font = new Font("Segoe UI Semibold", 15f, FontStyle.Bold),
            Location = new Point(15, 25),
            Size = new Size(180, 26),
        };
        var stop = new Button
        {
            Text = "■",
            FlatStyle = FlatStyle.Flat,
            ForeColor = StopFg,
            BackColor = BubbleBg,
            Size = new Size(30, 30),
            Location = new Point(208, 15),
            Cursor = Cursors.Hand,
            TabStop = false,
        };
        stop.FlatAppearance.BorderSize = 0;
        stop.Click += (_, _) => StopRequested?.Invoke();

        Controls.Add(_name);
        Controls.Add(_elapsed);
        Controls.Add(stop);
        Controls.Add(stripe);

        // Drag by the background or either label; double-click reopens the app.
        MakeDraggable(this);
        MakeDraggable(_name);
        MakeDraggable(_elapsed);
        DoubleClick += (_, _) => RestoreRequested?.Invoke();
        _name.DoubleClick += (_, _) => RestoreRequested?.Invoke();
        _elapsed.DoubleClick += (_, _) => RestoreRequested?.Invoke();

        _tick.Tick += (_, _) => UpdateDisplay();
    }

    // Don't steal focus from whatever the user is doing when the bubble appears.
    protected override bool ShowWithoutActivation => true;

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Region = Region.FromHrgn(CreateRoundRectRgn(0, 0, Width + 1, Height + 1, 14, 14));
    }

    public void ShowBubble()
    {
        if (!_positioned)
        {
            PositionBottomRight();
            _positioned = true;
        }

        UpdateDisplay();
        _tick.Start();
        Show();
    }

    public void HideBubble()
    {
        _tick.Stop();
        Hide();
    }

    private void UpdateDisplay()
    {
        if (_timer.Active is { } active)
        {
            _name.Text = active.Name;
            _elapsed.Text = TimerText.Elapsed(_timer.Elapsed);
        }
    }

    private void PositionBottomRight()
    {
        var area = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 720);
        Location = new Point(area.Right - Width - 16, area.Bottom - Height - 16);
    }

    private void MakeDraggable(Control control)
    {
        control.MouseDown += (_, e) =>
        {
            if (e.Button != MouseButtons.Left)
                return;
            _dragging = true;
            _dragCursorStart = Cursor.Position;
            _dragFormStart = Location;
        };
        control.MouseMove += (_, e) =>
        {
            if (!_dragging || e.Button != MouseButtons.Left)
                return;
            var delta = new Size(Cursor.Position.X - _dragCursorStart.X, Cursor.Position.Y - _dragCursorStart.Y);
            Location = _dragFormStart + delta;
        };
        control.MouseUp += (_, _) => _dragging = false;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _tick.Dispose();
        base.Dispose(disposing);
    }
}
