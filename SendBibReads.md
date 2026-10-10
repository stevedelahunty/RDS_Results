# SendBibReads

Version 1 contract between RDS Results and a display client.

RDS Results listens for display clients and pushes each bib read as one JSON message. RDS Display is the test client. A jumbotron controller implements this same contract.

## Connection

- TCP.
- RDS Results listens on all network interfaces.
- Default port: **9100**. The port is set in RDS Results under Settings, Bib reads, Display port.
- The display connects to the scoring computer's address and that port. On the same computer, connect to `127.0.0.1`.
- No authentication in version 1. Use this on the race network.
- Several displays may be connected at once. Each connection receives the same messages.
- UTF-8. No byte-order mark.
- One JSON object per line. The line ends with `\n`. A `\r` before the `\n` is allowed. JSON strings do not contain raw line breaks.
- Field order does not matter. Unknown fields are ignored. Unknown `ty` values are ignored.
- The display does not need to send anything. RDS Results ignores lines sent by the client in version 1.

A display that loses the connection connects again. Every connection starts over with `hello`.

## Session

1. The server accepts the socket.
2. The server sends `hello`.
3. The server sends the reads it still has, oldest first, at most 100. Each of those is a `read` message with `"update": false` and the latest values for that read.
4. The server sends each new read as it arrives.
5. When a later results refresh fills in finish data for a read already sent, the server sends that read again with `"update": true` and the same `id`.
6. The server sends `ping` about every 10 seconds.

`hello` means the previous board is stale. Clear it, then apply the reads that follow.

## Messages

Every message has `v` and `ty`. Version 1 uses `"v": 1`.

### hello

```json
{"v":1,"ty":"hello","app":"RDS_Results"}
```

| Field | Meaning |
| --- | --- |
| app | Name of the sending program. |

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
{"v":1,"ty":"ping"}
```

The client may ignore `ping`. It shows that the socket is still open.

## Matching behavior the display can rely on

RDS Results matches a read to a local results row by bib, then chip. The matched place, finish time, clock time, pace, division, and report name are included on the `read` message. Until that match exists, those strings are empty and `report` is `Not in results`.

The location on the message is the location of that read. A jumbotron can choose which read to show from `location`.

## What version 1 does not do

The server does not wait for a reply. The server does not send the full results list, only bib reads and the result fields listed above. A later contract version will say so by changing `v` or by adding a new `ty`. Keep accepting version 1 messages, and ignore names and message types this document does not define.
