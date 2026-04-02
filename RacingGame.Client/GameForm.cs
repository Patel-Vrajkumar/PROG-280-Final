using RacingGame.Shared;

namespace RacingGame.Client;

/// <summary>
/// The main race screen.  Shows the track, all cars, and a "MOVE!" button.
/// Driven by messages from the server via <see cref="NetworkClient"/>.
/// </summary>
public sealed class GameForm : Form
{
    // ── Layout constants ──────────────────────────────────────────────────────
    private const int TrackPanelHeight = 420;
    private const int LaneHeight       = 60;
    private const int CarWidth         = 80;
    private const int CarHeight        = 36;
    private const int TrackLeft        = 10;     // pixels: start line x
    private const int TrackRight       = 860;    // pixels: finish line x (right edge)
    private const int TrackUsable      = TrackRight - TrackLeft - CarWidth;

    // ── State ─────────────────────────────────────────────────────────────────
    private readonly NetworkClient   _net;
    private readonly string          _myName;
    private readonly int             _myCarChoice;

    // Ordered list of players (order = lane order)
    private readonly List<PlayerInfo>              _players   = [];
    private readonly Dictionary<string, int>       _positions = [];  // name → 0..100
    private GamePhase _phase = GamePhase.Waiting;
    private string    _winnerText = string.Empty;

    // ── Controls ──────────────────────────────────────────────────────────────
    private readonly Panel  _trackPanel  = new();
    private readonly Button _btnMove     = new();
    private readonly Label  _lblStatus   = new();
    private readonly Label  _lblWaiting  = new();

    public GameForm(NetworkClient net, string playerName, int carChoice)
    {
        _net         = net;
        _myName      = playerName;
        _myCarChoice = carChoice;

        Text            = $"Racing Game  –  {playerName}";
        Size            = new Size(920, 560);
        StartPosition   = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox     = false;
        BackColor       = Color.FromArgb(15, 15, 25);
        ForeColor       = Color.White;

        BuildUI();

        net.MessageReceived += OnMessageReceived;
        net.Disconnected    += OnDisconnected;

        FormClosing += (_, _) =>
        {
            net.MessageReceived -= OnMessageReceived;
            net.Disconnected    -= OnDisconnected;
            net.Dispose();
        };
    }

    // ── UI construction ───────────────────────────────────────────────────────

    private void BuildUI()
    {
        // Track panel (owner-drawn)
        _trackPanel.Location  = new Point(10, 50);
        _trackPanel.Size      = new Size(880, TrackPanelHeight);
        _trackPanel.BackColor = Color.FromArgb(30, 30, 45);
        _trackPanel.BorderStyle = BorderStyle.FixedSingle;
        _trackPanel.Paint    += DrawTrack;
        Controls.Add(_trackPanel);

        // Status bar (top)
        _lblStatus.Location  = new Point(10, 14);
        _lblStatus.Size      = new Size(880, 28);
        _lblStatus.Font      = new Font("Segoe UI", 11, FontStyle.Bold);
        _lblStatus.ForeColor = Color.Gold;
        _lblStatus.TextAlign = ContentAlignment.MiddleCenter;
        _lblStatus.Text      = "Waiting for players …";
        Controls.Add(_lblStatus);

        // Waiting overlay on the track
        _lblWaiting.Location  = new Point(200, 170);
        _lblWaiting.Size      = new Size(480, 80);
        _lblWaiting.Font      = new Font("Segoe UI", 18, FontStyle.Bold);
        _lblWaiting.ForeColor = Color.Cyan;
        _lblWaiting.BackColor = Color.Transparent;
        _lblWaiting.TextAlign = ContentAlignment.MiddleCenter;
        _lblWaiting.Text      = "Waiting for players …\nAt least 2 needed to start.";
        _trackPanel.Controls.Add(_lblWaiting);

        // Move button (bottom)
        _btnMove.Text      = "🚀  MOVE!";
        _btnMove.Location  = new Point(350, 484);
        _btnMove.Size      = new Size(200, 50);
        _btnMove.Font      = new Font("Segoe UI", 14, FontStyle.Bold);
        _btnMove.BackColor = Color.DodgerBlue;
        _btnMove.ForeColor = Color.White;
        _btnMove.FlatStyle = FlatStyle.Flat;
        _btnMove.FlatAppearance.BorderSize = 0;
        _btnMove.Cursor    = Cursors.Hand;
        _btnMove.Enabled   = false;
        _btnMove.Click    += OnMoveClicked;
        Controls.Add(_btnMove);
    }

    // ── Track painting ────────────────────────────────────────────────────────

    private static readonly Color[] LaneColors = [
        Color.FromArgb(45, 45, 65),
        Color.FromArgb(38, 38, 58)
    ];

    private static readonly Color[] CarColors = [
        Color.DeepSkyBlue,
        Color.OrangeRed,
        Color.LimeGreen,
        Color.Gold,
        Color.Violet
    ];

    private void DrawTrack(object? sender, PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

        int playerCount = Math.Max(_players.Count, 1);
        int laneH       = Math.Min(LaneHeight, (TrackPanelHeight - 20) / playerCount);

        // Draw lanes
        for (int i = 0; i < _players.Count; i++)
        {
            int laneY = 10 + i * laneH;
            g.FillRectangle(new SolidBrush(LaneColors[i % 2]),
                            TrackLeft, laneY, TrackRight - TrackLeft, laneH - 2);

            // Lane divider
            using var divPen = new Pen(Color.FromArgb(60, 60, 80), 1);
            g.DrawLine(divPen, TrackLeft, laneY + laneH - 2,
                               TrackRight, laneY + laneH - 2);
        }

        // Start line
        using var startPen = new Pen(Color.White, 2);
        g.DrawLine(startPen, TrackLeft + CarWidth + 2, 8,
                             TrackLeft + CarWidth + 2, 10 + _players.Count * laneH - 10);

        // Finish line (chequered pattern)
        DrawFinishLine(g, playerCount, laneH);

        // Cars
        for (int i = 0; i < _players.Count; i++)
        {
            var p    = _players[i];
            int pos  = _positions.TryGetValue(p.Name, out int v) ? v : 0;
            int carX = TrackLeft + CarWidth + 4 + (int)((pos / 100.0) * TrackUsable);
            int laneY = 10 + i * laneH;
            int carY  = laneY + (laneH - CarHeight) / 2;

            Color carColor = CarColors[i % CarColors.Length];
            DrawCar(g, carX, carY, carColor, p.Name == _myName);

            // Player name label
            var nameFont = new Font("Segoe UI", 7.5f, FontStyle.Bold);
            g.DrawString(p.Name, nameFont, Brushes.White, carX + 2, carY - 14);
        }

        // Winner overlay
        if (!string.IsNullOrEmpty(_winnerText))
        {
            using var overlayBrush = new SolidBrush(Color.FromArgb(200, 0, 0, 0));
            g.FillRectangle(overlayBrush, 0, 0, _trackPanel.Width, _trackPanel.Height);

            using var bigFont = new Font("Segoe UI", 26, FontStyle.Bold);
            g.DrawString(_winnerText, bigFont, Brushes.Gold,
                         new RectangleF(0, 120, _trackPanel.Width, 120),
                         new StringFormat { Alignment = StringAlignment.Center });

            using var subFont = new Font("Segoe UI", 14);
            g.DrawString("Close this window to return to the lobby.",
                         subFont, Brushes.LightGray,
                         new RectangleF(0, 220, _trackPanel.Width, 60),
                         new StringFormat { Alignment = StringAlignment.Center });
        }
    }

    private void DrawFinishLine(Graphics g, int playerCount, int laneH)
    {
        int checkSize = 10;
        int totalH    = playerCount * laneH;
        int rows      = totalH / checkSize;
        bool white    = true;
        for (int row = 0; row < rows; row++, white = !white)
        {
            bool cell = white;
            for (int y2 = row * checkSize; y2 < (row + 1) * checkSize; y2 += checkSize)
            {
                g.FillRectangle(cell ? Brushes.White : Brushes.Black,
                                TrackRight - 10, 10 + row * checkSize, 10, checkSize);
                cell = !cell;
            }
        }
    }

    private static void DrawCar(Graphics g, int x, int y, Color color, bool isMe)
    {
        // Body
        using var bodyBrush = new SolidBrush(color);
        g.FillRoundedRectangle(bodyBrush, x, y + 8, CarWidth, CarHeight - 16, 6);

        // Roof
        using var roofBrush = new SolidBrush(isMe ? Color.White : Color.LightGray);
        g.FillRoundedRectangle(roofBrush, x + 14, y + 4, CarWidth - 28, CarHeight - 10, 5);

        // Wheels
        g.FillEllipse(Brushes.DarkGray, x + 6,  y + CarHeight - 14, 16, 16);
        g.FillEllipse(Brushes.DarkGray, x + CarWidth - 22, y + CarHeight - 14, 16, 16);
        g.FillEllipse(Brushes.Gray, x + 8,  y + CarHeight - 12, 12, 12);
        g.FillEllipse(Brushes.Gray, x + CarWidth - 20, y + CarHeight - 12, 12, 12);

        // Highlight border if this is the local player
        if (isMe)
        {
            using var pen = new Pen(Color.White, 2);
            g.DrawRoundedRectangle(pen, x, y + 8, CarWidth, CarHeight - 16, 6);
        }
    }

    // ── Message handling ──────────────────────────────────────────────────────

    private void OnMessageReceived(GameMessage msg)
    {
        if (InvokeRequired) { Invoke(() => OnMessageReceived(msg)); return; }

        switch (msg.Type)
        {
            case MessageType.WaitingRoom:
                if (msg.Players is not null)
                {
                    _players.Clear();
                    _players.AddRange(msg.Players);
                    foreach (var p in _players)
                        _positions[p.Name] = 0;
                }
                _lblStatus.Text = $"Waiting room  –  {_players.Count} / 5 players";
                _lblWaiting.Visible = true;
                _trackPanel.Invalidate();
                break;

            case MessageType.PlayerJoined:
                if (!_players.Any(p => p.Name == msg.PlayerName))
                {
                    _players.Add(new PlayerInfo { Name = msg.PlayerName, CarChoice = msg.CarChoice });
                    _positions[msg.PlayerName] = 0;
                }
                _lblStatus.Text = $"Waiting room  –  {_players.Count} / 5 players";
                _trackPanel.Invalidate();
                break;

            case MessageType.PlayerLeft:
                _players.RemoveAll(p => p.Name == msg.PlayerName);
                _positions.Remove(msg.PlayerName);
                _lblStatus.Text = $"Waiting room  –  {_players.Count} / 5 players";
                _trackPanel.Invalidate();
                break;

            case MessageType.GameStart:
                _phase = GamePhase.InProgress;
                _winnerText = string.Empty;
                if (msg.Players is not null)
                {
                    _players.Clear();
                    _players.AddRange(msg.Players);
                    foreach (var p in _players)
                        _positions[p.Name] = 0;
                }
                _lblWaiting.Visible = false;
                _lblStatus.Text     = "🚦  Race started! Click MOVE to advance your car!";
                _btnMove.Enabled    = true;
                _trackPanel.Invalidate();
                break;

            case MessageType.PositionUpdate:
                if (msg.Positions is not null)
                    foreach (var kv in msg.Positions)
                        _positions[kv.Key] = kv.Value;
                _trackPanel.Invalidate();
                break;

            case MessageType.GameOver:
                _phase = GamePhase.Finished;
                if (msg.Positions is not null)
                    foreach (var kv in msg.Positions)
                        _positions[kv.Key] = kv.Value;

                _winnerText = msg.WinnerName == _myName
                    ? $"🏆  YOU WIN, {_myName}!  🏆"
                    : $"🏆  {msg.WinnerName} WINS!  🏆";

                if (!string.IsNullOrEmpty(msg.Message))
                    _winnerText += $"\n{msg.Message}";

                _lblStatus.Text  = _winnerText;
                _btnMove.Enabled = false;
                _trackPanel.Invalidate();
                break;

            case MessageType.Error:
                MessageBox.Show(msg.Message ?? "Unknown error", "Server Error",
                                MessageBoxButtons.OK, MessageBoxIcon.Warning);
                break;
        }
    }

    private void OnDisconnected()
    {
        if (InvokeRequired) { Invoke(OnDisconnected); return; }
        _lblStatus.Text  = "Disconnected from server.";
        _btnMove.Enabled = false;
    }

    // ── Move button ───────────────────────────────────────────────────────────

    private async void OnMoveClicked(object? sender, EventArgs e)
    {
        if (_phase != GamePhase.InProgress) return;
        try
        {
            await _net.SendAsync(new GameMessage { Type = MessageType.Move });
        }
        catch
        {
            _lblStatus.Text  = "Connection lost.";
            _btnMove.Enabled = false;
        }
    }

    // ── Local GamePhase shadow ─────────────────────────────────────────────────
    private enum GamePhase { Waiting, InProgress, Finished }
}

// ── Graphics extension helpers (rounded rectangles) ─────────────────────────
internal static class GraphicsExtensions
{
    public static void FillRoundedRectangle(this Graphics g, Brush brush,
        float x, float y, float w, float h, float r)
    {
        using var path = RoundedRectPath(x, y, w, h, r);
        g.FillPath(brush, path);
    }

    public static void DrawRoundedRectangle(this Graphics g, Pen pen,
        float x, float y, float w, float h, float r)
    {
        using var path = RoundedRectPath(x, y, w, h, r);
        g.DrawPath(pen, path);
    }

    private static System.Drawing.Drawing2D.GraphicsPath RoundedRectPath(
        float x, float y, float w, float h, float r)
    {
        var path = new System.Drawing.Drawing2D.GraphicsPath();
        path.AddArc(x,         y,         r * 2, r * 2,  180, 90);
        path.AddArc(x + w - r * 2, y,     r * 2, r * 2, 270, 90);
        path.AddArc(x + w - r * 2, y + h - r * 2, r * 2, r * 2, 0, 90);
        path.AddArc(x,         y + h - r * 2, r * 2, r * 2, 90, 90);
        path.CloseFigure();
        return path;
    }
}
