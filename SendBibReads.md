# SendBibReads

Version 1 contract between RDS Results and a jumbotron.

RDS Results is the master. It connects out to the jumbotron and pushes commands as JSON lines. The jumbotron is a slave: it listens, accepts the socket, and applies what it is sent. RDS Display is the test slave. A jumbotron controller implements this same contract.

## Connection

- TCP.
- The jumbotron listens on all network interfaces.
- Default port: **9100**. Set the jumbotron host and port in RDS Results under Settings, Jumbotron. The jumbotron listens on that port.
- RDS Results connects out to the jumbotron. On the same computer, use `127.0.0.1`.
- No authentication in version 1. Use this on the race network.
- UTF-8. No byte-order mark.
- One JSON object per line. The line ends with `\n`. A `\r` before the `\n` is allowed. JSON strings do not contain raw line breaks.
- Field order does not matter. Unknown fields are ignored. Unknown `ty` values are ignored.
- The jumbotron does not send commands. RDS Results ignores lines sent by the jumbotron in version 1.

If the socket drops, RDS Results connects again. Every connection starts over with `hello`.

## Session

1. The jumbotron accepts the socket from RDS Results.
2. RDS Results sends `hello`. `hello` includes the current jumbotron `mode`.
3. RDS Results sends `welcome`, then `eventWelcome`, then `board`, when it has those payloads.
4. RDS Results sends the reads it still has, oldest first, at most 100. Each of those is a `read` message with `"update": false` and the latest values for that read.
5. RDS Results sends each new read as it arrives.
6. When a later results refresh fills in finish data for a read already sent, RDS Results sends that read again with `"update": true` and the same `id`.
7. When race data or the event graphic changes, RDS Results sends a fresh `welcome`, `eventWelcome`, or `board`.
8. When the operator changes the jumbotron mode in RDS Results, RDS Results sends `mode`. If the new mode is `eventWelcome`, `welcome`, or `scrollingResults`, that `mode` line is followed by the current `eventWelcome`, `welcome`, or `board` payload so the display does not have to keep an earlier copy.
9. RDS Results sends `ping` as soon as the session is replayed, then about every 10 seconds. Each ping carries the host clock, race start time, and current weather so the jumbotron does not fetch those itself.

`hello` means the previous session is stale. Clear reads and standings, then apply the messages that follow.

## Messages

Every message has `v` and `ty`. Version 1 uses `"v": 1`.

### hello

```json
{"v":1,"ty":"hello","app":"RDS_Results","mode":"bibReads"}
```

| Field | Meaning |
| --- | --- |
| app | Name of the sending program. |
| mode | Current jumbotron mode. See [Modes](#modes). Older servers may omit this field. Treat a missing value as `bibReads`. |

### mode

```json
{"v":1,"ty":"mode","mode":"welcome"}
```

| Field | Meaning |
| --- | --- |
| mode | Jumbotron mode chosen in RDS Results. The display switches to that program. |

Unknown `mode` values are kept and shown. A later RDS Results build may add modes such as sponsor messages.

### welcome

```json
{"v":1,"ty":"welcome","title":"Spring Classic","detail":"Apr 12, 2026  ·  10K"}
```

| Field | Meaning |
| --- | --- |
| title | Race name for the splash screen. |
| detail | Short line under the title, usually the date and distance. |

The display shows the RDS splash graphic and this text. Welcome mode does not send a custom image.

### eventWelcome

```json
{"v":1,"ty":"eventWelcome","title":"Spring Classic","start":"April 12, 2026  7:30 AM","events":"10K   ·   5K","hint":"Live results at the URL below.","resultsUrl":"https://example.com/results","imageMime":"image/jpeg","image":"<base64>"}
```

| Field | Meaning |
| --- | --- |
| title | Event name. |
| start | Start date and time when RaceDay sent one. |
| events | Event list for this race. |
| hint | How to view results. May be empty. |
| resultsUrl | Results link shown on the screen. A QR code can be part of the custom graphic. |
| imageMime | MIME type of `image`. Empty when no graphic was chosen. |
| image | JPEG, PNG, or similar, base64, no line breaks. Empty when no graphic was chosen. RDS Results resizes wide graphics before sending. |

The custom graphic is the background. Sponsor art and a QR code belong on that graphic.

Event welcome graphics are 16:9 and keep two empty boxes in a fixed place. The display overlays only the values, using the race start from `ping`. It does not draw the labels.

| Overlay | Graphic box | Position on the 16:9 graphic |
| --- | --- | --- |
| Race start time | RACE STARTS AT | left 40.8%–70.4%, top 61%–79% |
| Countdown | TIME UNTIL START | left 70.4%–100%, top 61%–79% |

### board

```json
{"v":1,"ty":"board","pages":[{"title":"10K Overall Men","columns":[{"key":"overall_place","label":"Place","role":"place"},{"key":"bib","label":"Bib","role":"bib"},{"key":"full_name","label":"Name","role":"name"},{"key":"net_time","label":"Net","role":"net"},{"key":"pace","label":"Pace","role":"pace"}],"rows":[{"cells":["1","101","Ada Lovelace","0:48:10","7:45"],"place":"1","bib":"101","name":"Ada Lovelace","finishTime":"0:48:10","pace":"7:45","city":""}]}]}
```

| Field | Meaning |
| --- | --- |
| pages | Result sets in show order. Each report is its own set, split by event, men before women. |
| pages[].title | Page title, usually the report name plus Men or Women. |
| pages[].columns | Fields to draw, left to right. Place and name are always included. The other columns are the ones chosen for that report. |
| columns[].key | Stable id from the report column. |
| columns[].label | Header text, from the report when it has one. |
| columns[].role | `place`, `name`, `bib`, `chip`, `city`, `state`, `event`, `clock`, `net`, `pace`, `division`, `agePlace`, `gender`, `age`, `ageGroup`, or `other`. Use it to size the column. |
| pages[].rows | Finishers on that page, overall-place order. |
| rows[].cells | Values in the same order as `columns`. |

`place`, `bib`, `name`, `finishTime`, `pace`, and `city` are still sent on each row. Draw `columns` and `cells`. The named fields stay so an older screen can show place, bib, name, finish time, pace, and city when those columns are selected. `finishTime` is the net time when that column is selected, otherwise the clock time.

The display scrolls one page, then advances. A later poll replaces `board` when the standings or the chosen columns changed.

### read

```json
{"v":1,"ty":"read","id":1,"update":false,"readTime":"14:02:11","location":"Finish","bib":"101","chip":"101","name":"Ada Lovelace","city":"London","state":"","event":"10K","place":"12","finishTime":"0:48:10","clockTime":"0:48:12","pace":"7:45","division":"F30-39","divisionPlace":"3","ageGroup":"F30-39","gender":"F","age":"32","team":"","report":"Expert Report","message":""}
```

| Field | Meaning |
| --- | --- |
| id | Integer identity of this read. Unique while RDS Results is running. Starts at 1 and is not reused. |
| update | `false` for the first time this `id` is sent on this connection, including the opening replay. `true` when a read already sent has newer result fields. |
| readTime | Time of the bib read from RaceDay, usually `HH:mm:ss` in the race time zone. |
| location | Timing location name that produced the read. |
| bib | Bib number. May be empty when RaceDay did not send one. |
| chip | Chip number. May be empty. |
| name | Athlete name. |
| city | City. |
| state | State or region. |
| event | Event name. |
| place | Overall place from the matched results row. |
| finishTime | Chip time when the results row has one, otherwise clock time. Empty until the read is matched to a results row. |
| clockTime | Gun or clock time from the matched results row. |
| pace | Pace from the matched results row. |
| division | Division or age-group label from the matched results row. |
| divisionPlace | Place within that division. |
| ageGroup | Age group from the read or the results row. |
| gender | Gender. |
| age | Age. |
| team | Team name from the read. |
| report | Name of the results report that matched this read. `Not in results` when no row matched. |
| message | Announcer message from the read, when RaceDay sent one. |

String fields are present and may be `""`. A client treats a missing string as `""`.

When `update` is `true`, replace the row with the same `id`. When the client has never seen that `id`, show it as a new read. New reads are displayed newest first. An update does not move the read ahead of newer reads.

`finishTime` often arrives in a second message. The first `read` can have an empty finish time. A later `read` with the same `id` and `"update": true` carries the matched time and place.

### ping

```json
{"v":1,"ty":"ping","time":"9:25 AM","date":"Thu Oct 22","h":9,"m":25,"s":12,"race":{"start":"7:30 AM","h":7,"m":30,"s":0,"y":2026,"mo":10,"d":22},"wx":{"loc":"Londonderry, NH","temp":"58°F","cond":"Partly cloudy"}}
```

| Field | Meaning |
| --- | --- |
| time | Host local time, 12-hour clock with AM/PM. Ready to show as-is. |
| date | Host local date, for example `Thu Oct 22`. |
| h | Host local hour, 0–23. |
| m | Host local minute, 0–59. |
| s | Host local second, 0–59. |
| race | Race start from RDS Results. Omitted until a start time is set. |
| race.start | Start time ready to show as-is, for example `7:30 AM`. |
| race.h | Start hour, 0–23. |
| race.m | Start minute, 0–59. |
| race.s | Start second, 0–59. |
| race.y | Start year. |
| race.mo | Start month, 1–12. |
| race.d | Start day of month. |
| wx | Current weather from RDS Results. Omitted until the first weather fetch succeeds. |
| wx.loc | Place name. |
| wx.temp | Temperature, including the unit. |
| wx.cond | Short condition, for example `Partly cloudy`. |

Use `time` and `date` on screen. Use `h`, `m`, and `s` to keep the clock ticking between pings. Do not use the jumbotron’s own clock or timezone. Do not fetch weather from the jumbotron.

On Event welcome, show `race.start` in the left box and count down from `race` to the host clock in the right box. After the start, show `0:00:00`. Older servers may send an empty ping; treat missing fields as “not yet available.”

## Matching behavior the display can rely on

RDS Results matches a read to a local results row by bib, then chip. The matched place, finish time, clock time, pace, division, and report name are included on the `read` message. Until that match exists, those strings are empty and `report` is `Not in results`.

The location on the message is the location of that read. A jumbotron can choose which read to show from `location`.

## Modes

RDS Results sets the jumbotron mode. Connected displays follow it.

| `mode` | What the display shows |
| --- | --- |
| `welcome` | App splash screen, with the race name when known. |
| `eventWelcome` | Custom event graphic plus start info and how to view results. |
| `bibReads` | Walk-up results as announcer reads arrive. This is the default. |
| `selfies` | Selfie station. |
| `scrollingResults` | Standings by results set, men then women. |
| `novusVox` | NovusVox program. |

`read` messages still arrive in every mode so a switch back to `bibReads` has history. The display only presents walk-up results while the mode is `bibReads`.

## What version 1 does not do

The server does not wait for a reply. Keep accepting version 1 messages, and ignore names and message types this document does not define.
