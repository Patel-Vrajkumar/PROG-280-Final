# PROG-280-Final – TCP/IP Multiplayer Racing Game

A multiplayer car-racing game built in **C# .NET 10**.  
Two players to five players connect over TCP/IP, choose a car, and race to the finish line by clicking **MOVE!** as fast as they can.

---

## Solution layout

```
RacingGame.slnx
├── RacingGame.Shared/      – message protocol (shared by server & client)
├── RacingGame.Server/      – .NET 10 console TCP server
└── RacingGame.Client/      – .NET 10 Windows Forms racing client
```

---

## Requirements

| Component | Requirement |
|-----------|-------------|
| SDK | .NET 10 SDK |
| Client OS | Windows (Windows Forms) |
| Server OS | Windows / Linux / macOS (.NET 10 console) |

---

## How to run

### 1 – Start the server

```bash
# Default port 9000
dotnet run --project RacingGame.Server

# Custom port
dotnet run --project RacingGame.Server -- 12345
```

The server prints the IP address(es) other players should use to connect.

### 2 – Start one client per player (up to 5)

```bash
dotnet run --project RacingGame.Client
```

*(On Windows you can also double-click `RacingGame.Client.exe` after building.)*

### 3 – Connect screen

Each player fills in:
| Field | Default | Description |
|-------|---------|-------------|
| Your Name | — | Display name during the race |
| Server IP | 127.0.0.1 | IP address printed by the server |
| Port | 9000 | Must match the server port |
| Car | Car 1 | Choose from 3 cars (blue / red / green) |

Click **Join Game**.

### 4 – Race!

* Once **2 or more players** have joined, the server automatically starts the race.
* Click **🚀 MOVE!** (or press it rapidly) to advance your car.
* Every 5 units of progress moves your car forward on the track.
* The **first player to reach position 100** (the finish line) wins.
* The winner's name is displayed to all players; the game then stops.

---

## Playing from another computer on the same network

1. On the server machine run `ipconfig` (Windows) or `ip addr` (Linux) to find its LAN IP, e.g. `192.168.1.42`.
2. Make sure port **9000** (or your chosen port) is allowed through the firewall.
3. On the client machine enter that IP in the **Server IP** field.

---

## Build

```bash
dotnet build RacingGame.slnx
```

---

## Game rules summary

* Maximum **5 players** per game.
* A player cannot join while a game is in progress.
* If a player disconnects mid-race and only one remains, that player wins by default.
* Player names must be unique – the server appends a number if needed.
