# RDS Results

RDS Results is a Windows app for race day. It keeps a live board of finish results from RaceDay Scoring, follows announcer bib reads, and sends commands out to a jumbotron.

The window is meant to stay on one screen. Division reports sit side by side. As more reports are open, each card drops columns. Bib, name, and finish time always stay.

## What you see

The header is the race at a glance: name and event id, dates, how many reports are detected, distance, the last poll and the next one, and whether RaceDay Scoring is connected.

**Results** is the board.

- Destination cards show where results can go. The jumbotron card is live: it reports whether RDS Results is connected out to the jumbotron. iResultsLIVE, the on-site kiosk, and print stay marked “Not set up” until those outputs exist.
- Each available report is a card. Place, gender, age, and pace appear when the card is wide enough.
- The bottom row is a narrow camera preview, a wide system activity log, and a right pane with event counts plus the current race time. Set a camera IP in Settings. **Clear** empties the activity log.

**Bib reads** lists announcer reads for the timing locations you choose.

**Settings** holds the RaceDay connection, theme, bib listener, jumbotron mode, camera, and display sending.

The camera URL is built from the camera IP and a template (`rtsp://{ip}:554/stream1` by default). **Advanced** can change that template or set an exact RTSP URL.

If RaceDay Scoring refuses the connection, a banner across the top asks you to start the **RaceDay Hub** in RaceDay Scoring. That refusal means the hub is not enabled. The banner goes away on the next successful refresh.

## RaceDay Scoring

RDS Results talks to the RaceDay Scoring HTTP connector. Reports show up after they are made available via the kiosk. While automatic refresh is on, the app downloads those reports on a timer (15 seconds by default, from 2 to 3600).

Default connection values:

| Setting | Default |
| --- | --- |
| Host | `localhost` |
| Port | `8080` |
| Password | `12345` |

The connector password is the HTTP password. The bib listener uses a separate hub password.

Settings are saved in `%LOCALAPPDATA%\RDS_Results\settings.json`.

## Bib reads and displays

The bib listener follows the announcer socket (default port **9002**). On the Bib reads view, check the timing locations you want to follow.

When sending is on, RDS Results connects out to the jumbotron and pushes commands as JSON lines. The jumbotron only listens. Set the jumbotron host and port under Settings, Jumbotron. The default port is **9100**. [SendBibReads.md](SendBibReads.md) is the version 1 contract.

The jumbotron mode is set in RDS Results. The jumbotron switches as soon as the mode changes.

| Mode | What the jumbotron shows |
| --- | --- |
| Welcome | The app splash screen. |
| Event welcome | A custom graphic you choose, plus the event name, start, and how to view results. Put sponsor art and a QR code on that graphic. |
| Bib reads | Walk-up results as announcer reads arrive. |
| Scrolling results | Each results set, men then women. |
| Selfies, NovusVox | Placeholders until those programs are wired. |

RDS Display, in this same solution, is the test client. A jumbotron controller implements the same contract.

## Projects

| Project | What it is |
| --- | --- |
| `RDS_Results` | The race-day board. WPF on .NET 8 (`net8.0-windows`). |
| `RDS_Display` | A test jumbotron that listens for commands from RDS Results. |

Open `RDS_Results.sln` in Visual Studio and run **RDS_Results**. From a terminal:

```powershell
dotnet run --project RDS_Results\RDS_Results.csproj
```
