using RacingGame.Shared;

namespace RacingGame.Client;

/// <summary>
/// First screen: player enters their name, picks a car, and types the server IP.
/// </summary>
public sealed class ConnectForm : Form
{
    // ── Controls ─────────────────────────────────────────────────────────────
    private readonly TextBox _txtName   = new();
    private readonly TextBox _txtHost   = new();
    private readonly NumericUpDown _nudPort = new();
    private readonly Panel _carPanel   = new();
    private readonly Button _btnCar1   = new();
    private readonly Button _btnCar2   = new();
    private readonly Button _btnCar3   = new();
    private readonly Button _btnJoin   = new();
    private readonly Label  _lblStatus = new();

    private int _selectedCar = 1;

    // ── Public result properties ──────────────────────────────────────────────
    public string PlayerName  { get; private set; } = string.Empty;
    public string ServerHost  { get; private set; } = string.Empty;
    public int    ServerPort  { get; private set; } = 9000;
    public int    CarChoice   { get; private set; } = 1;

    public ConnectForm()
    {
        Text            = "Racing Game – Connect";
        Size            = new Size(520, 560);
        StartPosition   = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox     = false;
        BackColor       = Color.FromArgb(20, 20, 30);
        ForeColor       = Color.White;

        BuildUI();
    }

    private void BuildUI()
    {
        int y = 30;

        // Title
        var title = new Label
        {
            Text      = "🏎  RACING GAME",
            Font      = new Font("Segoe UI", 22, FontStyle.Bold),
            ForeColor = Color.Gold,
            AutoSize  = true,
            Location  = new Point(110, y)
        };
        Controls.Add(title);
        y += 60;

        // Name
        Controls.Add(MakeLabel("Your Name:", y));
        _txtName.Location    = new Point(170, y - 2);
        _txtName.Size        = new Size(220, 28);
        _txtName.Font        = new Font("Segoe UI", 11);
        _txtName.BackColor   = Color.FromArgb(40, 40, 55);
        _txtName.ForeColor   = Color.White;
        _txtName.BorderStyle = BorderStyle.FixedSingle;
        Controls.Add(_txtName);
        y += 45;

        // Server IP
        Controls.Add(MakeLabel("Server IP:", y));
        _txtHost.Location    = new Point(170, y - 2);
        _txtHost.Size        = new Size(160, 28);
        _txtHost.Text        = "127.0.0.1";
        _txtHost.Font        = new Font("Segoe UI", 11);
        _txtHost.BackColor   = Color.FromArgb(40, 40, 55);
        _txtHost.ForeColor   = Color.White;
        _txtHost.BorderStyle = BorderStyle.FixedSingle;
        Controls.Add(_txtHost);
        y += 45;

        // Port
        Controls.Add(MakeLabel("Port:", y));
        _nudPort.Location  = new Point(170, y - 2);
        _nudPort.Size      = new Size(90, 28);
        _nudPort.Minimum   = 1;
        _nudPort.Maximum   = 65535;
        _nudPort.Value     = 9000;
        _nudPort.Font      = new Font("Segoe UI", 11);
        _nudPort.BackColor = Color.FromArgb(40, 40, 55);
        _nudPort.ForeColor = Color.White;
        Controls.Add(_nudPort);
        y += 50;

        // Car selection label
        Controls.Add(MakeLabel("Choose Car:", y));
        y += 30;

        // Car buttons panel
        _carPanel.Location  = new Point(60, y);
        _carPanel.Size      = new Size(390, 100);
        _carPanel.BackColor = Color.Transparent;

        StyleCarButton(_btnCar1, "🚗  Car 1", Color.DeepSkyBlue,   0);
        StyleCarButton(_btnCar2, "🏎  Car 2", Color.OrangeRed,     130);
        StyleCarButton(_btnCar3, "🚙  Car 3", Color.LimeGreen,     260);

        _btnCar1.Click += (_, _) => SelectCar(1);
        _btnCar2.Click += (_, _) => SelectCar(2);
        _btnCar3.Click += (_, _) => SelectCar(3);

        _carPanel.Controls.AddRange([_btnCar1, _btnCar2, _btnCar3]);
        Controls.Add(_carPanel);
        y += 115;

        // Join button
        _btnJoin.Text      = "Join Game";
        _btnJoin.Location  = new Point(170, y);
        _btnJoin.Size      = new Size(160, 42);
        _btnJoin.Font      = new Font("Segoe UI", 13, FontStyle.Bold);
        _btnJoin.BackColor = Color.DodgerBlue;
        _btnJoin.ForeColor = Color.White;
        _btnJoin.FlatStyle = FlatStyle.Flat;
        _btnJoin.FlatAppearance.BorderSize = 0;
        _btnJoin.Cursor    = Cursors.Hand;
        _btnJoin.Click    += OnJoinClicked;
        Controls.Add(_btnJoin);
        y += 52;

        // Status
        _lblStatus.Location  = new Point(30, y);
        _lblStatus.Size      = new Size(450, 30);
        _lblStatus.Font      = new Font("Segoe UI", 10);
        _lblStatus.ForeColor = Color.Tomato;
        _lblStatus.TextAlign = ContentAlignment.MiddleCenter;
        Controls.Add(_lblStatus);
        y += 38;

        // ── How to Play button ────────────────────────────────────────────────
        var btnHelp = new Button
        {
            Text      = "How to Play",
            Location  = new Point(80, y),
            Size      = new Size(140, 34),
            Font      = new Font("Segoe UI", 10),
            BackColor = Color.FromArgb(40, 60, 80),
            ForeColor = Color.LightCyan,
            FlatStyle = FlatStyle.Flat,
            Cursor    = Cursors.Hand
        };
        btnHelp.FlatAppearance.BorderSize = 1;
        btnHelp.FlatAppearance.BorderColor = Color.SteelBlue;
        // Open the HowToPlayForm dialog when clicked
        btnHelp.Click += (_, _) => new HowToPlayForm().ShowDialog(this);
        Controls.Add(btnHelp);

        // ── High Scores button ────────────────────────────────────────────────
        var btnScores = new Button
        {
            Text      = "High Scores",
            Location  = new Point(290, y),
            Size      = new Size(140, 34),
            Font      = new Font("Segoe UI", 10),
            BackColor = Color.FromArgb(60, 50, 20),
            ForeColor = Color.Gold,
            FlatStyle = FlatStyle.Flat,
            Cursor    = Cursors.Hand
        };
        btnScores.FlatAppearance.BorderSize  = 1;
        btnScores.FlatAppearance.BorderColor = Color.Goldenrod;
        // Show the high-scores leaderboard dialog when clicked
        btnScores.Click += (_, _) => ShowHighScores();
        Controls.Add(btnScores);

        // Highlight car 1 as default selection
        SelectCar(1);
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

    private static void StyleCarButton(Button btn, string text, Color accent, int x)
    {
        btn.Text      = text;
        btn.Location  = new Point(x, 10);
        btn.Size      = new Size(115, 70);
        btn.Font      = new Font("Segoe UI", 10, FontStyle.Bold);
        btn.BackColor = Color.FromArgb(40, 40, 55);
        btn.ForeColor = accent;
        btn.FlatStyle = FlatStyle.Flat;
        btn.FlatAppearance.BorderColor = accent;
        btn.FlatAppearance.BorderSize  = 2;
        btn.Cursor    = Cursors.Hand;
    }

    private void SelectCar(int car)
    {
        _selectedCar = car;
        _btnCar1.BackColor = car == 1 ? Color.FromArgb(0, 80, 160) : Color.FromArgb(40, 40, 55);
        _btnCar2.BackColor = car == 2 ? Color.FromArgb(140, 30, 0) : Color.FromArgb(40, 40, 55);
        _btnCar3.BackColor = car == 3 ? Color.FromArgb(0, 100, 0)  : Color.FromArgb(40, 40, 55);
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
            gameForm.FormClosed += (_, _) => Show();
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

    // ── Helper ────────────────────────────────────────────────────────────────
    private Label MakeLabel(string text, int y) => new()
    {
        Text      = text,
        Location  = new Point(30, y),
        Size      = new Size(140, 28),
        Font      = new Font("Segoe UI", 11),
        ForeColor = Color.LightGray,
        TextAlign = ContentAlignment.MiddleRight
    };
}
