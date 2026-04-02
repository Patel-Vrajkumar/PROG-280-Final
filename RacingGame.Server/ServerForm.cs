using System.Net;
using System.Net.Sockets;

namespace RacingGame.Server;

/// <summary>
/// A Windows Forms window that lets the server operator configure the IP
/// binding and port, then start/stop the server.  Log messages from the server
/// appear in a scrolling text area and the connected player list updates live.
/// </summary>
public sealed class ServerForm : Form
{
    // ── Controls ──────────────────────────────────────────────────────────────
    private readonly ComboBox    _cboIp      = new();   // drop-down of local IP addresses
    private readonly NumericUpDown _nudPort  = new();   // port number input
    private readonly Button      _btnStart   = new();   // Start / Stop server toggle
    private readonly ListBox     _lstPlayers = new();   // live list of connected players
    private readonly RichTextBox _rtbLog     = new();   // scrolling log output

    // ── Server ────────────────────────────────────────────────────────────────
    private GameServer? _server;            // null when server is stopped
    private Task?       _serverTask;        // background task running StartAsync()
    private readonly List<string> _players = [];  // names of currently connected players

    public ServerForm()
    {
        Text            = "Racing Game – Server";
        Size            = new Size(620, 580);
        StartPosition   = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox     = false;
        BackColor       = Color.FromArgb(20, 20, 30);
        ForeColor       = Color.White;

        BuildUI();

        // Stop the server gracefully when the form is closed
        FormClosing += (_, _) => _server?.Stop();
    }

    // ── UI construction ───────────────────────────────────────────────────────

    private void BuildUI()
    {
        int y = 20;

        // ── Title ─────────────────────────────────────────────────────────────
        var title = new Label
        {
            Text      = "Racing Game – Server",
            Font      = new Font("Segoe UI", 18, FontStyle.Bold),
            ForeColor = Color.Gold,
            AutoSize  = true,
            Location  = new Point(20, y)
        };
        Controls.Add(title);
        y += 50;

        // ── IP binding drop-down ───────────────────────────────────────────────
        AddLabel("Bind IP:", y);
        _cboIp.Location      = new Point(140, y - 2);
        _cboIp.Size          = new Size(200, 28);
        _cboIp.DropDownStyle = ComboBoxStyle.DropDownList;
        _cboIp.Font          = new Font("Segoe UI", 11);
        _cboIp.BackColor     = Color.FromArgb(40, 40, 55);
        _cboIp.ForeColor     = Color.White;
        // Populate with "Any (0.0.0.0)" plus every local IPv4 address
        _cboIp.Items.Add("Any (0.0.0.0)");
        foreach (var ip in Dns.GetHostAddresses(Dns.GetHostName())
                               .Where(a => a.AddressFamily == AddressFamily.InterNetwork))
            _cboIp.Items.Add(ip.ToString());
        _cboIp.SelectedIndex = 0;   // default to "Any"
        Controls.Add(_cboIp);
        y += 42;

        // ── Port input ────────────────────────────────────────────────────────
        AddLabel("Port:", y);
        _nudPort.Location  = new Point(140, y - 2);
        _nudPort.Size      = new Size(110, 28);
        _nudPort.Minimum   = 1;
        _nudPort.Maximum   = 65535;
        _nudPort.Value     = 9000;      // default port
        _nudPort.Font      = new Font("Segoe UI", 11);
        _nudPort.BackColor = Color.FromArgb(40, 40, 55);
        _nudPort.ForeColor = Color.White;
        Controls.Add(_nudPort);
        y += 50;

        // ── Start / Stop button ───────────────────────────────────────────────
        _btnStart.Text      = "Start Server";
        _btnStart.Location  = new Point(140, y);
        _btnStart.Size      = new Size(160, 42);
        _btnStart.Font      = new Font("Segoe UI", 13, FontStyle.Bold);
        _btnStart.BackColor = Color.ForestGreen;
        _btnStart.ForeColor = Color.White;
        _btnStart.FlatStyle = FlatStyle.Flat;
        _btnStart.FlatAppearance.BorderSize = 0;
        _btnStart.Cursor    = Cursors.Hand;
        _btnStart.Click    += OnStartStopClicked;
        Controls.Add(_btnStart);
        y += 60;

        // ── Connected players list ────────────────────────────────────────────
        var lblPlayers = new Label
        {
            Text      = "Connected Players:",
            Location  = new Point(20, y),
            Size      = new Size(200, 24),
            Font      = new Font("Segoe UI", 10, FontStyle.Bold),
            ForeColor = Color.LightGray
        };
        Controls.Add(lblPlayers);
        y += 26;

        _lstPlayers.Location  = new Point(20, y);
        _lstPlayers.Size      = new Size(200, 140);
        _lstPlayers.Font      = new Font("Segoe UI", 10);
        _lstPlayers.BackColor = Color.FromArgb(30, 30, 45);
        _lstPlayers.ForeColor = Color.White;
        _lstPlayers.BorderStyle = BorderStyle.FixedSingle;
        Controls.Add(_lstPlayers);

        // ── Server log area ───────────────────────────────────────────────────
        var lblLog = new Label
        {
            Text      = "Server Log:",
            Location  = new Point(240, y - 26),
            Size      = new Size(150, 24),
            Font      = new Font("Segoe UI", 10, FontStyle.Bold),
            ForeColor = Color.LightGray
        };
        Controls.Add(lblLog);

        _rtbLog.Location   = new Point(240, y);
        _rtbLog.Size       = new Size(355, 280);
        _rtbLog.Font       = new Font("Consolas", 9);
        _rtbLog.BackColor  = Color.FromArgb(10, 10, 18);
        _rtbLog.ForeColor  = Color.LightGreen;
        _rtbLog.ReadOnly   = true;
        _rtbLog.ScrollBars = RichTextBoxScrollBars.Vertical;
        _rtbLog.BorderStyle = BorderStyle.FixedSingle;
        Controls.Add(_rtbLog);
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    /// <summary>Adds a right-aligned label to the left column.</summary>
    private void AddLabel(string text, int y)
    {
        Controls.Add(new Label
        {
            Text      = text,
            Location  = new Point(20, y),
            Size      = new Size(115, 28),
            Font      = new Font("Segoe UI", 11),
            ForeColor = Color.LightGray,
            TextAlign = ContentAlignment.MiddleRight
        });
    }

    // ── Start / Stop logic ────────────────────────────────────────────────────

    /// <summary>
    /// Toggles the server on and off when the button is clicked.
    /// </summary>
    private void OnStartStopClicked(object? sender, EventArgs e)
    {
        if (_server is null)
            StartServer();
        else
            StopServer();
    }

    /// <summary>Creates a <see cref="GameServer"/> and runs it on a background task.</summary>
    private void StartServer()
    {
        int port = (int)_nudPort.Value;

        // Lock the config inputs while the server is running
        _nudPort.Enabled = false;
        _cboIp.Enabled   = false;
        _btnStart.Text      = "Stop Server";
        _btnStart.BackColor = Color.Firebrick;

        // Wire the server's logger to our log box (marshal to UI thread)
        _server = new GameServer(port, msg => AppendLog(msg));

        // Run the server on a background task
        _serverTask = Task.Run(() => _server.StartAsync());

        AppendLog("=== Server started ===");
    }

    /// <summary>Stops the running server and re-enables the config inputs.</summary>
    private void StopServer()
    {
        _server?.Stop();
        _server = null;

        _nudPort.Enabled = true;
        _cboIp.Enabled   = true;
        _btnStart.Text      = "Start Server";
        _btnStart.BackColor = Color.ForestGreen;

        AppendLog("=== Server stopped ===");
        _lstPlayers.Items.Clear();
    }

    /// <summary>
    /// Appends a line to the log box, marshalling to the UI thread if needed.
    /// Also watches for player-join / player-left patterns to update the player list.
    /// </summary>
    private void AppendLog(string message)
    {
        // Marshal to the UI thread if called from the background server task
        if (InvokeRequired) { Invoke(() => AppendLog(message)); return; }

        // Append to log with timestamp
        _rtbLog.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}\n");
        _rtbLog.ScrollToCaret();

        // Parse simple join/leave messages to keep the player list current
        if (message.StartsWith("  Player joined: "))
        {
            // Format: "  Player joined: Name (Car X)"
            string namePart = message[17..];
            string pName    = namePart.Contains(" (") ? namePart[..namePart.IndexOf(" (")] : namePart;
            if (!_lstPlayers.Items.Contains(pName))
                _lstPlayers.Items.Add(pName);
        }
        else if (message.StartsWith("[-] Player left: "))
        {
            string pName = message[17..];
            _lstPlayers.Items.Remove(pName);
        }
    }
}
