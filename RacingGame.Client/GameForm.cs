using RacingGame.Shared;

namespace RacingGame.Client;

/// <summary>
/// The main race screen.  Shows the track, all cars, a Ready button (waiting phase),
/// a Move button (race phase), a countdown overlay (3-2-1-Go!), and a
/// Restart / Quit panel when the race ends.
/// Driven entirely by messages from the server via <see cref="NetworkClient"/>.
/// </summary>
public sealed class GameForm : Form
{
    // ── Layout constants ──────────────────────────────────────────────────────
    private const int TrackPanelHeight = 420;   // height of the race track panel
    private const int LaneHeight       = 60;    // default height of one lane
    private const int CarWidth         = 80;    // width of a car sprite
    private const int CarHeight        = 36;    // height of a car sprite
    private const int TrackLeft        = 10;    // x-pixel of the start line
    private const int TrackRight       = 860;   // x-pixel of the finish line
    private const int TrackUsable      = TrackRight - TrackLeft - CarWidth; // moveable distance

    // ── State ─────────────────────────────────────────────────────────────────
    private readonly NetworkClient         _net;           // TCP connection to server
    private readonly string                _myName;        // this player's name
    private readonly int                   _myCarChoice;   // this player's car number (1-3)

    private readonly List<PlayerInfo>        _players   = [];  // ordered list of players
    private readonly Dictionary<string, int> _positions = [];  // name -> track position 0-100
    private GamePhase _phase      = GamePhase.Waiting;         // current game phase
    private string    _winnerText = string.Empty;              // text shown when race ends
    private string    _countdown  = string.Empty;              // current countdown tick ("3","2","1","Go!")
    private bool      _iAmReady   = false;                     // true after local player clicks Ready
    private string    _readyStatus = string.Empty;             // e.g. "1/3 ready"

    // ── Car images (loaded from images/ folder if the files exist) ─────────────
    private readonly Image?[] _carImages = new Image?[4]; // index 1-3 used (0 unused)

    // ── Controls ──────────────────────────────────────────────────────────────
    private readonly Panel  _trackPanel  = new();   // owner-drawn race track
    private readonly Button _btnMove     = new();   // "MOVE!" – active during race
    private readonly Button _btnReady    = new();   // "I'm Ready!" – active in waiting room
    private readonly Button _btnRestart  = new();   // "Play Again" – shown after game over
    private readonly Button _btnQuit     = new();   // "Quit" – shown after game over
    private readonly Label  _lblStatus   = new();   // top status bar
    private readonly Label  _lblWaiting  = new();   // overlay label shown while waiting

    public GameForm(NetworkClient net, string playerName, int carChoice)
    {
        _net         = net;
        _myName      = playerName;
        _myCarChoice = carChoice;

        Text            = $"Racing Game  -  {playerName}";
        Size            = new Size(920, 580);
        StartPosition   = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox     = false;
        BackColor       = Color.FromArgb(15, 15, 25);
        ForeColor       = Color.White;

        // Try to load car images from the images/ folder next to the executable
        LoadCarImages();

        // Build all the UI controls
        BuildUI();

        // Subscribe to messages coming from the server
        net.MessageReceived += OnMessageReceived;
        net.Disconnected    += OnDisconnected;

        // Unsubscribe and disconnect cleanly when the window closes
        FormClosing += (_, _) =>
        {
            net.MessageReceived -= OnMessageReceived;
            net.Disconnected    -= OnDisconnected;
            net.Dispose();
        };
    }

    // ── Image loading ─────────────────────────────────────────────────────────

    /// <summary>
    /// Looks for Car_1.png, Car_2.png, Car_3.png in the "images" folder next to
    /// the executable.  If a file is missing the code falls back to drawing the
    /// car with rectangles (original behaviour).
    /// </summary>
    private void LoadCarImages()
    {
        string baseDir = AppDomain.CurrentDomain.BaseDirectory;
        string imgDir  = Path.Combine(baseDir, "images");

        for (int i = 1; i <= 3; i++)
        {
            string path = Path.Combine(imgDir, $"Car_{i}.png");
            if (File.Exists(path))
            {
                try { _carImages[i] = Image.FromFile(path); }
                catch { /* ignore – will fall back to drawn car */ }
            }
        }
    }

    // ── UI construction ───────────────────────────────────────────────────────

    private void BuildUI()
    {
        // ── Status bar (top) ──────────────────────────────────────────────────
        _lblStatus.Location  = new Point(10, 14);
        _lblStatus.Size      = new Size(880, 28);
        _lblStatus.Font      = new Font("Segoe UI", 11, FontStyle.Bold);
        _lblStatus.ForeColor = Color.Gold;
        _lblStatus.TextAlign = ContentAlignment.MiddleCenter;
        _lblStatus.Text      = "Waiting for players …";
        Controls.Add(_lblStatus);

        // ── Race track panel (center, owner-drawn) ────────────────────────────
        _trackPanel.Location    = new Point(10, 50);
        _trackPanel.Size        = new Size(880, TrackPanelHeight);
        _trackPanel.BackColor   = Color.FromArgb(30, 30, 45);
        _trackPanel.BorderStyle = BorderStyle.FixedSingle;
        _trackPanel.Paint      += DrawTrack;   // all drawing happens in DrawTrack()
        Controls.Add(_trackPanel);

        // ── Waiting overlay (shown on the track before race starts) ───────────
        _lblWaiting.Location  = new Point(200, 150);
        _lblWaiting.Size      = new Size(480, 100);
        _lblWaiting.Font      = new Font("Segoe UI", 16, FontStyle.Bold);
        _lblWaiting.ForeColor = Color.Cyan;
        _lblWaiting.BackColor = Color.Transparent;
        _lblWaiting.TextAlign = ContentAlignment.MiddleCenter;
        _lblWaiting.Text      = "Waiting for players …\nAt least 2 needed.  Click Ready when set!";
        _trackPanel.Controls.Add(_lblWaiting);

        // ── Ready button (shown while waiting, hidden once race starts) ────────
        _btnReady.Text      = "I'm Ready!";
        _btnReady.Location  = new Point(280, 484);
        _btnReady.Size      = new Size(170, 50);
        _btnReady.Font      = new Font("Segoe UI", 13, FontStyle.Bold);
        _btnReady.BackColor = Color.ForestGreen;
        _btnReady.ForeColor = Color.White;
        _btnReady.FlatStyle = FlatStyle.Flat;
        _btnReady.FlatAppearance.BorderSize = 0;
        _btnReady.Cursor    = Cursors.Hand;
        _btnReady.Click    += OnReadyClicked;
        Controls.Add(_btnReady);

        // ── Move button (disabled until race starts) ──────────────────────────
        _btnMove.Text      = "MOVE!";
        _btnMove.Location  = new Point(460, 484);
        _btnMove.Size      = new Size(180, 50);
        _btnMove.Font      = new Font("Segoe UI", 14, FontStyle.Bold);
        _btnMove.BackColor = Color.DodgerBlue;
        _btnMove.ForeColor = Color.White;
        _btnMove.FlatStyle = FlatStyle.Flat;
        _btnMove.FlatAppearance.BorderSize = 0;
        _btnMove.Cursor    = Cursors.Hand;
        _btnMove.Enabled   = false;
        _btnMove.Click    += OnMoveClicked;
        Controls.Add(_btnMove);

        // ── Restart button (hidden until race ends) ───────────────────────────
        _btnRestart.Text      = "Play Again";
        _btnRestart.Location  = new Point(280, 484);
        _btnRestart.Size      = new Size(170, 50);
        _btnRestart.Font      = new Font("Segoe UI", 13, FontStyle.Bold);
        _btnRestart.BackColor = Color.DodgerBlue;
        _btnRestart.ForeColor = Color.White;
        _btnRestart.FlatStyle = FlatStyle.Flat;
        _btnRestart.FlatAppearance.BorderSize = 0;
        _btnRestart.Cursor    = Cursors.Hand;
        _btnRestart.Visible   = false;
        _btnRestart.Click    += OnRestartClicked;
        Controls.Add(_btnRestart);

        // ── Quit button (hidden until race ends) ──────────────────────────────
        _btnQuit.Text      = "Quit";
        _btnQuit.Location  = new Point(460, 484);
        _btnQuit.Size      = new Size(180, 50);
        _btnQuit.Font      = new Font("Segoe UI", 13, FontStyle.Bold);
        _btnQuit.BackColor = Color.Firebrick;
        _btnQuit.ForeColor = Color.White;
        _btnQuit.FlatStyle = FlatStyle.Flat;
        _btnQuit.FlatAppearance.BorderSize = 0;
        _btnQuit.Cursor    = Cursors.Hand;
        _btnQuit.Visible   = false;
        _btnQuit.Click    += (_, _) => Close();   // close the form = return to lobby
        Controls.Add(_btnQuit);
    }

    // ── Track painting ────────────────────────────────────────────────────────

    // Two alternating lane background colours
    private static readonly Color[] LaneColors =
    [
        Color.FromArgb(45, 45, 65),
        Color.FromArgb(38, 38, 58)
    ];

    // One colour per car slot (matches the car images and car buttons)
    private static readonly Color[] CarColors =
    [
        Color.DeepSkyBlue,   // Car 1
        Color.OrangeRed,     // Car 2
        Color.LimeGreen,     // Car 3
        Color.Gold,          // Car 4 (extra players)
        Color.Violet         // Car 5
    ];

    /// <summary>
    /// Paints the entire track: lanes, start/finish lines, cars, overlays.
    /// Called by Windows Forms whenever the track panel needs to be redrawn.
    /// </summary>
    private void DrawTrack(object? sender, PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

        int playerCount = Math.Max(_players.Count, 1);
        // Calculate lane height – shrink lanes if there are many players
        int laneH = Math.Min(LaneHeight, (TrackPanelHeight - 20) / playerCount);

        // ── Draw lane backgrounds ─────────────────────────────────────────────
        for (int i = 0; i < _players.Count; i++)
        {
            int laneY = 10 + i * laneH;
            g.FillRectangle(new SolidBrush(LaneColors[i % 2]),
                            TrackLeft, laneY, TrackRight - TrackLeft, laneH - 2);

            // Draw a thin line between lanes
            using var divPen = new Pen(Color.FromArgb(60, 60, 80), 1);
            g.DrawLine(divPen, TrackLeft, laneY + laneH - 2, TrackRight, laneY + laneH - 2);
        }

        // ── Start line (white vertical bar) ───────────────────────────────────
        using var startPen = new Pen(Color.White, 2);
        g.DrawLine(startPen, TrackLeft + CarWidth + 2, 8,
                             TrackLeft + CarWidth + 2, 10 + _players.Count * laneH - 10);

        // ── Finish line (chequered flag pattern) ──────────────────────────────
        DrawFinishLine(g, playerCount, laneH);

        // ── Draw each car on the track ────────────────────────────────────────
        for (int i = 0; i < _players.Count; i++)
        {
            var p     = _players[i];
            int pos   = _positions.TryGetValue(p.Name, out int v) ? v : 0;
            // Convert the 0-100 position to a pixel x-coordinate
            int carX  = TrackLeft + CarWidth + 4 + (int)((pos / 100.0) * TrackUsable);
            int laneY = 10 + i * laneH;
            int carY  = laneY + (laneH - CarHeight) / 2;

            Color carColor = CarColors[i % CarColors.Length];
            bool isMe = (p.Name == _myName);  // highlight the local player's car

            // Use a car image if one was loaded, otherwise draw with code
            int imgIndex = Math.Clamp(p.CarChoice, 1, 3);
            if (_carImages[imgIndex] is Image img)
                g.DrawImage(img, carX, carY, CarWidth, CarHeight);
            else
                DrawCar(g, carX, carY, carColor, isMe);

            // Draw a white border around the local player's car so it stands out
            if (isMe)
            {
                using var pen = new Pen(Color.White, 2);
                g.DrawRoundedRectangle(pen, carX, carY + 8, CarWidth, CarHeight - 16, 6);
            }

            // Draw player name above the car
            var nameFont = new Font("Segoe UI", 7.5f, FontStyle.Bold);
            g.DrawString(p.Name, nameFont, Brushes.White, carX + 2, carY - 14);
        }

        // ── Countdown overlay (shown during 3-2-1-Go!) ────────────────────────
        if (!string.IsNullOrEmpty(_countdown))
        {
            // Semi-transparent dark background
            using var overlayBrush = new SolidBrush(Color.FromArgb(180, 0, 0, 0));
            g.FillRectangle(overlayBrush, 0, 0, _trackPanel.Width, _trackPanel.Height);

            // Large countdown number / "Go!" text
            Color tickColor = _countdown == "Go!" ? Color.LimeGreen : Color.White;
            using var bigFont = new Font("Segoe UI", 80, FontStyle.Bold);
            g.DrawString(_countdown, bigFont, new SolidBrush(tickColor),
                         new RectangleF(0, 80, _trackPanel.Width, 260),
                         new StringFormat { Alignment = StringAlignment.Center });
        }

        // ── Winner overlay (shown after the race ends) ────────────────────────
        if (!string.IsNullOrEmpty(_winnerText))
        {
            // Semi-transparent dark background
            using var overlayBrush = new SolidBrush(Color.FromArgb(200, 0, 0, 0));
            g.FillRectangle(overlayBrush, 0, 0, _trackPanel.Width, _trackPanel.Height);

            // Large winner announcement text
            using var bigFont = new Font("Segoe UI", 26, FontStyle.Bold);
            g.DrawString(_winnerText, bigFont, Brushes.Gold,
                         new RectangleF(0, 120, _trackPanel.Width, 120),
                         new StringFormat { Alignment = StringAlignment.Center });

            // Instruction text below the announcement
            using var subFont = new Font("Segoe UI", 14);
            g.DrawString("Click \"Play Again\" to restart or \"Quit\" to leave.",
                         subFont, Brushes.LightGray,
                         new RectangleF(0, 240, _trackPanel.Width, 60),
                         new StringFormat { Alignment = StringAlignment.Center });
        }
    }

    /// <summary>Draws the chequered finish-line pattern on the right edge of the track.</summary>
    private void DrawFinishLine(Graphics g, int playerCount, int laneH)
    {
        int checkSize = 10;
        int totalH    = playerCount * laneH;
        int rows      = totalH / checkSize;
        bool white    = true;
        for (int row = 0; row < rows; row++, white = !white)
        {
            g.FillRectangle(white ? Brushes.White : Brushes.Black,
                            TrackRight - 10, 10 + row * checkSize, 10, checkSize);
        }
    }

    /// <summary>
    /// Draws a car using rectangles and ellipses (used when no image file is found).
    /// </summary>
    private static void DrawCar(Graphics g, int x, int y, Color color, bool isMe)
    {
        // Main car body (coloured rectangle)
        using var bodyBrush = new SolidBrush(color);
        g.FillRoundedRectangle(bodyBrush, x, y + 8, CarWidth, CarHeight - 16, 6);

        // Roof (lighter rectangle on top of body)
        using var roofBrush = new SolidBrush(isMe ? Color.White : Color.LightGray);
        g.FillRoundedRectangle(roofBrush, x + 14, y + 4, CarWidth - 28, CarHeight - 10, 5);

        // Wheels (dark circles at the bottom corners)
        g.FillEllipse(Brushes.DarkGray, x + 6,              y + CarHeight - 14, 16, 16);
        g.FillEllipse(Brushes.DarkGray, x + CarWidth - 22,  y + CarHeight - 14, 16, 16);
        g.FillEllipse(Brushes.Gray,     x + 8,              y + CarHeight - 12, 12, 12);
        g.FillEllipse(Brushes.Gray,     x + CarWidth - 20,  y + CarHeight - 12, 12, 12);
    }

    // ── Message handling ──────────────────────────────────────────────────────

    /// <summary>
    /// Receives a <see cref="GameMessage"/> from the network and updates the UI.
    /// Always called on the UI thread (marshalled via Invoke if needed).
    /// </summary>
    private void OnMessageReceived(GameMessage msg)
    {
        // If called from a background thread, re-invoke on the UI thread
        if (InvokeRequired) { Invoke(() => OnMessageReceived(msg)); return; }

        switch (msg.Type)
        {
            // ── Waiting room snapshot ─────────────────────────────────────────
            case MessageType.WaitingRoom:
                if (msg.Players is not null)
                {
                    _players.Clear();
                    _players.AddRange(msg.Players);
                    foreach (var p in _players)
                        _positions[p.Name] = 0;
                }
                // msg.Message contains "readyCount/totalCount"
                _readyStatus = msg.Message ?? string.Empty;
                int readyCnt  = 0;
                int totalCnt  = _players.Count;
                if (!string.IsNullOrEmpty(_readyStatus))
                {
                    var parts = _readyStatus.Split('/');
                    if (parts.Length == 2)
                    {
                        int.TryParse(parts[0], out readyCnt);
                        int.TryParse(parts[1], out totalCnt);
                    }
                }
                _lblStatus.Text = $"Lobby: {totalCnt} / 5 players  |  {readyCnt} ready";
                UpdateWaitingLabel();
                _trackPanel.Invalidate();   // request a repaint
                break;

            // ── A new player joined ───────────────────────────────────────────
            case MessageType.PlayerJoined:
                if (!_players.Any(p => p.Name == msg.PlayerName))
                {
                    _players.Add(new PlayerInfo { Name = msg.PlayerName, CarChoice = msg.CarChoice });
                    _positions[msg.PlayerName] = 0;
                }
                _lblStatus.Text = $"Lobby: {_players.Count} / 5 players";
                UpdateWaitingLabel();
                _trackPanel.Invalidate();
                break;

            // ── A player left ─────────────────────────────────────────────────
            case MessageType.PlayerLeft:
                _players.RemoveAll(p => p.Name == msg.PlayerName);
                _positions.Remove(msg.PlayerName);
                _lblStatus.Text = $"Lobby: {_players.Count} / 5 players";
                _trackPanel.Invalidate();
                break;

            // ── Countdown tick (3 / 2 / 1 / Go!) ─────────────────────────────
            case MessageType.Countdown:
                _countdown = msg.Message ?? string.Empty;
                _phase     = GamePhase.Countdown;
                _btnReady.Enabled = false;   // disable buttons during countdown
                _btnMove.Enabled  = false;
                _trackPanel.Invalidate();
                break;

            // ── Race has started ──────────────────────────────────────────────
            case MessageType.GameStart:
                _phase     = GamePhase.InProgress;
                _countdown = string.Empty;   // clear the countdown overlay
                _winnerText = string.Empty;
                if (msg.Players is not null)
                {
                    _players.Clear();
                    _players.AddRange(msg.Players);
                    foreach (var p in _players)
                        _positions[p.Name] = 0;
                }
                // Hide waiting UI, enable the move button
                _lblWaiting.Visible  = false;
                _btnReady.Visible    = false;
                _btnMove.Enabled     = true;
                _btnRestart.Visible  = false;
                _btnQuit.Visible     = false;
                _lblStatus.Text      = "Race started! Click MOVE to advance your car!";
                _trackPanel.Invalidate();
                break;

            // ── Position update (all cars moved) ──────────────────────────────
            case MessageType.PositionUpdate:
                if (msg.Positions is not null)
                    foreach (var kv in msg.Positions)
                        _positions[kv.Key] = kv.Value;
                _trackPanel.Invalidate();
                break;

            // ── Race over ─────────────────────────────────────────────────────
            case MessageType.GameOver:
                _phase = GamePhase.Finished;
                if (msg.Positions is not null)
                    foreach (var kv in msg.Positions)
                        _positions[kv.Key] = kv.Value;

                // Personalise the winner message for the local player
                _winnerText = msg.WinnerName == _myName
                    ? $"YOU WIN, {_myName}!"
                    : $"{msg.WinnerName} WINS!";
                if (!string.IsNullOrEmpty(msg.Message))
                    _winnerText += $"\n{msg.Message}";

                _lblStatus.Text  = _winnerText;
                _btnMove.Enabled = false;
                _btnReady.Visible   = false;

                // Show the "Play Again" and "Quit" buttons
                _btnRestart.Visible = true;
                _btnQuit.Visible    = true;
                _trackPanel.Invalidate();
                break;

            // ── WaitingRoom after game over (server reset lobby) ──────────────
            // (The server sends WaitingRoom again after a race resets, handled above)

            // ── Error from server ─────────────────────────────────────────────
            case MessageType.Error:
                MessageBox.Show(msg.Message ?? "Unknown error", "Server Error",
                                MessageBoxButtons.OK, MessageBoxIcon.Warning);
                break;
        }
    }

    /// <summary>Updates the waiting-overlay label text to show ready count.</summary>
    private void UpdateWaitingLabel()
    {
        if (_players.Count < 2)
            _lblWaiting.Text = "Waiting for players …\nAt least 2 needed.  Click Ready when set!";
        else
            _lblWaiting.Text = $"{_players.Count} players in lobby.\n{_readyStatus} ready.  Click \"I'm Ready!\" to start!";
        _lblWaiting.Visible = (_phase == GamePhase.Waiting);
    }

    /// <summary>Called when the server connection drops unexpectedly.</summary>
    private void OnDisconnected()
    {
        if (InvokeRequired) { Invoke(OnDisconnected); return; }
        _lblStatus.Text  = "Disconnected from server.";
        _btnMove.Enabled = false;
        _btnReady.Enabled = false;
    }

    // ── Button handlers ───────────────────────────────────────────────────────

    /// <summary>
    /// Sends a Ready message to the server when the player clicks "I'm Ready!".
    /// The button is disabled afterwards so players can only ready up once.
    /// </summary>
    private async void OnReadyClicked(object? sender, EventArgs e)
    {
        if (_iAmReady) return;          // guard: only send once
        _iAmReady = true;
        _btnReady.Enabled   = false;    // disable to prevent double-clicking
        _btnReady.BackColor = Color.DimGray;
        _btnReady.Text      = "Waiting…";

        try
        {
            await _net.SendAsync(new GameMessage { Type = MessageType.Ready });
        }
        catch
        {
            _lblStatus.Text = "Connection lost.";
        }
    }

    /// <summary>
    /// Sends a Move message to the server when the player clicks "MOVE!".
    /// Only active when the race is in progress.
    /// </summary>
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

    /// <summary>
    /// "Play Again" – disconnects from the current session and returns the player
    /// to the lobby (ConnectForm) so they can re-join or start a new game.
    /// </summary>
    private void OnRestartClicked(object? sender, EventArgs e)
    {
        Close();   // FormClosed handler in ConnectForm will re-show the lobby
    }

    // ── Local GamePhase shadow ─────────────────────────────────────────────────
    // Mirrors the server's phase so we know which buttons to show.
    private enum GamePhase { Waiting, Countdown, InProgress, Finished }
}

// ── Graphics extension helpers (rounded rectangles) ─────────────────────────
internal static class GraphicsExtensions
{
    /// <summary>Fills a rectangle with rounded corners.</summary>
    public static void FillRoundedRectangle(this Graphics g, Brush brush,
        float x, float y, float w, float h, float r)
    {
        using var path = RoundedRectPath(x, y, w, h, r);
        g.FillPath(brush, path);
    }

    /// <summary>Draws the outline of a rectangle with rounded corners.</summary>
    public static void DrawRoundedRectangle(this Graphics g, Pen pen,
        float x, float y, float w, float h, float r)
    {
        using var path = RoundedRectPath(x, y, w, h, r);
        g.DrawPath(pen, path);
    }

    /// <summary>Creates a GraphicsPath for a rounded rectangle.</summary>
    private static System.Drawing.Drawing2D.GraphicsPath RoundedRectPath(
        float x, float y, float w, float h, float r)
    {
        var path = new System.Drawing.Drawing2D.GraphicsPath();
        path.AddArc(x,             y,             r * 2, r * 2, 180, 90);
        path.AddArc(x + w - r * 2, y,             r * 2, r * 2, 270, 90);
        path.AddArc(x + w - r * 2, y + h - r * 2, r * 2, r * 2, 0,   90);
        path.AddArc(x,             y + h - r * 2, r * 2, r * 2, 90,  90);
        path.CloseFigure();
        return path;
    }
}
