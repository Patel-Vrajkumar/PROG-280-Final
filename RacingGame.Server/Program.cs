using RacingGame.Server;

// Enable visual styles so buttons and controls look polished
Application.EnableVisualStyles();
Application.SetCompatibleTextRenderingDefault(false);

// Show the server configuration window (blocks until the window is closed)
Application.Run(new ServerForm());
