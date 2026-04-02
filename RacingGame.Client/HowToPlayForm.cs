namespace RacingGame.Client;

/// <summary>
/// A simple modal dialog that explains the rules and controls of the game.
/// Shown when the player clicks "How to Play" on the connect screen.
/// </summary>
public sealed class HowToPlayForm : Form
{
    public HowToPlayForm()
    {
        Text            = "How to Play – Racing Game";
        Size            = new Size(520, 540);
        StartPosition   = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox     = false;
        MinimizeBox     = false;
        BackColor       = Color.FromArgb(20, 20, 30);
        ForeColor       = Color.White;

        BuildUI();
    }

    private void BuildUI()
    {
        // ── Title ─────────────────────────────────────────────────────────────
        var lblTitle = new Label
        {
            Text      = "How to Play",
            Font      = new Font("Segoe UI", 18, FontStyle.Bold),
            ForeColor = Color.Gold,
            AutoSize  = true,
            Location  = new Point(20, 15)
        };
        Controls.Add(lblTitle);

        // ── Instructions text ─────────────────────────────────────────────────
        // Using a read-only RichTextBox so text wraps nicely and can be scrolled
        var rtb = new RichTextBox
        {
            Location    = new Point(15, 55),
            Size        = new Size(475, 390),
            BackColor   = Color.FromArgb(30, 30, 45),
            ForeColor   = Color.White,
            Font        = new Font("Segoe UI", 11),
            ReadOnly    = true,
            BorderStyle = BorderStyle.None,
            ScrollBars  = RichTextBoxScrollBars.Vertical,
            Text        = GetInstructions()
        };
        Controls.Add(rtb);

        // ── Close button ──────────────────────────────────────────────────────
        var btnClose = new Button
        {
            Text      = "Got it!",
            Location  = new Point(190, 460),
            Size      = new Size(130, 40),
            Font      = new Font("Segoe UI", 12, FontStyle.Bold),
            BackColor = Color.DodgerBlue,
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Cursor    = Cursors.Hand,
            DialogResult = DialogResult.OK   // pressing this closes the dialog
        };
        btnClose.FlatAppearance.BorderSize = 0;
        Controls.Add(btnClose);

        AcceptButton = btnClose;   // pressing Enter also closes the dialog
    }

    // ── Instructions text ──────────────────────────────────────────────────────

    private static string GetInstructions() =>
        """
        OBJECTIVE
        ─────────
        Be the first player to reach the finish line!
        Your car starts at the left and moves right each time you click MOVE.
        The first car to reach position 100 wins the race.


        CONNECTING
        ──────────
        1. Enter your player name.
        2. Type the server's IP address (ask the host for this).
        3. Set the port number (default: 9000).
        4. Choose your car (Car 1, 2, or 3).
        5. Click "Join Game" to enter the lobby.


        LOBBY & READY SYSTEM
        ────────────────────
        • After joining you will be placed in the waiting room.
        • At least 2 players must be connected before a race can start.
        • Click "I'm Ready!" when you want to race.
        • Once EVERY player in the lobby has clicked Ready, a
          3 – 2 – 1 – Go! countdown begins automatically.


        RACING
        ──────
        • After "Go!" your MOVE button becomes active.
        • Click MOVE as fast as you can to push your car forward.
        • Each click advances your car by 5 units (finish line = 100).
        • Car positions update live for all players.


        RESIGN
        ──────
        • Click RESIGN at any time during a race to give up.
        • The remaining player wins automatically.


        AFTER THE RACE
        ──────────────
        • The winner is announced with a trophy overlay.
        • Click "Play Again" to return to the lobby and race again.
        • Click "Quit" to disconnect and close the game window.


        HIGH SCORES
        ───────────
        • Your wins are saved locally each time you finish first.
        • Click "High Scores" on the connect screen to view the
          all-time leaderboard.


        PING
        ────
        • Your current connection delay to the server (in milliseconds)
          is displayed in the top-right corner of the race window.
        • A lower ping means less delay between your click and the server.


        BOTS  (server-side feature)
        ────
        • The server operator can add AI bots from the server window.
        • Bots join like normal players and move automatically.
        • Useful for testing or for single-player practice mode.
        """;
}
