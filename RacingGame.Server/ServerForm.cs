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
public sealed partial class ServerForm : Form
{
    // ── Server state ──────────────────────────────────────────────────────────
    private GameServer? _server;       // null when the server is stopped
    private Task?       _serverTask;   // background task for StartAsync()

    public ServerForm()
    {
        InitializeComponent();

        // Populate IP dropdown with "Any" and every local IPv4 address.
        // This requires a live DNS lookup so it cannot go in InitializeComponent().
        _cboIp.Items.Add("Any (0.0.0.0)");
        foreach (var ip in Dns.GetHostAddresses(Dns.GetHostName())
                               .Where(a => a.AddressFamily == AddressFamily.InterNetwork))
            _cboIp.Items.Add(ip.ToString());
        _cboIp.SelectedIndex = 0;

        // Placeholder log entries so the UI looks active on first launch
        AppendLog("=== Neon Racing 2026 – Server Console ===");
        AppendLog("Press \"Start Server\" to begin accepting players.");
        AppendLog("Up to 5 players (humans + bots) per race.");
        AppendLog("AI bots auto-join when a player clicks \"I'm Ready!\"");
    }

    // ── Start / Stop ──────────────────────────────────────────────────────────

    /// <summary>Draws a neon cyan border around the server window.</summary>
    private void OnFormPaint(object? sender, PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var glow = new Pen(Color.FromArgb(30, Color.Cyan), 10f))
            g.DrawRectangle(glow, 5, 5, Width - 10, Height - 10);
        using (var line = new Pen(Color.FromArgb(120, Color.Cyan), 1.5f))
            g.DrawRectangle(line, 2, 2, Width - 5, Height - 5);
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
