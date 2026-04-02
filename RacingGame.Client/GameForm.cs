using RacingGame.Shared;

namespace RacingGame.Client;

/// <summary>
/// The main race screen.  Shows the track, all cars, and the appropriate
/// action buttons depending on game phase:
///   • Waiting  → "I'm Ready!" button
///   • Race     → "MOVE!" + "Resign" buttons, ping display, race timer
///   • Finished → "Play Again" + "Quit" buttons
///
/// If the server connection is lost a reconnect countdown is displayed.
/// All UI updates are triggered by messages from the server.
/// </summary>
public sealed class GameForm : Form
{
    // ── Layout constants ──────────────────────────────────────────────────────
    private const int TrackPanelHeight = 420;   // height of the race track panel in pixels
    private const int LaneHeight       = 60;    // default height of one lane
    private const int CarWidth         = 80;    // width of a car image / drawn car
    private const int CarHeight        = 36;    // height of a car image / drawn car
    private const int TrackLeft        = 10;    // x-coordinate of the start line
    private const int TrackRight       = 860;   // x-coordinate of the finish line
    private const int TrackUsable      = TrackRight - TrackLeft - CarWidth;  // moveable pixels

    // ── Network / identity ────────────────────────────────────────────────────
    private readonly NetworkClient _net;          // TCP connection to the server
    private readonly string        _myName;       // this player's display name
    private readonly int           _myCarChoice;  // this player's car number (1-3)
    private readonly string        _serverHost;   // server hostname (for reconnect)
    private readonly int           _serverPort;   // server port (for reconnect)

    // ── Game state ────────────────────────────────────────────────────────────
    private readonly List<PlayerInfo>        _players   = [];   // players in lane order
    private readonly Dictionary<string, int> _positions = [];   // name → position 0-100
    private GamePhase _phase       = GamePhase.Waiting;
    private string    _winnerText  = string.Empty;   // text shown on the winner overlay
    private string    _countdown   = string.Empty;   // current countdown tick
    private bool      _iAmReady    = false;           // true after local player clicks Ready
    private string    _readyStatus = string.Empty;   // e.g. "1/3 ready" from server

    // ── Ping / latency ────────────────────────────────────────────────────────
    private long _lastPingSentTicks = 0;   // ticks when the last Ping was sent
    private int  _pingMs = -1;            // last measured round-trip in ms (-1 = unknown)
    private readonly System.Windows.Forms.Timer _pingTimer = new() { Interval = 2000 };

    // ── Race timer ────────────────────────────────────────────────────────────
    private DateTime _raceStartTime;     // when the current race started
    private readonly System.Windows.Forms.Timer _raceTimer = new() { Interval = 1000 };

    // ── Reconnect countdown (shown when connection drops) ─────────────────────
    private int _reconnectSecondsLeft = 0;
    private readonly System.Windows.Forms.Timer _reconnectTimer = new() { Interval = 1000 };

    // ── Car images loaded from the images/ folder ─────────────────────────────
    private readonly Image?[] _carImages = new Image?[4];  // index 1-3 used (index 0 unused)

    // ── Controls ──────────────────────────────────────────────────────────────
    private readonly Panel  _trackPanel   = new();   // owner-drawn race track
    private readonly Label  _lblStatus    = new();   // top status bar
    private readonly Label  _lblWaiting   = new();   // overlay on track while waiting
    private readonly Label  _lblPing      = new();   // top-right ping display
    private readonly Label  _lblRaceTime  = new();   // elapsed race time display
    private readonly Button _btnReady     = new();   // "I'm Ready!" – waiting phase
    private readonly Button _btnMove      = new();   // "MOVE!" – race phase
    private readonly Button _btnResign    = new();   // "Resign" – race phase
    private readonly Button _btnRestart   = new();   // "Play Again" – after race
    private readonly Button _btnQuit      = new();   // "Quit" – after race
    private readonly Panel  _reconnectPanel = new(); // shown when disconnected

    public GameForm(NetworkClient net, string playerName, int carChoice,
                    string serverHost, int serverPort)
    {
        _net        = net;
        _myName     = playerName;
        _myCarChoice = carChoice;
        _serverHost = serverHost;
        _serverPort = serverPort;

        Text            = $"Racing Game  –  {playerName}";
        Size            = new Size(920, 590);
        StartPosition   = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox     = false;
        BackColor       = Color.FromArgb(15, 15, 25);
        ForeColor       = Color.White;

        // Load car sprite images from the images/ folder if available
        LoadCarImages();

        // Build all the controls
        BuildUI();

        // Subscribe to network events
        net.MessageReceived += OnMessageReceived;
        net.Disconnected    += OnDisconnected;

        // Start the ping timer so we measure latency every 2 seconds
        _pingTimer.Tick += OnPingTimerTick;
        _pingTimer.Start();

        // Race timer ticks every second to update the elapsed-time display
        _raceTimer.Tick += (_, _) => UpdateRaceTimeLabel();

        // Reconnect countdown timer
        _reconnectTimer.Tick += OnReconnectTimerTick;

        // Unsubscribe and clean up when the form is closed
        FormClosing += OnFormClosing;
    }

    // ── Image loading ─────────────────────────────────────────────────────────

    /// <summary>
    /// Looks for Car_1.png, Car_2.png, Car_3.png in the "images" folder next to
    /// the executable and caches them.  Falls back to drawn cars if files are missing.
    /// </summary>
    private void LoadCarImages()
    {
        string imgDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "images");
        for (int i = 1; i <= 3; i++)
        {
            string path = Path.Combine(imgDir, $"Car_{i}.png");
            if (File.Exists(path))
            {
                try { _carImages[i] = Image.FromFile(path); }
                catch { /* file exists but couldn't be read – use drawn car */ }
            }
        }
    }

    // ── UI construction ───────────────────────────────────────────────────────

    private void BuildUI()
    {
        // ── Status bar (top, shows current game state) ────────────────────────
        _lblStatus.Location  = new Point(10, 14);
        _lblStatus.Size      = new Size(780, 28);
        _lblStatus.Font      = new Font("Segoe UI", 11, FontStyle.Bold);
        _lblStatus.ForeColor = Color.Gold;
        _lblStatus.TextAlign = ContentAlignment.MiddleCenter;
        _lblStatus.Text      = "Waiting for players …";
        Controls.Add(_lblStatus);

        // ── Ping label (top-right, shows round-trip latency) ──────────────────
        _lblPing.Location  = new Point(795, 14);
        _lblPing.Size      = new Size(110, 28);
        _lblPing.Font      = new Font("Consolas", 10);
        _lblPing.ForeColor = Color.LimeGreen;
        _lblPing.TextAlign = ContentAlignment.MiddleRight;
        _lblPing.Text      = "Ping: –";
        Controls.Add(_lblPing);

        // ── Race track panel (center, all drawing done in DrawTrack) ──────────
        _trackPanel.Location    = new Point(10, 50);
        _trackPanel.Size        = new Size(880, TrackPanelHeight);
        _trackPanel.BackColor   = Color.FromArgb(30, 30, 45);
        _trackPanel.BorderStyle = BorderStyle.FixedSingle;
        _trackPanel.Paint      += DrawTrack;
        Controls.Add(_trackPanel);

        // ── Waiting overlay (shown on the track before the race begins) ────────
        _lblWaiting.Location  = new Point(200, 150);
        _lblWaiting.Size      = new Size(480, 100);
        _lblWaiting.Font      = new Font("Segoe UI", 16, FontStyle.Bold);
        _lblWaiting.ForeColor = Color.Cyan;
        _lblWaiting.BackColor = Color.Transparent;
        _lblWaiting.TextAlign = ContentAlignment.MiddleCenter;
        _lblWaiting.Text      = "Waiting for players …\nAt least 2 needed.  Click Ready!";
        _trackPanel.Controls.Add(_lblWaiting);

        // ── Race timer label (bottom-left, shown during race) ─────────────────
        _lblRaceTime.Location  = new Point(10, 496);
        _lblRaceTime.Size      = new Size(160, 28);
        _lblRaceTime.Font      = new Font("Consolas", 11);
        _lblRaceTime.ForeColor = Color.LightGray;
        _lblRaceTime.Text      = string.Empty;
        Controls.Add(_lblRaceTime);

        // ── Resign button (shown during race, left side) ──────────────────────
        _btnResign.Text      = "Resign";
        _btnResign.Location  = new Point(180, 490);
        _btnResign.Size      = new Size(120, 44);
        _btnResign.Font      = new Font("Segoe UI", 11, FontStyle.Bold);
        _btnResign.BackColor = Color.DarkRed;
        _btnResign.ForeColor = Color.White;
        _btnResign.FlatStyle = FlatStyle.Flat;
        _btnResign.FlatAppearance.BorderSize = 0;
        _btnResign.Cursor    = Cursors.Hand;
        _btnResign.Visible   = false;   // only shown while race is in progress
        _btnResign.Click    += OnResignClicked;
        Controls.Add(_btnResign);

        // ── Ready button (shown in waiting room) ──────────────────────────────
        _btnReady.Text      = "I'm Ready!";
        _btnReady.Location  = new Point(310, 490);
        _btnReady.Size      = new Size(160, 44);
        _btnReady.Font      = new Font("Segoe UI", 13, FontStyle.Bold);
        _btnReady.BackColor = Color.ForestGreen;
        _btnReady.ForeColor = Color.White;
        _btnReady.FlatStyle = FlatStyle.Flat;
        _btnReady.FlatAppearance.BorderSize = 0;
        _btnReady.Cursor    = Cursors.Hand;
        _btnReady.Click    += OnReadyClicked;
        Controls.Add(_btnReady);

        // ── Move button (shown during race) ──────────────────────────────────
        _btnMove.Text      = "MOVE!";
        _btnMove.Location  = new Point(490, 490);
        _btnMove.Size      = new Size(200, 44);
        _btnMove.Font      = new Font("Segoe UI", 14, FontStyle.Bold);
        _btnMove.BackColor = Color.DodgerBlue;
        _btnMove.ForeColor = Color.White;
        _btnMove.FlatStyle = FlatStyle.Flat;
        _btnMove.FlatAppearance.BorderSize = 0;
        _btnMove.Cursor    = Cursors.Hand;
        _btnMove.Enabled   = false;
        _btnMove.Visible   = false;   // only shown during race
        _btnMove.Click    += OnMoveClicked;
        Controls.Add(_btnMove);

        // ── Play Again button (shown after race ends) ─────────────────────────
        _btnRestart.Text      = "Play Again";
        _btnRestart.Location  = new Point(310, 490);
        _btnRestart.Size      = new Size(160, 44);
        _btnRestart.Font      = new Font("Segoe UI", 13, FontStyle.Bold);
        _btnRestart.BackColor = Color.DodgerBlue;
        _btnRestart.ForeColor = Color.White;
        _btnRestart.FlatStyle = FlatStyle.Flat;
        _btnRestart.FlatAppearance.BorderSize = 0;
        _btnRestart.Cursor    = Cursors.Hand;
        _btnRestart.Visible   = false;
        // Closing this form returns to ConnectForm because ConnectForm registered:
        //   gameForm.FormClosed += (_, _) => Show();
        _btnRestart.Click += (_, _) => Close();
        Controls.Add(_btnRestart);

        // ── Quit button (shown after race ends) ───────────────────────────────
        _btnQuit.Text      = "Quit";
        _btnQuit.Location  = new Point(490, 490);
        _btnQuit.Size      = new Size(200, 44);
        _btnQuit.Font      = new Font("Segoe UI", 13, FontStyle.Bold);
        _btnQuit.BackColor = Color.Firebrick;
        _btnQuit.ForeColor = Color.White;
        _btnQuit.FlatStyle = FlatStyle.Flat;
        _btnQuit.FlatAppearance.BorderSize = 0;
        _btnQuit.Cursor    = Cursors.Hand;
        _btnQuit.Visible   = false;
        // Closing this form also returns to ConnectForm (see above)
        _btnQuit.Click += (_, _) => Close();
        Controls.Add(_btnQuit);

        // ── Reconnect panel (shown when the TCP connection is lost) ───────────
        // Overlays the button row; hidden until a disconnect event fires.
        _reconnectPanel.Location  = new Point(10, 485);
        _reconnectPanel.Size      = new Size(880, 60);
        _reconnectPanel.BackColor = Color.FromArgb(50, 0, 0);
        _reconnectPanel.Visible   = false;
        Controls.Add(_reconnectPanel);

        var lblReconnect = new Label
        {
            Name      = "lblReconnect",
            Location  = new Point(10, 10),
            Size      = new Size(460, 36),
            Font      = new Font("Segoe UI", 11, FontStyle.Bold),
            ForeColor = Color.OrangeRed,
            TextAlign = ContentAlignment.MiddleLeft,
            Text      = "Connection lost!"
        };
        _reconnectPanel.Controls.Add(lblReconnect);

        var btnReturnToLobby = new Button
        {
            Text      = "Return to Lobby",
            Location  = new Point(490, 8),
            Size      = new Size(170, 38),
            Font      = new Font("Segoe UI", 10, FontStyle.Bold),
            BackColor = Color.DodgerBlue,
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Cursor    = Cursors.Hand
        };
        btnReturnToLobby.FlatAppearance.BorderSize = 0;
        // Close the form → ConnectForm re-appears so the player can rejoin
        btnReturnToLobby.Click += (_, _) => Close();
        _reconnectPanel.Controls.Add(btnReturnToLobby);
    }

    // ── Track painting ────────────────────────────────────────────────────────

    // Two alternating lane background colours
    private static readonly Color[] LaneColors =
    [
        Color.FromArgb(45, 45, 65),
        Color.FromArgb(38, 38, 58)
    ];

    // Car colours (one per lane index, used when no image file is found)
    private static readonly Color[] CarColors =
    [
        Color.DeepSkyBlue,   // Car 1 / lane 0
        Color.OrangeRed,     // Car 2 / lane 1
        Color.LimeGreen,     // Car 3 / lane 2
        Color.Gold,          // lane 3 (extra player)
        Color.Violet         // lane 4 (extra player)
    ];

    /// <summary>
    /// Paints the entire race track: lanes, start/finish lines, cars, and any
    /// active overlay (countdown or winner announcement).
    /// Called automatically by Windows Forms whenever the panel needs redrawing.
    /// </summary>
    private void DrawTrack(object? sender, PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

        int playerCount = Math.Max(_players.Count, 1);
        // Shrink lane height if there are many players so they all fit on screen
        int laneH = Math.Min(LaneHeight, (TrackPanelHeight - 20) / playerCount);

        // ── Draw lane backgrounds ─────────────────────────────────────────────
        for (int i = 0; i < _players.Count; i++)
        {
            int laneY = 10 + i * laneH;
            // Alternate between two slightly different dark colours
            g.FillRectangle(new SolidBrush(LaneColors[i % 2]),
                            TrackLeft, laneY, TrackRight - TrackLeft, laneH - 2);

            // Thin divider line between lanes
            using var divPen = new Pen(Color.FromArgb(60, 60, 80), 1);
            g.DrawLine(divPen, TrackLeft, laneY + laneH - 2, TrackRight, laneY + laneH - 2);
        }

        // ── Start line ───────────────────────────────────────────────────────
        using var startPen = new Pen(Color.White, 2);
        g.DrawLine(startPen, TrackLeft + CarWidth + 2, 8,
                             TrackLeft + CarWidth + 2, 10 + _players.Count * laneH - 10);

        // ── Chequered finish line ─────────────────────────────────────────────
        DrawFinishLine(g, playerCount, laneH);

        // ── Draw each player's car ────────────────────────────────────────────
        for (int i = 0; i < _players.Count; i++)
        {
            var  p     = _players[i];
            int  pos   = _positions.TryGetValue(p.Name, out int v) ? v : 0;
            // Convert position (0-100) to a pixel x-coordinate on the track
            int  carX  = TrackLeft + CarWidth + 4 + (int)((pos / 100.0) * TrackUsable);
            int  laneY = 10 + i * laneH;
            int  carY  = laneY + (laneH - CarHeight) / 2;
            bool isMe  = (p.Name == _myName);

            // Use a loaded image if available, otherwise draw with code
            int imgIndex = Math.Clamp(p.CarChoice, 1, 3);
            if (_carImages[imgIndex] is Image img)
                g.DrawImage(img, carX, carY, CarWidth, CarHeight);
            else
                DrawCar(g, carX, carY, CarColors[i % CarColors.Length], isMe);

            // White border around the local player's car for easy identification
            if (isMe)
            {
                using var pen = new Pen(Color.White, 2);
                g.DrawRoundedRectangle(pen, carX, carY + 8, CarWidth, CarHeight - 16, 6);
            }

            // Player name above the car
            using var nameFont = new Font("Segoe UI", 7.5f, FontStyle.Bold);
            g.DrawString(p.Name, nameFont, Brushes.White, carX + 2, carY - 14);
        }

        // ── Countdown overlay (3 → 2 → 1 → Go!) ──────────────────────────────
        if (!string.IsNullOrEmpty(_countdown))
        {
            // Semi-transparent dark background
            using var bg = new SolidBrush(Color.FromArgb(180, 0, 0, 0));
            g.FillRectangle(bg, 0, 0, _trackPanel.Width, _trackPanel.Height);

            // Large tick number or "Go!" text
            Color tickColor = _countdown == "Go!" ? Color.LimeGreen : Color.White;
            using var bigFont = new Font("Segoe UI", 80, FontStyle.Bold);
            g.DrawString(_countdown, bigFont, new SolidBrush(tickColor),
                         new RectangleF(0, 80, _trackPanel.Width, 260),
                         new StringFormat { Alignment = StringAlignment.Center });
        }

        // ── Winner overlay (shown after race ends) ────────────────────────────
        if (!string.IsNullOrEmpty(_winnerText))
        {
            // Semi-transparent dark background
            using var bg = new SolidBrush(Color.FromArgb(200, 0, 0, 0));
            g.FillRectangle(bg, 0, 0, _trackPanel.Width, _trackPanel.Height);

            // Large winner announcement
            using var bigFont = new Font("Segoe UI", 26, FontStyle.Bold);
            g.DrawString(_winnerText, bigFont, Brushes.Gold,
                         new RectangleF(0, 100, _trackPanel.Width, 120),
                         new StringFormat { Alignment = StringAlignment.Center });

            // Instructions below the announcement
            using var subFont = new Font("Segoe UI", 14);
            g.DrawString("Click \"Play Again\" to restart or \"Quit\" to leave.",
                         subFont, Brushes.LightGray,
                         new RectangleF(0, 230, _trackPanel.Width, 60),
                         new StringFormat { Alignment = StringAlignment.Center });
        }
    }

    /// <summary>Draws a chequered flag pattern on the right edge of the track.</summary>
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
    /// Draws a car using rounded rectangles and circles.
    /// Used when no PNG image file was found for this car slot.
    /// </summary>
    private static void DrawCar(Graphics g, int x, int y, Color color, bool isMe)
    {
        // Main body
        using var bodyBrush = new SolidBrush(color);
        g.FillRoundedRectangle(bodyBrush, x, y + 8, CarWidth, CarHeight - 16, 6);

        // Roof (lighter shade on top of body)
        using var roofBrush = new SolidBrush(isMe ? Color.White : Color.LightGray);
        g.FillRoundedRectangle(roofBrush, x + 14, y + 4, CarWidth - 28, CarHeight - 10, 5);

        // Front and rear wheels
        g.FillEllipse(Brushes.DarkGray, x + 6,             y + CarHeight - 14, 16, 16);
        g.FillEllipse(Brushes.DarkGray, x + CarWidth - 22, y + CarHeight - 14, 16, 16);
        g.FillEllipse(Brushes.Gray,     x + 8,             y + CarHeight - 12, 12, 12);
        g.FillEllipse(Brushes.Gray,     x + CarWidth - 20, y + CarHeight - 12, 12, 12);
    }

    // ── Message handling ──────────────────────────────────────────────────────

    /// <summary>
    /// Handles a <see cref="GameMessage"/> from the server.
    /// Always runs on the UI thread (marshalled via Invoke if needed).
    /// </summary>
    private void OnMessageReceived(GameMessage msg)
    {
        if (InvokeRequired) { Invoke(() => OnMessageReceived(msg)); return; }

        switch (msg.Type)
        {
            // ── Lobby snapshot (received when joining or after a game resets) ──
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
                ParseReadyCounts(_readyStatus, out int rdy, out int tot);
                _lblStatus.Text = $"Lobby: {tot} / 5 players  |  {rdy} ready";
                UpdateWaitingLabel();

                // If this WaitingRoom arrives after a game ended, reset the UI
                if (_phase == GamePhase.Finished)
                {
                    _phase = GamePhase.Waiting;
                    _iAmReady = false;
                    _winnerText = string.Empty;
                    _countdown  = string.Empty;
                    _raceTimer.Stop();
                    _lblRaceTime.Text = string.Empty;
                    SetButtonState(GamePhase.Waiting);
                }
                _trackPanel.Invalidate();
                break;

            // ── A new player joined the lobby ─────────────────────────────────
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

            // ── A player left (disconnected or resigned) ──────────────────────
            case MessageType.PlayerLeft:
                _players.RemoveAll(p => p.Name == msg.PlayerName);
                _positions.Remove(msg.PlayerName);
                _lblStatus.Text = $"Lobby: {_players.Count} / 5 players";
                _trackPanel.Invalidate();
                break;

            // ── Countdown tick: 3 / 2 / 1 / Go! ─────────────────────────────
            case MessageType.Countdown:
                _countdown = msg.Message ?? string.Empty;
                _phase     = GamePhase.Countdown;
                // Disable all buttons during the countdown
                _btnReady.Enabled = false;
                _btnMove.Enabled  = false;
                _trackPanel.Invalidate();
                break;

            // ── Race has started ──────────────────────────────────────────────
            case MessageType.GameStart:
                _phase      = GamePhase.InProgress;
                _countdown  = string.Empty;   // clear countdown overlay
                _winnerText = string.Empty;
                if (msg.Players is not null)
                {
                    _players.Clear();
                    _players.AddRange(msg.Players);
                    foreach (var p in _players) _positions[p.Name] = 0;
                }
                _lblStatus.Text  = "Race started! Click MOVE to advance!";
                _lblWaiting.Visible = false;
                SetButtonState(GamePhase.InProgress);
                // Start the race timer
                _raceStartTime = DateTime.UtcNow;
                _raceTimer.Start();
                _trackPanel.Invalidate();
                break;

            // ── Car positions updated after a move ────────────────────────────
            case MessageType.PositionUpdate:
                if (msg.Positions is not null)
                    foreach (var kv in msg.Positions)
                        _positions[kv.Key] = kv.Value;
                _trackPanel.Invalidate();
                break;

            // ── Race over ─────────────────────────────────────────────────────
            case MessageType.GameOver:
                _phase      = GamePhase.Finished;
                _raceTimer.Stop();   // freeze the race clock

                if (msg.Positions is not null)
                    foreach (var kv in msg.Positions)
                        _positions[kv.Key] = kv.Value;

                // Personalise the message for the local player
                _winnerText = msg.WinnerName == _myName
                    ? $"🏆  YOU WIN, {_myName}!  🏆"
                    : $"🏆  {msg.WinnerName} WINS!  🏆";
                if (!string.IsNullOrEmpty(msg.Message))
                    _winnerText += $"\n{msg.Message}";

                _lblStatus.Text = _winnerText;
                SetButtonState(GamePhase.Finished);

                // Save to local high scores if this player won
                if (msg.WinnerName == _myName)
                    HighScoreManager.RecordWin(_myName);

                _trackPanel.Invalidate();
                break;

            // ── Pong (response to our Ping) ───────────────────────────────────
            case MessageType.Pong:
                if (msg.Timestamp.HasValue)
                {
                    // Calculate round-trip time in milliseconds
                    _pingMs = (int)TimeSpan.FromTicks(
                        DateTime.UtcNow.Ticks - msg.Timestamp.Value).TotalMilliseconds;
                    _lblPing.Text      = $"Ping: {_pingMs} ms";
                    // Colour-code: green ≤ 80ms, yellow ≤ 200ms, red > 200ms
                    _lblPing.ForeColor = _pingMs <= 80  ? Color.LimeGreen
                                       : _pingMs <= 200 ? Color.Yellow
                                       :                  Color.OrangeRed;
                }
                break;

            // ── Error from server ─────────────────────────────────────────────
            case MessageType.Error:
                MessageBox.Show(msg.Message ?? "Unknown error", "Server Error",
                                MessageBoxButtons.OK, MessageBoxIcon.Warning);
                break;
        }
    }

    /// <summary>
    /// Called when the TCP connection drops unexpectedly.
    /// Shows the reconnect panel with a 30-second countdown.
    /// </summary>
    private void OnDisconnected()
    {
        if (InvokeRequired) { Invoke(OnDisconnected); return; }

        // Stop timers that depend on the connection
        _pingTimer.Stop();
        _raceTimer.Stop();

        // Disable action buttons
        _btnMove.Enabled  = false;
        _btnReady.Enabled = false;
        _btnResign.Visible = false;

        // Start the 30-second reconnect countdown
        _reconnectSecondsLeft = 30;
        UpdateReconnectLabel();
        _reconnectPanel.Visible = true;
        _reconnectTimer.Start();

        _lblStatus.Text = "Connection lost!";
    }

    // ── Button handlers ───────────────────────────────────────────────────────

    /// <summary>
    /// Sends a Ready message to the server.  The button is disabled immediately
    /// to prevent the player from clicking it twice.
    /// </summary>
    private async void OnReadyClicked(object? sender, EventArgs e)
    {
        if (_iAmReady) return;
        _iAmReady = true;
        _btnReady.Enabled   = false;
        _btnReady.BackColor = Color.DimGray;
        _btnReady.Text      = "Waiting…";

        try { await _net.SendAsync(new GameMessage { Type = MessageType.Ready }); }
        catch { _lblStatus.Text = "Connection lost."; }
    }

    /// <summary>
    /// Sends a Move message to advance this player's car.
    /// Only processed by the server while the race is in progress.
    /// </summary>
    private async void OnMoveClicked(object? sender, EventArgs e)
    {
        if (_phase != GamePhase.InProgress) return;
        try { await _net.SendAsync(new GameMessage { Type = MessageType.Move }); }
        catch { _lblStatus.Text = "Connection lost."; _btnMove.Enabled = false; }
    }

    /// <summary>
    /// Sends a Resign message to give up the current race.
    /// Confirms with the player before sending.
    /// </summary>
    private async void OnResignClicked(object? sender, EventArgs e)
    {
        if (_phase != GamePhase.InProgress) return;

        // Ask for confirmation so the button isn't triggered accidentally
        var result = MessageBox.Show("Are you sure you want to resign?",
                                     "Resign Race",
                                     MessageBoxButtons.YesNo,
                                     MessageBoxIcon.Question);
        if (result != DialogResult.Yes) return;

        _btnResign.Enabled = false;   // prevent double-resign
        try { await _net.SendAsync(new GameMessage { Type = MessageType.Resign }); }
        catch { _lblStatus.Text = "Connection lost."; }
    }

    // ── Timer callbacks ───────────────────────────────────────────────────────

    /// <summary>
    /// Fires every 2 seconds to send a Ping and measure round-trip latency.
    /// </summary>
    private async void OnPingTimerTick(object? sender, EventArgs e)
    {
        try
        {
            _lastPingSentTicks = DateTime.UtcNow.Ticks;
            await _net.SendAsync(new GameMessage
            {
                Type      = MessageType.Ping,
                Timestamp = _lastPingSentTicks   // server will echo this back in Pong
            });
        }
        catch { /* ignore if connection already closed */ }
    }

    /// <summary>Updates the race-time label with the elapsed seconds since race start.</summary>
    private void UpdateRaceTimeLabel()
    {
        var elapsed = DateTime.UtcNow - _raceStartTime;
        // Use elapsed.Minutes (0-59 within the current hour) not TotalMinutes (total float)
        _lblRaceTime.Text = $"⏱ {elapsed.Minutes}:{elapsed.Seconds:D2}";
    }

    /// <summary>
    /// Fires every second while the reconnect countdown is running.
    /// When the countdown reaches zero the form closes and ConnectForm reappears.
    /// </summary>
    private void OnReconnectTimerTick(object? sender, EventArgs e)
    {
        _reconnectSecondsLeft--;
        if (_reconnectSecondsLeft <= 0)
        {
            _reconnectTimer.Stop();
            Close();   // return to ConnectForm
        }
        else
        {
            UpdateReconnectLabel();
        }
    }

    /// <summary>Updates the countdown text inside the reconnect panel.</summary>
    private void UpdateReconnectLabel()
    {
        if (_reconnectPanel.Controls["lblReconnect"] is Label lbl)
            lbl.Text = $"Connection lost!  Returning to lobby in {_reconnectSecondsLeft}s …";
    }

    // ── UI helpers ────────────────────────────────────────────────────────────

    /// <summary>
    /// Shows/hides and enables/disables the action buttons to match the current
    /// game phase so only the relevant buttons are visible at any one time.
    /// </summary>
    private void SetButtonState(GamePhase phase)
    {
        // Waiting phase: show Ready button only
        _btnReady.Visible    = (phase == GamePhase.Waiting);
        _btnReady.Enabled    = (phase == GamePhase.Waiting) && !_iAmReady;

        // Race phase: show Move + Resign buttons
        _btnMove.Visible     = (phase == GamePhase.InProgress);
        _btnMove.Enabled     = (phase == GamePhase.InProgress);
        _btnResign.Visible   = (phase == GamePhase.InProgress);
        _btnResign.Enabled   = (phase == GamePhase.InProgress);

        // Finished phase: show Play Again + Quit buttons
        _btnRestart.Visible  = (phase == GamePhase.Finished);
        _btnQuit.Visible     = (phase == GamePhase.Finished);
    }

    /// <summary>Updates the waiting-overlay label text with current ready count.</summary>
    private void UpdateWaitingLabel()
    {
        if (_players.Count < 2)
            _lblWaiting.Text = "Waiting for players …\nAt least 2 needed.  Click Ready!";
        else
            _lblWaiting.Text = $"{_players.Count} players in lobby.\n{_readyStatus} ready.  Click \"I'm Ready!\" to start!";
        _lblWaiting.Visible = (_phase == GamePhase.Waiting);
    }

    /// <summary>Parses "readyCount/totalCount" from the WaitingRoom message.</summary>
    private static void ParseReadyCounts(string text, out int ready, out int total)
    {
        ready = 0; total = 0;
        if (string.IsNullOrEmpty(text)) return;
        var parts = text.Split('/');
        if (parts.Length == 2)
        {
            int.TryParse(parts[0], out ready);
            int.TryParse(parts[1], out total);
        }
    }

    // ── Cleanup ───────────────────────────────────────────────────────────────

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        // Stop all timers
        _pingTimer.Stop();
        _raceTimer.Stop();
        _reconnectTimer.Stop();

        // Unsubscribe from network events and close the TCP connection
        _net.MessageReceived -= OnMessageReceived;
        _net.Disconnected    -= OnDisconnected;
        _net.Dispose();
    }

    // ── Local GamePhase shadow ─────────────────────────────────────────────────
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
