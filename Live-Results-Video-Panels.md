October 22, 2026

# Live Results on a Video Panel, Straight from RaceDay Scoring

### RDS Results connects the RaceDay Scoring computer you already run to a NovusPulse video panel.

A finish-line video panel should show the race you are already scoring. It should not be a second project you build on Saturday morning. RDS Results is a Windows app that reads RaceDay Scoring on your race network and drives a NovusPulse video panel. Publish the reports you already use, point RDS Results at the panel, and the screen follows the race.

The same setup works for a single 5K or a weekend of events. RaceDay Scoring stays the system of record. The panel is a display. RDS Results is the short path between them.

## Three pieces on one race network

Live results on a NovusPulse panel take the tools you bring to the finish line and one app in the middle.

- **RaceDay Scoring** scores the race, publishes your kiosk reports, and produces announcer reads.
- **RDS Results** reads those reports and reads on the local network and chooses what the screen shows.
- **NovusPulse** is the video panel. It listens. RDS Results connects out to it and keeps the picture current.

The panel does not need its own internet connection, its own clock, or a custom data feed built for that event. Clock, weather, start time, and results all come from RDS Results. If the venue loses the outside internet, the board keeps running, because RaceDay Scoring and RDS Results are already on the timing computers.

## What the panel shows

You pick the mode in RDS Results. The NovusPulse panel switches as soon as the mode changes. You can set a default for the day, and you can change it for the session from the results screen.

### Event welcome

Before the start, your graphic fills the screen. Put sponsor art and a QR code on that graphic. RDS Results places the race start time and a countdown in the two standard boxes on the graphic, and it can show the event name and where to find results.

### Bib reads

As announcer reads arrive from the timing locations you choose, the panel shows the athlete: bib, name, and, when the result is in, place and finish time. The first look can be the name alone. When the finisher matches a results row, that same spot on the screen fills in the time and place.

### Scrolling results

Each results set walks the screen, men and then women. Place, bib, name, and finish time stay on the board. A later refresh replaces the standings when they change.

### Welcome

While you are still setting up, the panel can hold a simple splash with the race name.

The same connection also carries the race-day clock and the weather for the town you enter, so the panel is not looking those up on its own.

## Set it up once

1. In RaceDay Scoring, start the **RaceDay Hub** and make your results reports available on the kiosk. RDS Results shows those same reports.
2. Open RDS Results on the scoring computer, or on another computer on the race network. Enter the RaceDay Scoring host, port, and connector password. Leave **Automatic** refresh on. The board updates on its own, every 15 seconds by default.
3. Turn on the bib listener and, on the Bib reads view, check the timing locations you want the panel to follow.
4. Under Jumbotron, enter the NovusPulse panel address, turn on **Send to the jumbotron**, and pick the mode. Set the race start time. If you want a branded open, browse to your event graphic.

That is the integration. There is no display project to write for each race, and there is no second set of results to type in.

If RaceDay Scoring refuses the connection, start the RaceDay Hub. The banner across the top of RDS Results clears on the next successful refresh. If the link to the panel drops, RDS Results connects again and the screen catches up.

## Why it stays simple on race day

- **One control.** Change the mode in RDS Results and the NovusPulse panel follows. Welcome while you set up, Event welcome before the gun, Bib reads as people finish, Scrolling results when the crowd wants the standings.
- **Data you already produce.** Kiosk reports supply the standings. Announcer reads supply the walk-up names. You do not build a separate results file for the screen.
- **A local link.** RDS Results talks to RaceDay Scoring on the race network and connects out to the panel. The show does not depend on a cloud round trip.
- **A panel that only displays.** The screen does not score, match bibs, or fetch weather. RDS Results does that and sends the finished picture.

## Bring the race up on the big screen

A finisher looks up and sees their name. A spectator watches the standings scroll. Before the gun, the same panel holds your event graphic, the start time, and a countdown.

Score the race in RaceDay Scoring. Drive the NovusPulse video panel with RDS Results. The live board is a connection and a mode, not a custom build.

*[Featured image: a NovusPulse panel at a finish line showing a finisher’s name, bib, and time, with RDS Results open beside RaceDay Scoring.]*

*[Inline: Event welcome graphic with the start time and countdown in the two standard boxes.]*

*[Inline: Bib reads on the panel as announcer reads arrive, then the same athlete updated with place and finish time.]*

*[Inline: Scrolling results, one event at a time, men then women.]*
