using System.Drawing.Drawing2D;
using System.Net;
using System.Net.Sockets;

namespace RacingGame.Server;

/// <summary>
/// A Windows Forms window that lets the server operator:
///   • Pick the IP binding and port before starting.
///   • Start and stop the server with a single button.
///   • Watch the connected-player list and server log update in real time.
///
/// AI bots are spawned automatically when a player clicks "I'm Ready!"
/// to fill any empty slots up to the maximum of 5 players.
/// </summary>
public sealed class ServerForm : Form
{
    // ── Controls ──────────────────────────────────────────────────────────────
    private readonly ComboBox      _cboIp      = new();   // local IP addresses drop-down
    private readonly NumericUpDown _nudPort    = new();   // TCP port input
    private readonly Button        _btnStart   = new();   // Start / Stop toggle button
    private readonly ListBox       _lstPlayers = new();   // live list of connected players
    private readonly RichTextBox   _rtbLog     = new();   // scrolling server log

    // ── Server state ──────────────────────────────────────────────────────────
    private GameServer? _server;       // null when the server is stopped
    private Task?       _serverTask;   // background task for StartAsync()

    public ServerForm()
    {
        Text            = "Neon Racing 2026 – Server";
        Size            = new Size(640, 640);
        StartPosition   = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox     = false;
        BackColor       = Color.FromArgb(8, 8, 18);
        ForeColor       = Color.White;

        BuildUI();

        // Draw a neon border on the server window
        Paint += (_, e) =>
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var glow = new Pen(Color.FromArgb(30, Color.Cyan), 10f))
                g.DrawRectangle(glow, 5, 5, Width - 10, Height - 10);
            using (var line = new Pen(Color.FromArgb(120, Color.Cyan), 1.5f))
                g.DrawRectangle(line, 2, 2, Width - 5, Height - 5);
        };

        // When the window is closed, stop the server and all bots
        FormClosing += (_, _) => StopEverything();
    }

    // ── UI construction ───────────────────────────────────────────────────────

    private void BuildUI()
    {
        int y = 20;

        // ── Title ─────────────────────────────────────────────────────────────
        Controls.Add(new Label
        {
            Text      = "🏎  Neon Racing 2026 – Server",
            Font      = new Font("Segoe UI", 16, FontStyle.Bold),
            ForeColor = Color.Cyan,
            AutoSize  = true,
            Location  = new Point(20, y)
        });
        y += 45;

        // ── Subtitle / info ───────────────────────────────────────────────────
        Controls.Add(new Label
        {
            Text      = "AI bots fill empty slots automatically when a player clicks \"I'm Ready!\"",
            Font      = new Font("Segoe UI", 9, FontStyle.Italic),
            ForeColor = Color.FromArgb(100, 200, 230),
            AutoSize  = true,
            Location  = new Point(20, y)
        });
        y += 35;

        // ── IP binding drop-down ───────────────────────────────────────────────
        AddLabel("Bind IP:", y);
        _cboIp.Location      = new Point(150, y - 2);
        _cboIp.Size          = new Size(200, 28);
        _cboIp.DropDownStyle = ComboBoxStyle.DropDownList;
        _cboIp.Font          = new Font("Segoe UI", 11);
        _cboIp.BackColor     = Color.FromArgb(18, 28, 40);
        _cboIp.ForeColor     = Color.Cyan;
        // Populate with "Any (0.0.0.0)" and every local IPv4 address
        _cboIp.Items.Add("Any (0.0.0.0)");
        foreach (var ip in Dns.GetHostAddresses(Dns.GetHostName())
                               .Where(a => a.AddressFamily == AddressFamily.InterNetwork))
            _cboIp.Items.Add(ip.ToString());
        _cboIp.SelectedIndex = 0;   // default to binding on all interfaces
        Controls.Add(_cboIp);
        y += 45;

        // ── Port number ───────────────────────────────────────────────────────
        AddLabel("Port:", y);
        _nudPort.Location  = new Point(150, y - 2);
        _nudPort.Size      = new Size(110, 28);
        _nudPort.Minimum   = 1;
        _nudPort.Maximum   = 65535;
        _nudPort.Value     = 9000;          // default port matches the client
        _nudPort.Font      = new Font("Segoe UI", 11);
        _nudPort.BackColor = Color.FromArgb(18, 28, 40);
        _nudPort.ForeColor = Color.Cyan;
        Controls.Add(_nudPort);
        y += 45;

        // ── Max players info label ────────────────────────────────────────────
        Controls.Add(new Label
        {
            Text      = "Max Players: 5  (human + bots combined)",
            Font      = new Font("Segoe UI", 9),
            ForeColor = Color.DarkGray,
            AutoSize  = true,
            Location  = new Point(150, y)
        });
        y += 30;

        // ── Start / Stop button ───────────────────────────────────────────────
        _btnStart.Text      = "▶  Start Server";
        _btnStart.Location  = new Point(150, y);
        _btnStart.Size      = new Size(170, 44);
        _btnStart.Font      = new Font("Segoe UI", 13, FontStyle.Bold);
        _btnStart.BackColor = Color.FromArgb(0, 70, 20);
        _btnStart.ForeColor = Color.LimeGreen;
        _btnStart.FlatStyle = FlatStyle.Flat;
        _btnStart.FlatAppearance.BorderColor = Color.LimeGreen;
        _btnStart.FlatAppearance.BorderSize  = 2;
        _btnStart.Cursor    = Cursors.Hand;
        _btnStart.Click    += OnStartStopClicked;
        Controls.Add(_btnStart);
        y += 62;

        // ── Connected players list (left column) ──────────────────────────────
        Controls.Add(new Label
        {
            Text      = "Connected Players:",
            Location  = new Point(20, y),
            Size      = new Size(200, 24),
            Font      = new Font("Segoe UI", 10, FontStyle.Bold),
            ForeColor = Color.Cyan
        });
        y += 26;

        _lstPlayers.Location    = new Point(20, y);
        _lstPlayers.Size        = new Size(195, 310);
        _lstPlayers.Font        = new Font("Segoe UI", 10);
        _lstPlayers.BackColor   = Color.FromArgb(10, 18, 28);
        _lstPlayers.ForeColor   = Color.Cyan;
        _lstPlayers.BorderStyle = BorderStyle.FixedSingle;
        Controls.Add(_lstPlayers);

        // ── Server log (right column) ─────────────────────────────────────────
        Controls.Add(new Label
        {
            Text      = "Server Log:",
            Location  = new Point(230, y - 26),
            Size      = new Size(150, 24),
            Font      = new Font("Segoe UI", 10, FontStyle.Bold),
            ForeColor = Color.Cyan
        });

        _rtbLog.Location    = new Point(230, y);
        _rtbLog.Size        = new Size(385, 310);
        _rtbLog.Font        = new Font("Consolas", 9);
        _rtbLog.BackColor   = Color.FromArgb(4, 8, 14);
        _rtbLog.ForeColor   = Color.Cyan;
        _rtbLog.ReadOnly    = true;
        _rtbLog.ScrollBars  = RichTextBoxScrollBars.Vertical;
        _rtbLog.BorderStyle = BorderStyle.FixedSingle;
        Controls.Add(_rtbLog);

        // ── Placeholder log entries so the UI looks active for a presentation ─
        AppendLog("=== Neon Racing 2026 – Server Console ===");
        AppendLog("Press \"Start Server\" to begin accepting players.");
        AppendLog("Up to 5 players (humans + bots) per race.");
        AppendLog("AI bots auto-join when a player clicks \"I'm Ready!\"");
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    /// <summary>Adds a right-aligned label in the left input column.</summary>
    private void AddLabel(string text, int y)
    {
        Controls.Add(new Label
        {
            Text      = text,
            Location  = new Point(20, y),
            Size      = new Size(125, 28),
            Font      = new Font("Segoe UI", 11),
            ForeColor = Color.LightGray,
            TextAlign = ContentAlignment.MiddleRight
        });
    }

    // ── Start / Stop ──────────────────────────────────────────────────────────

    /// <summary>Toggles the server on or off when the button is clicked.</summary>
    private void OnStartStopClicked(object? sender, EventArgs e)
    {
        if (_server is null)
            StartServer();
        else
            StopEverything();
    }

    /// <summary>
    /// Creates and starts the <see cref="GameServer"/>.
    /// AI bots are spawned automatically by the server when players click "Ready".
    /// </summary>
    private void StartServer()
    {
        int port = (int)_nudPort.Value;

        // Lock the config controls while the server is running
        _nudPort.Enabled = false;
        _cboIp.Enabled   = false;
        _btnStart.Text      = "■  Stop Server";
        _btnStart.BackColor = Color.FromArgb(70, 0, 0);
        _btnStart.ForeColor = Color.OrangeRed;
        _btnStart.FlatAppearance.BorderColor = Color.OrangeRed;

        // Wire the server's log output to our RichTextBox
        _server = new GameServer(port, msg => AppendLog(msg));

        // Run the server on a background task (it blocks on AcceptTcpClientAsync)
        _serverTask = Task.Run(() => _server.StartAsync());

        AppendLog("=== Server started ===");
    }

    /// <summary>Stops the server and re-enables config inputs.</summary>
    private void StopEverything()
    {
        // Stop the server (it also stops all bots it spawned)
        _server?.Stop();
        _server = null;

        // Only update UI if the form is still open (Stop() may be called from FormClosing)
        if (IsHandleCreated && !IsDisposed)
        {
            _nudPort.Enabled = true;
            _cboIp.Enabled   = true;
            _btnStart.Text      = "▶  Start Server";
            _btnStart.BackColor = Color.FromArgb(0, 70, 20);
            _btnStart.ForeColor = Color.LimeGreen;
            _btnStart.FlatAppearance.BorderColor = Color.LimeGreen;
            _lstPlayers.Items.Clear();
        }

        AppendLog("=== Server stopped ===");
    }

    /// <summary>
    /// Appends a timestamped line to the log box, always on the UI thread.
    /// Also updates the player list when it sees join/leave log messages.
    /// </summary>
    private void AppendLog(string message)
    {
        // If called from a background thread, re-invoke on the UI thread
        if (InvokeRequired) { Invoke(() => AppendLog(message)); return; }

        // Append text with a timestamp prefix
        _rtbLog.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}\n");
        _rtbLog.ScrollToCaret();

        // Keep the player list in sync by watching log output patterns
        if (message.StartsWith("  Player joined: "))
        {
            // Log format: "  Player joined: Name (Car X)"
            string namePart = message[17..];
            string pName    = namePart.Contains(" (") ? namePart[..namePart.IndexOf(" (")] : namePart;
            if (!_lstPlayers.Items.Contains(pName))
                _lstPlayers.Items.Add(pName);
        }
        else if (message.StartsWith("[-] Player left: "))
        {
            _lstPlayers.Items.Remove(message[17..]);
        }
        else if (message.StartsWith("  ") && message.Contains(" resigned."))
        {
            // e.g. "  PlayerName resigned."
            string pName = message.Trim().Replace(" resigned.", "");
            _lstPlayers.Items.Remove(pName);
        }
    }
}
