using RacingGame.Server;

Console.Title = "RacingGame – Server";
Console.WriteLine("===========================================");
Console.WriteLine("  TCP/IP Multiplayer Racing Game – Server ");
Console.WriteLine("===========================================");

int port = 9000;
if (args.Length > 0 && int.TryParse(args[0], out int p))
    port = p;

var server = new GameServer(port);

Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    server.Stop();
};

await server.StartAsync();
