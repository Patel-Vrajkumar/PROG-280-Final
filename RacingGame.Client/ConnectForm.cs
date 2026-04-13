using System.Drawing.Drawing2D;
using RacingGame.Shared;

namespace RacingGame.Client;

/// <summary>
/// First screen: player enters their name, picks a car, and types the server IP.
/// </summary>
public sealed partial class ConnectForm : Form
{
    private int _selectedCar = 1;

    // ── Public result properties ──────────────────────────────────────────────
    public string PlayerName  { get; private set; } = string.Empty;
    public string ServerHost  { get; private set; } = string.Empty;
    public int    ServerPort  { get; private set; } = 9000;
    public int    CarChoice   { get; private set; } = 1;

    public ConnectForm()
    {
        InitializeComponent();

        // Highlight car 1 as the default selection
        SelectCar(1);
    }

    /// <summary>Draws a pulsating cyan neon border around the connect form.</summary>
    private void OnFormPaint(object? sender, PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var glowPen = new Pen(Color.FromArgb(40, Color.Cyan), 10f))
            g.DrawRectangle(glowPen, 5, 5, Width - 10, Height - 10);
        using (var borderPen = new Pen(Color.FromArgb(160, Color.Cyan), 1.5f))
            g.DrawRectangle(borderPen, 2, 2, Width - 5, Height - 5);
    }

    // ── High scores dialog ────────────────────────────────────────────────────

    /// <summary>
    /// Builds and shows a simple modal dialog that lists the top 10 win counts.
    /// </summary>
    private void ShowHighScores()
    {
        var scores = HighScoreManager.GetTopScores(10);

        // Build the form dynamically
        var f = new Form
        {
            Text            = "High Scores",
            Size            = new Size(360, 420),
            StartPosition   = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox     = false,
            BackColor       = Color.FromArgb(20, 20, 30),
            ForeColor       = Color.White
        };

        f.Controls.Add(new Label
        {
            Text      = "High Scores",
            Font      = new Font("Segoe UI", 16, FontStyle.Bold),
            ForeColor = Color.Gold,
            AutoSize  = true,
            Location  = new Point(20, 15)
        });

        var lv = new ListView
        {
            Location     = new Point(15, 55),
            Size         = new Size(315, 280),
            View         = View.Details,
            FullRowSelect = true,
            BackColor    = Color.FromArgb(30, 30, 45),
            ForeColor    = Color.White,
            Font         = new Font("Segoe UI", 11),
            BorderStyle  = BorderStyle.FixedSingle,
            GridLines    = true
        };
        lv.Columns.Add("#",      40);
        lv.Columns.Add("Player", 180);
        lv.Columns.Add("Wins",   80);

        // Populate rows; show a placeholder if no wins have been recorded yet
        if (scores.Count == 0)
        {
            lv.Items.Add(new ListViewItem(["–", "No scores yet", "–"]));
        }
        else
        {
            for (int i = 0; i < scores.Count; i++)
            {
                var (name, wins) = scores[i];
                lv.Items.Add(new ListViewItem([$"{i + 1}", name, wins.ToString()]));
            }
        }
        f.Controls.Add(lv);

        var btnClose = new Button
        {
            Text      = "Close",
            Location  = new Point(115, 350),
            Size      = new Size(120, 36),
            Font      = new Font("Segoe UI", 11, FontStyle.Bold),
            BackColor = Color.DodgerBlue,
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            DialogResult = DialogResult.OK,
            Cursor    = Cursors.Hand
        };
        btnClose.FlatAppearance.BorderSize = 0;
        f.Controls.Add(btnClose);
        f.AcceptButton = btnClose;

        f.ShowDialog(this);
    }

    private void SelectCar(int car)
    {
        _selectedCar = car;
        _btnCar1.BackColor = car == 1 ? Color.FromArgb(0, 70, 130)  : Color.FromArgb(12, 20, 30);
        _btnCar2.BackColor = car == 2 ? Color.FromArgb(130, 25, 0)  : Color.FromArgb(12, 20, 30);
        _btnCar3.BackColor = car == 3 ? Color.FromArgb(0, 90, 10)   : Color.FromArgb(12, 20, 30);
    }

    private async void OnJoinClicked(object? sender, EventArgs e)
    {
        _lblStatus.Text = string.Empty;

        string name = _txtName.Text.Trim();
        string host = _txtHost.Text.Trim();
        int port    = (int)_nudPort.Value;

        if (string.IsNullOrEmpty(name))  { _lblStatus.Text = "Please enter your name."; return; }
        if (string.IsNullOrEmpty(host))  { _lblStatus.Text = "Please enter the server IP."; return; }

        _btnJoin.Enabled = false;
        _lblStatus.ForeColor = Color.LightSkyBlue;
        _lblStatus.Text  = "Connecting …";

        var net = new NetworkClient();
        try
        {
            await net.ConnectAsync(host, port);
            await net.SendAsync(new GameMessage
            {
                Type       = MessageType.Join,
                PlayerName = name,
                CarChoice  = _selectedCar
            });

            // Hand off to the game form
            PlayerName = name;
            ServerHost = host;
            ServerPort = port;
            CarChoice  = _selectedCar;

            var gameForm = new GameForm(net, name, _selectedCar, host, port);
            gameForm.FormClosed += (_, _) => { _btnJoin.Enabled = true; Show(); };
            Hide();
            gameForm.Show();
        }
        catch (Exception ex)
        {
            net.Dispose();
            _lblStatus.ForeColor = Color.Tomato;
            _lblStatus.Text      = $"Could not connect: {ex.Message}";
            _btnJoin.Enabled     = true;
        }
    }

}
