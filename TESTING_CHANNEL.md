# ThwargFilter test channel

ThwargLauncher injects `ThwargFilter.dll` into `acclient.exe`. The filter exposes a
file-based channel that an external test harness can use to drive a live game client and
to observe what happened.

There are two halves:

* **Actuator** - write chat commands into `incmds_<pid>.txt`; the filter executes them.
* **Observer** - the filter writes `chatlog_<pid>.jsonl` (everything the game said) and,
  on request, `gamestate_<pid>.txt` (a snapshot of the character and its surroundings).

Everything lives in one directory:

```
%AppData%\ThwargLauncher\Running\
```

All files are keyed by the `acclient.exe` process id.

---

## 1. Finding the game process

Every injected client writes a heartbeat file:

```
%AppData%\ThwargLauncher\Running\game_<pid>.txt
```

The `<pid>` in the filename is the `acclient.exe` process id and is the key for every
other file described here. The heartbeat is rewritten roughly every 3 seconds
(`Heartbeat.TIMER_SECONDS`) and carries the server, account, character, team list and an
`IsOnline` flag. A harness should:

1. Enumerate `game_*.txt` and parse the pid out of each filename.
2. Prefer files whose last write time is within the last ~10 seconds; a stale
   `game_<pid>.txt` means that client is gone.
3. Read `CharacterName` / `ServerName` / `AccountName` from the file to pick the right
   client when several are running.

The heartbeat starts as soon as the filter loads, **before any server contact**, so
`game_<pid>.txt` appears within about one beat (~3s) of process start even if the client
never manages to connect. That is what makes section 8 possible.

Because of that, the earliest heartbeats carry an **empty `ServerName` and
`AccountName`**: the filter cannot know which account it belongs to until the server
sends the character list. Identity fields fill in as login progresses. A harness that
matches on server or account must tolerate them being blank for the first few beats, and
should match on pid instead.

From file version 1.5 the heartbeat also carries `LoginStage`, `SecondsInStage`,
`RequestedCharacter` and `StatusNote`. See section 8.

---

## 2. Sending commands (`incmds_<pid>.txt`)

The harness writes the whole file each time. Format (see
`ThwargFilter\Channels\CommandWriter.cs`):

```
FileVersion:1.2
Timestamp=TimeUtc:'2026-07-25T16:23:40.9702660Z'
AcknowledgementUtc:0001-01-01T00:00:00.0000000
CommandCount:2
Command1=TimeStampUtc:'2026-07-25T16:23:40.9702660' CommandString:'/say hello'
Command2=TimeStampUtc:'2026-07-25T16:23:40.9802660' CommandString:'dumpstate'
```

The filter picks the file up two ways: a `FileSystemWatcher` on it (near instant) and the
3 second heartbeat timer (fallback).

### The timestamp trap (read this before writing a harness)

**The `Timestamp` header and the `CommandN` timestamps do NOT use the same format.**

| Field | Parsed by | Correct format |
| --- | --- | --- |
| `Timestamp=TimeUtc:'...'` | `GetUtcDateParam` | ISO UTC **with** the `Z` suffix |
| `CommandN=TimeStampUtc:'...'` | `GetDateParam` then `Command.RoundTrippableTime` | ISO **without** the `Z` suffix: `yyyy-MM-ddTHH:mm:ss.fffffff` |

Use a Z-suffixed timestamp on a `CommandN` line and the command is **silently dropped**:
no error, no log line, nothing happens in game.

Why, measured on a machine at UTC-07:00:

1. `SettingsLineParser.GetDateParam` calls
   `DateTime.Parse(text, null, DateTimeStyles.RoundtripKind)`.
   With a `Z` the result is `Kind=Utc`; without a `Z` it is `Kind=Unspecified`.
2. `Channels.Command`'s constructor then calls `RoundTrippableTime`, which ends in
   `.ToUniversalTime()`. That is a no-op on `Kind=Utc`, but on `Kind=Unspecified` it treats
   the value as **local** time and shifts it forward by the local UTC offset.

   ```
   input '2026-07-25T16:23:40.9702660Z'  ->  Command.TimeStampUtc = 2026-07-25T16:23:40Z  (+0h)
   input '2026-07-25T16:23:40.9702660'   ->  Command.TimeStampUtc = 2026-07-25T23:23:40Z  (+7h)
   ```

3. `ThwargLauncher.exe` itself writes command timestamps with a bare `DateTime.ToString()`
   (`CommandWriter.cs`), i.e. no `Z`, so every command the launcher has ever sent lands in
   that shifted-forward space.
4. `ChannelWriter.ReadCommandsFromFile` only enqueues a command when
   `cmd.TimeStampUtc > channel.LastInboundProcessedUtc`. Once any launcher-written command
   has been processed, that high-water mark sits hours in the future, and a correctly
   Z-suffixed (true UTC) command compares as older and is discarded.

Steps 1 and 2 are measured against the shipped assembly. Steps 3 and 4 are read directly
from the source but the specific offset you see depends on your machine's timezone.

**Practical rule:** write `CommandN` timestamps as
`DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffffff")` with **no** `Z` and no offset,
exactly as the launcher does, and make sure they strictly increase from one write to the
next. Write the `Timestamp` header as `DateTime.UtcNow.ToString("o")`, which does end in
`Z`.

Two more gotchas:

* A file whose `Timestamp` header is more than one hour old is ignored wholesale.
* `FileVersion` must start with `1`.

### What a command string can be

Anything you could type in the chat box, including a leading `/`. It is dispatched through
`DecalProxy.DispatchChatToBoxWithPluginIntercept`, so Decal plugins see it exactly as if
the player had typed it. The filter's own `/tf ...` verbs work too, plus the special case
below.

---

## 3. The `dumpstate` verb

Snapshots live game state to `gamestate_<pid>.txt`.

Reachable two ways:

* Over the channel: send `dumpstate` (or `/tf dumpstate`) as a `CommandString`.
* Typed in game: `/tf dumpstate`. It is listed by `/tf help`.

Channel commands arrive on the heartbeat timer thread or on a `FileSystemWatcher` thread,
never on the game thread, and Decal's `WorldFilter` / `CharacterFilter` may only be touched
on the game thread. So `dumpstate` is asynchronous: the request raises a flag and the
snapshot is taken on the next rendered frame. Expect it to land within a frame or two.

The harness should poll `gamestate_<pid>.txt` and use the `seq` field for freshness: it is
an integer that increments once per snapshot, per game process. Compare against the value
you saw before you asked, rather than trusting the file's mtime.

The file is fully overwritten with `FileShare.None`. A poller that catches the write in
progress gets an `IOException` rather than a truncated file; retry after a few
milliseconds.

### Format (`gamestate_<pid>.txt`, indented JSON)

```json
{
  "utc": "2026-07-25T16:23:14.1592804Z",
  "seq": 3,
  "pid": 33684,
  "loggedIn": true,
  "character": {
    "name": "Cray", "id": 1342177281, "level": 42, "loginStatus": 3,
    "accountName": "...", "server": "...", "race": "Aluvian", "gender": "Female",
    "classTemplate": "...", "totalXp": 0, "unassignedXp": 0, "burden": 0, "vitae": 0
  },
  "vitals": {
    "health":  { "points": 45, "max": 60, "base": 55, "buffed": 60, "bonus": 5 },
    "stamina": { "points": 90, "max": 90, "base": 90, "buffed": 90, "bonus": 0 },
    "mana":    { "points": 30, "max": 40, "base": 40, "buffed": 40, "bonus": 0 }
  },
  "position": {
    "landcell": 11403527, "landcellHex": "0x00AE0107",
    "ew": -1.23, "ns": 4.56,
    "x": 102.3, "y": 44.1, "z": 12.0
  },
  "nearbyTotal": 137,
  "nearbyTruncated": true,
  "nearby": [
    { "id": 1073741825, "name": "Drudge Skulker", "objectClass": "Monster", "distance": 0.014 }
  ],
  "notes": []
}
```

Field notes:

* `loggedIn` is `false` when the character filter has no character id yet (character select
  screen, or the client is still loading).
* `points` is the live pool value Decal exposes directly on the character filter;
  `max` / `base` / `buffed` / `bonus` come from that vital's skill info wrapper, where
  Decal's `Current` is the maximum. If you need certainty for an assertion, assert on the
  ratio of `points` to `max` rather than on absolute numbers.
* `position.landcell` is the landcell (landblock plus cell) as an integer;
  `landcellHex` is the same value formatted the way ACE and `/loc` display it.
  `ew` / `ns` are the in-game coordinates; `x` / `y` / `z` are raw landblock-local
  coordinates.
* `nearby` is the landscape object list, sorted by distance ascending, the player removed,
  and capped at 50 entries. `nearbyTotal` is the uncapped count and `nearbyTruncated` tells
  you whether the cap bit. `distance` is Decal's distance unit (landblocks); multiply by
  240 for metres.
* `notes` collects a human-readable line for every field the filter could not read. An
  empty `notes` array means everything was captured. This never fails hard: a snapshot
  taken at the character select screen produces a valid file whose `notes` explain what was
  missing.

---

## 4. Chat capture (`chatlog_<pid>.jsonl`)

One JSON object per line, appended as things happen, UTF-8 with **no** byte order mark.
Read it by tailing; every line stands alone.

When the file passes about 5 MB it is renamed to `chatlog_<pid>.1.jsonl` (overwriting any
previous rotation) and a fresh `chatlog_<pid>.jsonl` is started. A tailing harness should
handle the live file shrinking to zero.

Every record has:

| field | meaning |
| --- | --- |
| `utc` | ISO 8601 UTC with `Z`, when the filter saw it |
| `seq` | integer, increments per record for the life of the game process |
| `source` | `"network"` or `"chatbox"` (see below) |
| `type` | friendly event name |
| `text` | the message text, trailing newlines stripped |

### `source: "network"`

Parsed from server to client messages on `ServerDispatch`. Adds `opcode` (hex string) and,
depending on the message, `senderName`, `senderId`, `chatType`, `range`, `channel`.

| `type` | `opcode` | extra fields |
| --- | --- | --- |
| `ServerMessage` | `0xF7E0` | `chatType` |
| `HearSpeech` | `0x02BB` | `senderName`, `senderId`, `chatType` |
| `HearRangedSpeech` | `0x02BC` | `senderName`, `senderId`, `range`, `chatType` |
| `EmoteText` | `0x01E0` | `senderName`, `senderId` |
| `SoulEmote` | `0x01E2` | `senderName`, `senderId` |
| `TurbineChat` | `0xF7DE` | `senderName`, `senderId`, `channel` |

`ServerMessage` is what ACE's `GameMessageSystemChat` uses, so it covers almost all system
output including admin command responses. `chatType` is ACE's `ChatMessageType` value.

Example:

```json
{"utc":"2026-07-25T16:23:14.0517416Z","source":"network","type":"ServerMessage","opcode":"0xF7E0","text":"Location: 0x00AE0107 [102.3 44.1 12.0]","chatType":13,"seq":41}
```

### `source: "chatbox"`

Lines drawn into the client chat window, captured from Decal's chat box event. Adds
`color` (the client's chat colour index) and `target` (the chat window id). No `opcode`.

```json
{"utc":"2026-07-25T16:23:15.9012000Z","source":"chatbox","type":"ChatBoxMessage","text":"[UB] 0x00AE0107 102.3 44.1 12.0","color":17,"target":0,"seq":42}
```

### Which source should an assertion read?

This distinction matters and is the reason both hooks exist:

* **Server output is visible on both.** Anything the server sends arrives as a network
  message and is then drawn into the chat window, so it usually appears twice, once per
  source. Assert on `source == "network"` when you want the structured fields
  (`senderId`, `chatType`) and exact server semantics.
* **Decal plugin output is visible only on `chatbox`.** UtilityBelt, VirindiTank and
  friends render their output client-side and never send a network message. A network-only
  assertion will wait forever for `/ub pos` output.

Deduplicate on `(source, text)` if you do not want to see server lines twice.

---

## 5. Test vocabulary

Two practical command families for automated tests.

### ACE admin commands (visible on `network`, and on `chatbox`)

Our test characters are admin. Admin command responses come back as
`GameMessageSystemChat`, so they land in the chat log as `source: "network"`,
`type: "ServerMessage"` and can be asserted on with full structure. Anything typed as a
server command belongs here.

### UtilityBelt commands (visible ONLY on `chatbox`)

These are handled inside the client by the UtilityBelt plugin. They never touch the wire,
so **only** chatbox capture sees their output.

| command | purpose |
| --- | --- |
| `/ub pos` | print current position |
| `/ub id` | print id data for the selected object |
| `/ub propertydump` | dump all properties of the selected object |
| `/ub dumpskills` | dump character skills |
| `/ub mexec <expression>` | evaluate a UtilityBelt meta expression and print the result |
| `/ub use[li][p]` | use an object (by list index / partial name variants) |
| `/ub select[li][p]` | select an object (by list index / partial name variants) |
| `/ub face <heading>` | turn the character to a heading |
| `/ub jump` | jump |

`/ub mexec <expression>` is the most useful of these for a harness: it turns arbitrary
client-side state into a chat line you can assert on.

---

## 6. Recommended harness loop

1. Find `game_<pid>.txt`, confirm it is fresh, note the pid.
2. Open `chatlog_<pid>.jsonl` and seek to the end. Record the last `seq`.
3. Write `incmds_<pid>.txt` with your command and a Z-less, strictly increasing
   `CommandN` timestamp.
4. Poll the chat log for new records with `seq` greater than the one you noted, filtering
   on `source` per the rules above. Time out rather than blocking forever.
5. For positional or inventory assertions, send `dumpstate`, then poll
   `gamestate_<pid>.txt` until `seq` increases, retrying on `IOException`.

---

## 7. Where this lives in the filter

| concern | file |
| --- | --- |
| chat capture, both sources | `ThwargLauncher\ThwargFilter\Observation\ChatObserver.cs` |
| JSONL append and rotation | `ThwargLauncher\ThwargFilter\Observation\ChatLogWriter.cs` |
| `dumpstate` snapshot | `ThwargLauncher\ThwargFilter\Observation\GameStateDumper.cs` |
| file paths | `ThwargLauncher\ThwargFilter\FileLocations.cs` |
| `dumpstate` verb wiring | `ThwargLauncher\ThwargFilter\FilterCommands\FilterCommandParser.cs` |
| `appraise` verb | `ThwargLauncher\ThwargFilter\Observation\Appraiser.cs` |
| `inventoryhook` auto-identify toggle | `ThwargLauncher\ThwargFilter\ThwargInventory.cs` |
| `attack` / `attackstop` verbs | `ThwargLauncher\ThwargFilter\Observation\Attacker.cs` |
| shared name-substring target resolution | `ThwargLauncher\ThwargFilter\Observation\TargetResolver.cs` |
| smoke test fixture (no live client) | `tools\filter-smoke.ps1` |
| event subscriptions | `ThwargLauncher\ThwargFilter\FilterCore.cs` |
| command file format | `ThwargLauncher\ThwargFilter\Channels\` |

Every observation handler swallows its own exceptions and never rethrows, so a parse bug
in this code cannot break the filter or the game. Failures are reported to the filter log
(`%AppData%\ThwargLauncher\Logs\ThwargFilter_<pid>_log.txt`), rate limited so a repeating
error cannot fill the disk.

---

## 8. Diagnosing login stalls

A client that fails to reach the world used to be diagnosable only by waiting for a
timeout and guessing. The heartbeat now reports how far it got, so the diagnosis is a
single file read.

### Fields (heartbeat file version 1.5 and later)

| field | meaning |
| --- | --- |
| `LoginStage` | `Starting`, `CharSelect` or `InWorld` |
| `SecondsInStage` | seconds since the stage last changed; keeps rising while wedged |
| `RequestedCharacter` | the character name the launcher asked for |
| `StatusNote` | free text explaining why the client is stuck, empty when nothing is wrong |
| `LastServerDispatchSecondsAgo` | seconds since the last server message, or **-1** if the client has never heard from a server at all |

Stage transitions:

* `Starting` is set when the filter loads.
* `CharSelect` is set when the server's character list (message `0xF658`) arrives. This is
  the first proof that the client actually reached the server.
* `InWorld` is set when a character materializes after a fresh login.

Logging out back to character select moves the stage back to `CharSelect`, so the field
always describes the present, not the high water mark. Re-entering the stage the client is
already in does not reset `SecondsInStage`, which is what makes a wedge visible as a
steadily rising number.

`StatusNote` is cleared automatically whenever the stage genuinely changes, so a note is
always about the stage it appears next to.

### Decision table

Read `game_<pid>.txt` and match top to bottom:

| observation | diagnosis | action |
| --- | --- | --- |
| No `game_<pid>.txt` at all, ~10s after process start | The filter never loaded. This is an injection problem, not a login problem. | Check Decal injection and that `ThwargFilter.dll` is registered. The game process itself is irrelevant here. |
| `LoginStage:Starting`, `LastServerDispatchSecondsAgo:-1`, `SecondsInStage` rising | Connection stall. The client never reached the server. Commonly the server is down, or a previous hard kill left an account session lingering server side and the new login is refused. | Wait and retry. A lingering session usually clears on its own after the server's timeout. |
| `LoginStage:CharSelect` with a `StatusNote` naming the mismatch | Wrong character name. The requested name is not on this account. | Fix the name. Note that admin promotion renames the displayed character to `+Name`, so the launcher's stored name goes stale after a promotion. `StatusNote` lists the names the server actually offered. |
| `LoginStage:CharSelect`, no `StatusNote`, `SecondsInStage` rising | The name matched but the character select automation did not complete: the click sequence failed, or the client is stuck behind a splash or a queue. | Check the filter log for the character select timer, and confirm the client window is not minimized or otherwise unable to receive synthetic clicks. |
| `LoginStage:InWorld` and `IsOnline:True` | Healthy. | Proceed with the test. |

Two supporting notes:

* `LastServerDispatchSecondsAgo` is `-1` specifically to mean "no server contact ever",
  which is now reachable because heartbeats start before the client connects. Do not read
  `-1` as a small number: it is a sentinel, not a duration.
* `RequestedCharacter` is populated as soon as the launch file is read, so it is available
  for diagnosis even before the login attempt is made.

### Example: the name mismatch case

```
LoginStage:CharSelect
SecondsInStage:37
RequestedCharacter:Cray
StatusNote:requested character 'Cray' not found; available: [+Cray,Alice]
```

The account holds `+Cray` (an admin-promoted name) but the launcher asked for `Cray`.
The same message is written to the filter log via `log.WriteError`.

---

## 9. Appraisal

Several ACE admin commands act on the **last-appraised object** rather than on a named
target: `/remove-vitae` and the creature branch of `/heal` are the ones that bit us. Before
this verb there was no chat-command path to appraise a creature or a player, so those
commands could not be reliably aimed from a harness.

### Selection is not appraisal

This is the trap that cost a live run three readbacks. They are two different things:

* **Selection** is client side. Decal's `Actions.SelectItem` / `Actions.CurrentSelection`,
  and `/ub selectp`, change what the client considers selected. The server is not told.
* **Appraisal** is a server round trip. Only an identify request (Decal's
  `Actions.RequestId`) makes the server record a new appraisal target.

So `/ub selectp` registers a selection but never appraises. `/ub propertydump` did not
unblock it in practice either, and `/ub dumpskills` is silent. None of them are a
substitute for an identify.

### The verb

Reachable over the launcher channel as `appraise ...` (or `/tf appraise ...`), and typed
in game as `/tf appraise ...`. It is registered in `/tf help`.

| command | effect |
| --- | --- |
| `appraise` | appraise the logged in character |
| `appraise self` | same as bare `appraise` |
| `appraise <name-substring>` | case insensitive substring match over landscape objects and players |

Matching rules for the substring form:

* **Exactly one match** - that object is appraised.
* **Zero matches** - nothing is appraised, and the log says so.
* **More than one match** - nothing is appraised. The candidates are logged, nearest
  first, with id, object class and distance, so you can narrow the substring.

The ambiguous case deliberately does **not** guess. Picking one arbitrarily would silently
aim the next admin command at the wrong object, which is worse than doing nothing.

### Result record (machine readable)

Every appraise emits one line into `chatlog_<pid>.jsonl` (see section 4), so a harness can
await the outcome without tailing the filter log:

| field | meaning |
| --- | --- |
| `source` | always `"filter"` (not `"network"` or `"chatbox"`) |
| `type` | always `"AppraiseResult"` |
| `target` | the argument as given, or `"self"` |
| `outcome` | `"requested"`, `"ambiguous"` or `"notfound"` |
| `resolvedId` | the appraised object id, **only** when `outcome` is `"requested"` |
| `resolvedName` | the appraised object name, **only** when `outcome` is `"requested"` |
| `candidateCount` | how many objects matched, **only** when `outcome` is `"ambiguous"` |

```json
{"utc":"2026-07-25T18:42:52.5296132Z","source":"filter","type":"AppraiseResult","target":"self","outcome":"requested","resolvedId":1342177281,"resolvedName":"Cray","seq":1}
{"utc":"2026-07-25T18:42:52.5766398Z","source":"filter","type":"AppraiseResult","target":"drudge","outcome":"ambiguous","candidateCount":3,"seq":2}
{"utc":"2026-07-25T18:42:52.5766398Z","source":"filter","type":"AppraiseResult","target":"nosuch","outcome":"notfound","seq":3}
```

Note that `outcome:"requested"` means the identify request was **sent**, not that the
server has answered. It is the signal that the verb resolved a target, not a confirmation
of appraisal. Allow a beat before the admin command that depends on it.

The human readable log lines below are still written as well; the JSONL record is in
addition to them, not instead.

Results also go to the filter log, whose path is published in the heartbeat as
`LogFilepath` (see section 1). Sample lines:

```
Appraiser: appraise requested for 'drudge'
appraise 'drudge': ambiguous, 3 matches; nothing appraised. Narrow the substring.
appraise candidate: 'Drudge Skulker' id=1073741825 class=Monster distance=0.014
appraise candidate: 'Drudge Prowler' id=1073741842 class=Monster distance=0.031
appraise candidate: 'Drudge Slinker' id=1073741877 class=Monster distance=0.058
```

Appraisal is asynchronous in two steps: the verb marshals onto the game thread and issues
the identify on the next rendered frame, and the server then answers in its own time.
Allow a beat between `appraise` and the admin command that depends on it.

### Caveat: appraisal target drift

**The filter itself can move the server's appraisal target out from under you.**

`ThwargInventory` subscribes to `CoreManager.Current.ItemSelected` and automatically calls
`Actions.RequestId` for any inventory item it has not seen before
(`ThwargLauncher\ThwargFilter\ThwargInventory.cs`). That is an appraisal. So selecting or
hovering inventory items mid-test re-points the server's last-appraised object at a random
item, and a subsequent `/remove-vitae` or `/heal` aims at that item instead of your intended
target.

### Suppressing the drift: the `inventoryhook` verb

The auto-identify can be turned off, so a rig can pin the server's appraisal target.

| command | effect |
| --- | --- |
| `inventoryhook off` | suppress the auto-identify on item selection |
| `inventoryhook on` | restore it |
| `inventoryhook` | report the current state without changing it |

`on` is the **default**, so behaviour is unchanged unless a rig opts out. The argument is
case insensitive, an unrecognized argument changes nothing and logs an error, and every
state change is logged.

While suppressed, selected items are also not recorded as seen, so turning the hook back on
resumes normal behaviour cleanly rather than leaving a gap where items are permanently
treated as already identified.

### Recommended rig pattern

```
inventoryhook off          <- at rig start, pin the appraisal target
appraise <target>          <- await the AppraiseResult record
<admin command>            <- /remove-vitae, /heal, ...
inventoryhook on           <- at cleanup, restore default behaviour
```

Remaining rules even with the hook off:

* Issue `appraise` as late as possible, immediately before the admin command that consumes
  it.
* If a target-sensitive admin command behaves as though it hit the wrong thing, suspect
  drift first and re-issue `appraise`.
* Restore `inventoryhook on` at cleanup. The setting is per game process and lives only in
  memory, so it resets on client restart, but leaving it off will confuse the next person
  to use that client interactively.

---

## 10. Attacking

`attack <name-substring>` and `attackstop`, over the channel or as `/tf`, both listed in
`/tf help`. Target resolution is **identical to `appraise`** (same shared resolver): exactly
one match attacks, zero or several do nothing and log the candidates nearest first.

Each attempt emits an `AttackResult` record into `chatlog_<pid>.jsonl`, the same shape as
`AppraiseResult` (section 9): `source:"filter"`, `type:"AttackResult"`, plus `target`,
`outcome` (`requested` | `ambiguous` | `notfound`), `resolvedId`/`resolvedName` when
requested, `candidateCount` when ambiguous.

```json
{"utc":"...","source":"filter","type":"AttackResult","target":"drudge","outcome":"requested","resolvedId":1073741825,"resolvedName":"Drudge Skulker","seq":7}
```

### Read this before trusting `attack`

**Decal has no attack API.** This was established by enumerating every member of
`Decal.Adapter.Wrappers.HooksWrapper` and of the raw COM interface behind it,
`Decal.Interop.Core.IACHooks` (137 members). The only combat-adjacent members are:

| member | what it does |
| --- | --- |
| `SetCombatMode(CombatState)` | enter or leave combat mode (`Peace`, `Melee`, `Missile`, `Magic`) |
| `CombatMode` | read the current mode |
| `SelectItem(int)` / `CurrentSelection` | client side selection |
| `UseItem(int, int)` | "use" an object |

There is no `Attack`, `AttackSelected` or `MeleeAttack`, and there is no way to send a
client to server message either. (`Decal.Interop.Net`'s `Dispatch` members are the inbound
callback interface Decal calls **on** filters, not a send path.)

So `attack` does the only thing left:

1. resolve the target by name (real API, reliable)
2. `Actions.SelectItem(id)` and set `CurrentSelection` (real API, reliable)
3. `Actions.SetCombatMode(...)` (real API, reliable)
4. **synthesize the client's attack input** (not an API; see below)

Steps 1 to 3 are ordinary Decal calls. Step 4 is the weak link.

**`outcome:"requested"` therefore means "input was issued", NOT "the character is
confirmed to be swinging."** Verify the effect by other means: the target's health
dropping, combat chat in `chatlog_<pid>.jsonl`, or a `dumpstate` vitals read.

### Tuning step 4 without a rebuild

Because the correct input cannot be determined from the assemblies alone, it is
configurable through the filter's settings, and every choice is logged:

| setting | default | meaning |
| --- | --- | --- |
| `AttackMethod` | `key` | `key` posts a held keypress, `useitem` calls `Actions.UseItem(target, 0)`, `both` does both |
| `AttackKey` | `a` | the character posted when `AttackMethod` includes `key` |
| `AttackCombatMode` | `Melee` | `Melee`, `Missile` or `Magic`, passed to `SetCombatMode` |

**The `AttackKey` default of `a` is a placeholder and has not been confirmed against the
client's keybindings.** Confirm it in game and set it accordingly. `AttackMethod` exists so
a live tester can try the alternative path without waiting for a new build.

The keypress is deliberately sent as a **key down that is held**, because the AC client
attacks for as long as the attack input is held. `attackstop` releases it and sets
`CombatState.Peace`. A rig that issues `attack` and never issues `attackstop` leaves the
client believing a key is still down.

Reliability caveats for the synthetic input:

* It is `PostMessage` to the game window, so it depends on the window existing and the
  client honouring posted input. A minimized or input-blocked client may ignore it.
* It cannot be confirmed. Nothing reports back that the client acted on the keypress.
* It is keyboard-layout and keybinding dependent, unlike steps 1 to 3.

### Missile

Missile combat needs no separate call: `SetCombatMode` takes `CombatState.Missile` through
the same code path, so setting `AttackCombatMode=Missile` covers it. What is **not**
verified is whether the same held-key input fires a bow the way it swings a melee weapon.
Multishot testing will need that confirmed in game, and a bow equipped first: nothing here
wields anything, so equip before `attack`.

---

## 11. Plugin output cannot be captured (proven negative)

Chat lines that other Decal plugins print with their own prefixes (`[UB]`, `[VTank]`) are
**not capturable by this filter**. This section records the evidence so nobody re-opens the
question, and gives the workarounds.

### Why

`ChatBoxMessage` (section 4) sees text drawn into the client's chat window. Plugins using
the standard Virindi chat connector do not go that way when VCS is running:

```csharp
// Shared\VCS_Connector.cs, the connector UtilityBelt and VirindiTank build on
if (IsVCSPresent(host))
    VCS5.PluginCore.Instance.FilterOutputText(text, window, color);   // VCS renders it
else
    host.Actions.AddChatTextRaw(text, color, window);                 // client chat window
```

`FilterOutputText` hands the text to VCS5's own window rendering. It never reaches the
client chat window, so Decal never raises `ChatBoxMessage`, so the filter is blind to it.

### Why we cannot hook it

`VCS5.dll` was enumerated in full: **98 types, and zero events on any of them**, public or
private. Its entire public surface that touches text is write-only or a pure function:

| member | direction |
| --- | --- |
| `PluginCore.FilterOutputText(string, int, int)` | write |
| `Presets.FilterOutputPreset(...)` / `RegisterPreset(...)` | write |
| `Rules.ApplyRules(string, int, int)` | pure function you call, not a hook |
| `Actions.ProcessAction(...)` | write |

There is no "text added" event, no observer registration, and no interception point. VCS5
is additionally Dotfuscator-obfuscated, so its internals are single letter names that are
free to change between releases; reaching into them by reflection would be a filter that
breaks on somebody else's upgrade. Note also that VCS5 itself *consumes* Decal's
`ChatBoxMessage` (it has a private handler taking `ChatTextInterceptEventArgs`), which
confirms the direction of travel: VCS is downstream of the event we already listen to, not
a producer we can subscribe to.

**Conclusion: with VCS running, plugin output is uncapturable from a Decal filter.**

### What to do instead

In rough order of preference:

1. **Use server-routed probes.** Anything the SERVER says is captured, reliably and with
   structure, as `source:"network"`. Prefer an ACE admin command over a plugin command
   whenever both could answer the question.
2. **Use the filter's own verbs.** `dumpstate` (section 3) already covers position,
   vitals and nearby objects, which is most of what `/ub pos` and friends were being used
   for, and it lands in a structured file rather than a chat line.
3. **Run the rig without VCS.** Per the connector above, when VCS is *not* running the
   same plugins fall back to `Actions.AddChatTextRaw`, which does go through the client
   chat window. Removing or disabling VCS5 on the test client should therefore make plugin
   output visible on `source:"chatbox"`. This follows from the connector source and is
   **not yet confirmed in game**; confirm before relying on it, and note it only holds for
   plugins that use this standard connector.

What will **not** work, so nobody spends time on it: subscribing to VCS5 (no events),
scraping VCS windows (rendering only, no text model exposed), or reflecting into VCS5
internals (obfuscated and unstable).
