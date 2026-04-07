using System.Drawing.Drawing2D;
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

    // ── Neon animation (pulsating glow effect) ────────────────────────────────
    private float _glowPhase = 0f;   // advances each tick to drive sine-wave glow
    private readonly System.Windows.Forms.Timer _animTimer = new() { Interval = 50 };

    // ── Car images loaded from the images/ folder ─────────────────────────────
    private readonly Image?[] _carImages = new Image?[4];  // index 1-3 used (index 0 unused)

    // ── Controls ──────────────────────────────────────────────────────────────
    private readonly DoubleBufferedPanel _trackPanel   = new();   // owner-drawn race track
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

        // Neon animation: advance glow phase every 50 ms, then request a repaint
        _animTimer.Tick += (_, _) =>
        {
            _glowPhase += 0.12f;
            if (_glowPhase > MathF.PI * 2) _glowPhase -= MathF.PI * 2;
            _trackPanel.Invalidate();
        };
        _animTimer.Start();

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
        _trackPanel.BackColor   = Color.FromArgb(8, 8, 18);
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
        Color.FromArgb(28, 28, 48),
        Color.FromArgb(20, 20, 38)
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
    /// Paints the entire race track with neon cyberpunk visuals: gradient lane
    /// backgrounds, pulsating cyan edge lights, enhanced cars, and overlays for
    /// the countdown and winner announcement.
    /// </summary>
    private void DrawTrack(object? sender, PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode      = SmoothingMode.AntiAlias;
        g.InterpolationMode  = InterpolationMode.HighQualityBicubic;
        g.CompositingQuality = CompositingQuality.HighQuality;

        int playerCount = Math.Max(_players.Count, 1);
        int laneH = Math.Min(LaneHeight, (TrackPanelHeight - 20) / playerCount);
        int trackW = _trackPanel.Width;
        int trackH = _trackPanel.Height;

        // ── Full background gradient ──────────────────────────────────────────
        using (var bgBrush = new LinearGradientBrush(
                   new Rectangle(0, 0, trackW, trackH),
                   Color.FromArgb(6, 6, 16),
                   Color.FromArgb(14, 14, 28),
                   LinearGradientMode.Vertical))
        {
            g.FillRectangle(bgBrush, 0, 0, trackW, trackH);
        }

        // Subtle cyan grid overlay
        using (var gridPen = new Pen(Color.FromArgb(12, 0, 200, 220), 1f))
        {
            for (int gx = 0; gx < trackW; gx += 40)
                g.DrawLine(gridPen, gx, 0, gx, trackH);
            for (int gy = 0; gy < trackH; gy += 40)
                g.DrawLine(gridPen, 0, gy, trackW, gy);
        }

        // ── Lane backgrounds ──────────────────────────────────────────────────
        for (int i = 0; i < _players.Count; i++)
        {
            int laneY = 10 + i * laneH;
            using (var laneBrush = new LinearGradientBrush(
                       new Rectangle(TrackLeft, laneY, TrackRight - TrackLeft, Math.Max(1, laneH - 2)),
                       i % 2 == 0 ? Color.FromArgb(32, 32, 52) : Color.FromArgb(24, 24, 42),
                       i % 2 == 0 ? Color.FromArgb(22, 22, 40) : Color.FromArgb(16, 16, 32),
                       LinearGradientMode.Vertical))
            {
                g.FillRectangle(laneBrush, TrackLeft, laneY, TrackRight - TrackLeft, laneH - 2);
            }

            // Neon cyan lane-divider line
            float lineY = laneY + laneH - 2;
            DrawNeonLine(g, Color.FromArgb(60, 200, 255),
                         TrackLeft, lineY, TrackRight, lineY,
                         coreWidth: 1f, glowWidth: 5f, glowAlpha: 18);
        }

        // ── Pulsating neon track borders (top, bottom, left, right) ──────────
        int pulseAlpha = (int)(38 + 22 * MathF.Sin(_glowPhase));
        int topY  = 10;
        int botY  = 10 + _players.Count * laneH - 2;

        DrawNeonLine(g, Color.Cyan, TrackLeft, topY,  TrackRight, topY,  2f, 9f, pulseAlpha);
        DrawNeonLine(g, Color.Cyan, TrackLeft, botY,  TrackRight, botY,  2f, 9f, pulseAlpha);
        DrawNeonLine(g, Color.Cyan, TrackLeft, topY,  TrackLeft,  botY,  2f, 9f, pulseAlpha);
        DrawNeonLine(g, Color.Cyan, TrackRight, topY, TrackRight, botY,  2f, 9f, pulseAlpha);

        // ── Start line ────────────────────────────────────────────────────────
        int startX = TrackLeft + CarWidth + 2;
        DrawNeonLine(g, Color.White, startX, topY, startX, botY, 2f, 6f, 55);
        using (var startFont = new Font("Segoe UI", 6.5f, FontStyle.Bold))
        using (var startBrush = new SolidBrush(Color.FromArgb(140, 255, 255, 255)))
            g.DrawString("START", startFont, startBrush, startX - 14, topY - 13);

        // ── Chequered finish line ─────────────────────────────────────────────
        DrawFinishLine(g, playerCount, laneH);

        // ── Draw each player's car ────────────────────────────────────────────
        for (int i = 0; i < _players.Count; i++)
        {
            var  p     = _players[i];
            int  pos   = _positions.TryGetValue(p.Name, out int v) ? v : 0;
            int  carX  = TrackLeft + CarWidth + 4 + (int)((pos / 100.0) * TrackUsable);
            int  laneY = 10 + i * laneH;
            int  carY  = laneY + (laneH - CarHeight) / 2;
            bool isMe  = (p.Name == _myName);

            int imgIndex = Math.Clamp(p.CarChoice, 1, 3);
            if (_carImages[imgIndex] is Image img)
                g.DrawImage(img, carX, carY, CarWidth, CarHeight);
            else
                DrawCar(g, carX, carY, CarColors[i % CarColors.Length], isMe);

            // Pulsating cyan halo around the local player's car
            if (isMe)
            {
                int glowA = (int)(110 + 90 * MathF.Sin(_glowPhase));
                using (var glowPen = new Pen(Color.FromArgb(glowA / 3, Color.Cyan), 10f))
                    g.DrawRoundedRectangle(glowPen, carX - 4, carY + 5, CarWidth + 8, CarHeight - 10, 10);
                using (var haloPen = new Pen(Color.FromArgb(glowA, Color.Cyan), 2.5f))
                    g.DrawRoundedRectangle(haloPen, carX - 2, carY + 7, CarWidth + 4, CarHeight - 14, 8);
            }

            // Player name with drop shadow
            using (var nameFont = new Font("Segoe UI", 7.5f, FontStyle.Bold))
            {
                using (var shadowBrush = new SolidBrush(Color.FromArgb(180, 0, 0, 0)))
                    g.DrawString(p.Name, nameFont, shadowBrush, carX + 3, carY - 13);
                g.DrawString(p.Name, nameFont, Brushes.White, carX + 2, carY - 14);
            }
        }

        // ── Countdown overlay ─────────────────────────────────────────────────
        if (!string.IsNullOrEmpty(_countdown))
        {
            using (var bg = new SolidBrush(Color.FromArgb(185, 0, 0, 0)))
                g.FillRectangle(bg, 0, 0, trackW, trackH);

            Color tickColor = _countdown == "Go!" ? Color.LimeGreen
                            : _countdown == "3"   ? Color.OrangeRed
                            : _countdown == "2"   ? Color.Orange
                            :                       Color.Cyan;

            // Pulsating ring behind the number
            int cx = trackW / 2;
            int cy = trackH / 2 - 40;
            int ringA = (int)(80 + 80 * MathF.Sin(_glowPhase * 2));
            using (var ringBrush = new SolidBrush(Color.FromArgb(ringA / 4, tickColor)))
                g.FillEllipse(ringBrush, cx - 95, cy - 95, 190, 190);
            using (var ringPen = new Pen(Color.FromArgb(ringA, tickColor), 3f))
                g.DrawEllipse(ringPen, cx - 95, cy - 95, 190, 190);

            using (var bigFont = new Font("Segoe UI", 80, FontStyle.Bold))
            {
                var fmt = new StringFormat { Alignment = StringAlignment.Center };
                // Shadow
                using (var shadowBrush = new SolidBrush(Color.FromArgb(110, tickColor)))
                    g.DrawString(_countdown, bigFont, shadowBrush,
                                 new RectangleF(3, 83, trackW, 260), fmt);
                // Main text
                using (var textBrush = new SolidBrush(tickColor))
                    g.DrawString(_countdown, bigFont, textBrush,
                                 new RectangleF(0, 80, trackW, 260), fmt);
            }
        }

        // ── Winner overlay ────────────────────────────────────────────────────
        if (!string.IsNullOrEmpty(_winnerText))
        {
            using (var bg = new SolidBrush(Color.FromArgb(205, 0, 0, 0)))
                g.FillRectangle(bg, 0, 0, trackW, trackH);

            // Neon box behind the text
            int boxW = 620, boxH = 170;
            int boxX = (trackW - boxW) / 2, boxY = 75;
            using (var boxBg = new SolidBrush(Color.FromArgb(70, 0, 200, 230)))
                g.FillRoundedRectangle(boxBg, boxX, boxY, boxW, boxH, 18);
            int winGlow = (int)(90 + 100 * MathF.Sin(_glowPhase));
            using (var boxPen = new Pen(Color.FromArgb(winGlow, Color.Cyan), 3f))
                g.DrawRoundedRectangle(boxPen, boxX, boxY, boxW, boxH, 18);

            var fmt = new StringFormat { Alignment = StringAlignment.Center };
            using (var bigFont = new Font("Segoe UI", 26, FontStyle.Bold))
                g.DrawString(_winnerText, bigFont, Brushes.Gold,
                             new RectangleF(0, 100, trackW, 120), fmt);
            using (var subFont = new Font("Segoe UI", 13))
            using (var subBrush = new SolidBrush(Color.LightCyan))
                g.DrawString("Click  \"Play Again\"  to restart  ·  \"Quit\"  to leave.",
                             subFont, subBrush,
                             new RectangleF(0, 232, trackW, 50), fmt);
        }
    }

    /// <summary>
    /// Draws a neon line with an outer glow, middle glow, and bright core.
    /// </summary>
    private static void DrawNeonLine(Graphics g, Color color,
        float x1, float y1, float x2, float y2,
        float coreWidth = 2f, float glowWidth = 8f, int glowAlpha = 40)
    {
        using (var outerPen = new Pen(Color.FromArgb(glowAlpha, color), glowWidth))
            g.DrawLine(outerPen, x1, y1, x2, y2);
        using (var midPen = new Pen(Color.FromArgb(Math.Min(255, glowAlpha * 2), color), glowWidth / 2.5f))
            g.DrawLine(midPen, x1, y1, x2, y2);
        using (var corePen = new Pen(color, coreWidth))
            g.DrawLine(corePen, x1, y1, x2, y2);
    }

    /// <summary>Draws a chequered flag finish line with a neon cyan glow halo.</summary>
    private static void DrawFinishLine(Graphics g, int playerCount, int laneH)
    {
        int checkSize = 10;
        int totalH    = playerCount * laneH;
        int rows      = totalH / checkSize;
        bool white    = true;
        int fx        = TrackRight - 10;
        for (int row = 0; row < rows; row++, white = !white)
        {
            g.FillRectangle(white ? Brushes.White : Brushes.Black,
                            fx, 10 + row * checkSize, 10, checkSize);
        }
        // Cyan glow on the finish column
        using (var glow = new Pen(Color.FromArgb(50, Color.Cyan), 14f))
            g.DrawLine(glow, fx + 5, 10, fx + 5, 10 + totalH - 2);
        using (var bright = new Pen(Color.FromArgb(120, Color.Cyan), 3f))
            g.DrawLine(bright, fx - 1, 10, fx - 1, 10 + totalH - 2);
    }

    /// <summary>
    /// Draws a car using gradient fills and rounded shapes.
    /// Used when no PNG image file was found for this car slot.
    /// </summary>
    private static void DrawCar(Graphics g, int x, int y, Color color, bool isMe)
    {
        // Body gradient (lighter top → darker bottom)
        Color bodyTop    = Color.FromArgb(Math.Min(255, color.R + 60),
                                          Math.Min(255, color.G + 60),
                                          Math.Min(255, color.B + 60));
        using (var bodyBrush = new LinearGradientBrush(
                   new Rectangle(x, y + 8, CarWidth, CarHeight - 16),
                   bodyTop, color, LinearGradientMode.Vertical))
        {
            g.FillRoundedRectangle(bodyBrush, x, y + 8, CarWidth, CarHeight - 16, 6);
        }

        // Metallic roof with a specular shine
        using (var roofBrush = new LinearGradientBrush(
                   new Rectangle(x + 14, y + 4, CarWidth - 28, CarHeight - 10),
                   Color.FromArgb(230, 245, 255),
                   Color.FromArgb(130, 150, 175),
                   LinearGradientMode.Vertical))
        {
            g.FillRoundedRectangle(roofBrush, x + 14, y + 4, CarWidth - 28, CarHeight - 10, 5);
        }

        // Tinted windshield
        using (var wsBrush = new SolidBrush(Color.FromArgb(70, 80, 130, 200)))
            g.FillRoundedRectangle(wsBrush, x + 16, y + 6, 20, CarHeight - 14, 3);

        // Wheels with rim highlights
        DrawWheel(g, x + 6,             y + CarHeight - 14);
        DrawWheel(g, x + CarWidth - 22, y + CarHeight - 14);

        // Headlights (front glow)
        using (var headBrush = new SolidBrush(Color.FromArgb(255, 255, 245, 180)))
        {
            g.FillEllipse(headBrush, x + CarWidth - 7, y + 11,  5, 5);
            g.FillEllipse(headBrush, x + CarWidth - 7, y + CarHeight - 17, 5, 5);
        }
    }

    /// <summary>Draws a single wheel with a dark tyre and a bright rim.</summary>
    private static void DrawWheel(Graphics g, int wx, int wy)
    {
        g.FillEllipse(Brushes.DarkSlateGray, wx, wy, 16, 16);
        using (var rimBrush = new SolidBrush(Color.FromArgb(200, 220, 230)))
            g.FillEllipse(rimBrush, wx + 3, wy + 3, 10, 10);
        g.FillEllipse(Brushes.DimGray, wx + 6, wy + 6, 4, 4);
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

            // ── Race over ─────────────────────────────────────────────────
            case MessageType.GameOver:
                _phase      = GamePhase.Finished;
                _raceTimer.Stop();   // freeze the race clock

                if (msg.Positions is not null)
                    foreach (var kv in msg.Positions)
                        _positions[kv.Key] = kv.Value;

                // Personalise the message for the local player
                string winnerName = msg.WinnerName ?? "Unknown";
                bool iWon = (winnerName == _myName);
                _winnerText = iWon
                    ? $"🏆  YOU WIN, {_myName}!  🏆"
                    : $"🏆  {winnerName} WINS!  🏆";
                if (!string.IsNullOrEmpty(msg.Message))
                    _winnerText += $"\n{msg.Message}";

                _lblStatus.Text = _winnerText;
                SetButtonState(GamePhase.Finished);

                // Save to local high scores if this player won
                if (iWon) HighScoreManager.RecordWin(_myName);

                _trackPanel.Invalidate();

                // Show the winner popup after the track has been repainted
                BeginInvoke(() => ShowWinnerPopup(winnerName, iWon));
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
    /// Shows a neon-styled popup announcing the race winner.
    /// Contains "Play Again" (closes GameForm → returns to lobby) and
    /// "Quit" (exits the application) buttons.
    /// </summary>
    private void ShowWinnerPopup(string winnerName, bool iWon)
    {
        var dlg = new Form
        {
            Text            = "Race Finished!",
            Size            = new Size(500, 320),
            StartPosition   = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.None,
            BackColor       = Color.FromArgb(10, 10, 22),
            ForeColor       = Color.White,
            ShowInTaskbar   = false,
        };

        // Neon cyan border drawn by the dialog's Paint handler
        dlg.Paint += (_, pe) =>
        {
            var g2 = pe.Graphics;
            g2.SmoothingMode = SmoothingMode.AntiAlias;
            using (var outerGlow = new Pen(Color.FromArgb(35, Color.Cyan), 10f))
                g2.DrawRectangle(outerGlow, 5, 5, dlg.Width - 10, dlg.Height - 10);
            using (var border = new Pen(Color.Cyan, 2f))
                g2.DrawRectangle(border, 2, 2, dlg.Width - 4, dlg.Height - 4);
        };

        // Trophy / flag emoji
        string icon = iWon ? "🏆" : "🏁";
        dlg.Controls.Add(new Label
        {
            Text      = icon,
            Font      = new Font("Segoe UI Emoji", 42),
            AutoSize  = true,
            Location  = new Point(210, 16),
            BackColor = Color.Transparent,
        });

        // Headline
        string headline = iWon ? $"YOU WIN, {_myName}!" : $"{winnerName} WINS!";
        dlg.Controls.Add(new Label
        {
            Text      = headline,
            Font      = new Font("Segoe UI", 26, FontStyle.Bold),
            ForeColor = iWon ? Color.Cyan : Color.Gold,
            AutoSize  = false,
            Size      = new Size(460, 56),
            Location  = new Point(20, 100),
            TextAlign = ContentAlignment.MiddleCenter,
            BackColor = Color.Transparent,
        });

        // Sub-message
        string sub = iWon ? "Congratulations! 🎉" : "Better luck next time!";
        dlg.Controls.Add(new Label
        {
            Text      = sub,
            Font      = new Font("Segoe UI", 13),
            ForeColor = Color.LightCyan,
            AutoSize  = false,
            Size      = new Size(460, 28),
            Location  = new Point(20, 160),
            TextAlign = ContentAlignment.MiddleCenter,
            BackColor = Color.Transparent,
        });

        // "Play Again" button → close popup, then close GameForm (ConnectForm re-appears)
        var btnPlay = new Button
        {
            Text      = "Play Again",
            Location  = new Point(60, 220),
            Size      = new Size(165, 50),
            Font      = new Font("Segoe UI", 13, FontStyle.Bold),
            BackColor = Color.FromArgb(0, 55, 75),
            ForeColor = Color.Cyan,
            FlatStyle = FlatStyle.Flat,
            Cursor    = Cursors.Hand,
        };
        btnPlay.FlatAppearance.BorderColor = Color.Cyan;
        btnPlay.FlatAppearance.BorderSize  = 2;
        btnPlay.Click += (_, _) => { dlg.Close(); Close(); };
        dlg.Controls.Add(btnPlay);

        // "Quit Game" button → close popup, then exit the application
        var btnQuitDlg = new Button
        {
            Text      = "Quit Game",
            Location  = new Point(270, 220),
            Size      = new Size(165, 50),
            Font      = new Font("Segoe UI", 13, FontStyle.Bold),
            BackColor = Color.FromArgb(60, 0, 0),
            ForeColor = Color.OrangeRed,
            FlatStyle = FlatStyle.Flat,
            Cursor    = Cursors.Hand,
        };
        btnQuitDlg.FlatAppearance.BorderColor = Color.OrangeRed;
        btnQuitDlg.FlatAppearance.BorderSize  = 2;
        btnQuitDlg.Click += (_, _) => { dlg.Close(); Application.Exit(); };
        dlg.Controls.Add(btnQuitDlg);

        dlg.ShowDialog(this);
    }

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
        _animTimer.Stop();

        // Unsubscribe from network events and close the TCP connection
        _net.MessageReceived -= OnMessageReceived;
        _net.Disconnected    -= OnDisconnected;
        _net.Dispose();
    }

    // ── Local GamePhase shadow ─────────────────────────────────────────────────
    private enum GamePhase { Waiting, Countdown, InProgress, Finished }
}

// ── Double-buffered panel (eliminates flicker during redraws) ────────────────
/// <summary>
/// A Panel subclass with double buffering enabled so that each redraw is
/// composited off-screen before being blitted to the display, eliminating
/// the flicker that would otherwise appear during the race animation.
/// </summary>
internal sealed class DoubleBufferedPanel : Panel
{
    public DoubleBufferedPanel()
    {
        SetStyle(ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.AllPaintingInWmPaint  |
                 ControlStyles.UserPaint, true);
        UpdateStyles();
    }
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
