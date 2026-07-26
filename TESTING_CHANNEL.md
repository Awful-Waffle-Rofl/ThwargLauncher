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

### The `state` section: the state oracle

`dumpstate` also emits a `state` object. This is the sub-second deterministic answer to
"what state am I in" for rig validation: the questions with **no other fast truth source**,
since the shard DB lags minutes and chat says nothing about them.

```json
"state": {
  "equipmentSource": "GetByOwner",
  "equipment": [
    { "id": 1073741830, "name": "Yumi", "objectClass": "MissileWeapon", "wieldingSlot": 1, "stackCount": null },
    { "id": 1073741831, "name": "Deadly Frog Crotch Arrow", "objectClass": "MissileWeapon", "wieldingSlot": 2, "stackCount": 87 }
  ],
  "equipmentCount": 2,
  "ammo": { "id": 1073741831, "name": "Deadly Frog Crotch Arrow", "stackCount": 87, "wieldingSlot": 2 },
  "combatMode": { "value": 4, "name": "Missile", "truthSource": "client" },
  "stance": "unavailable: not exposed client-side (server CurrentMotionState is not readable from a filter)",
  "selection": { "id": 1073741900, "hasSelection": true, "name": "Drudge Skulker" }
}
```

#### The three-way contract every field obeys

This is what the deterministic-validation layer consumes, so it is a contract, not a
convention:

| shape | meaning |
| --- | --- |
| a value | the read succeeded, this is the answer |
| `null` | the read succeeded and the answer is genuinely "nothing" |
| `"unavailable: <reason>"` | the read **failed**; the answer is unknown |

A validator must never confuse **empty hand** with **could not read the hand**. So
`equipment: []` means "nothing equipped" while `equipment: "unavailable: ..."` means the
scan failed, and `ammo: null` means "nothing equipped that stacks" (the arrows-ran-out
signal) while `ammo: "unavailable: ..."` means unknown. A failed section is never silently
omitted and never collapses to an empty list.

**`[]` means empty, and nothing else.** This is enforced, not merely intended. The equipment
scan runs a readiness check before it will report an empty array, because the world data can
be legitimately readable while still unpopulated shortly after login. The readiness check
fails closed: if it cannot positively confirm the world is populated, it reports
`unavailable` rather than `[]`. Its signals, in order:

1. the character's own object is not in `WorldFilter` yet;
2. that object has no long properties yet, so its bag is still filling;
3. neither world query enumerated cleanly;
4. nothing at all is carried. A logged-in character always carries something, so zero
   carried objects means the collections have not populated. This one is a heuristic and is
   deliberately biased toward `unavailable`: a false `unavailable` costs a validator a
   retry, while a false `[]` asserts a wrong fact about the world.

#### Fields and their truth source

| field | truth source | notes |
| --- | --- | --- |
| `equipment[]` | **client-instant** | items whose `Wielder` is this character |
| `equipment[].id` / `.name` / `.objectClass` | client-instant | `WorldObject.Id` / `.Name` / `.ObjectClass` |
| `equipment[].wieldingSlot` | client-instant | `LongValueKey.WieldingSlot` (218103819); `null` if the item does not carry it |
| `equipment[].stackCount` | client-instant | `LongValueKey.StackCount` (218103814); `null` on non-stacking items |
| `ammo` | client-instant | derived, see below |
| `combatMode` | **client-instant, client-only** | `Actions.CombatMode`; see the stance caveat |
| `stance` | not available | always `unavailable`, see below |
| `selection` | client-instant | `Actions.CurrentSelection` |
| `inventory[]` | **client-instant** | pack contents, see below |
| `inventory[].type` | client-instant | `LongValueKey.Type` (218103808) = the **weenie class id (wcid)** |
| `attributes` | **client-cached** | six attributes from `CharacterFilter`, `/showstats` is server truth |
| `skills` | **client-cached** | from `CharacterFilter`, `/showstats` is server truth |
| `enchantments` | **client-cached** | active buff count plus spell ids |
| `position` (top level) | client-instant | read at snapshot time |
| `vitals` (top level) | **client-cached** | can lag the server, see caveats |

"client-instant" means the client knows it locally and the read reflects the client's
current belief with no round trip. "client-cached" means the client is holding a value the
server last told it, which can be stale.

#### How each field is derived

* **`equipment`** is every object whose `LongValueKey.Wielder` (218103818) equals this
  character's id. `Wielder` is the discriminator that separates worn/wielded gear from
  things merely carried: an item sitting in a pack carries `Container` instead. That is why
  the scan does not need to walk pack contents.

  **Cost.** The scan is scoped to wielded-only via `WorldFilter.GetByOwner(playerId)`, not
  `GetInventory()`. `GetInventory()` walks everything the character carries including pack
  contents, which is the expensive shape for a 1 to 2 second poll. If `GetByOwner` yields
  no wielded items at all, the scan retries once through `GetInventory()`. `equipmentSource`
  tells you which one produced the result. Capped at 40 entries with `equipmentTruncated`
  if hit.

  **Live result: `GetByOwner` is the populated query.** Both verification runs reported
  `equipmentSource: "GetByOwner"`, so the `GetInventory` retry is a fallback that does not
  normally fire.

  **Live result: hand/wielded items only.** A settled read returned exactly the hand slots
  (for example `Round Shield` slot 3 and `Training Stick` slot 1). The same character's
  shard DB rows show tunic, pants and boots wielder-linked **server-side**, but those worn
  items did **not** appear in the client-side scan. So the client-side `Wielder` key appears
  to cover **wielded items only, not worn armour or clothing**.

  **Resolved, then corrected.** `equipment` is the **union** of:

  1. the item **carries the `Wielder` key at all** (equipped gear), or
  2. the item has a non-zero `EquippedSlots` (corroborates, and names the slot), or
  3. the item carries `Coverage` **and sits in no container** (worn armour and clothing).

  Test 1 checks **key presence, not the value** - see the asymmetry below, which is the
  single most likely thing to trip anyone reading raw keys.

  The container test in (2) is load-bearing and is not optional: a spare shirt in a pack
  also carries `Coverage`, so without it packed clothing would be reported as worn. That
  discriminator is **inferred, not yet live-confirmed** - `equipmentDiagnostics
  .withCoverageNoContainer` reports how many items passed it, so the next live run settles
  whether the count matches what the character is actually wearing.

  Each entry says how it qualified via `equippedVia`, either `"wielder"` or `"coverage"`,
  so a validator can still filter to the live-confirmed hand slots alone. Entries also now
  carry `coverage` (null when absent) and `type` (the wcid). The existing keys keep their
  shape.

  To settle this without guessing, each scan reports counts in `equipmentDiagnostics`:

  ```json
  "equipmentDiagnostics": {
    "byOwner":   { "read": true, "total": 12, "withWielder": 2, "withCoverage": 5, "wieldedByMe": 2 },
    "inventory": null
  }
  ```

  `withCoverage` counts objects carrying `LongValueKey.Coverage` (218103821), which worn
  armour and clothing have. If a live run shows `withCoverage` well above `withWielder`,
  the worn items are present in the collection and simply lack the client-side `Wielder`
  key, and the filter could be widened to include them. `inventory` is `null` when the
  fallback scan never ran.

* **`ammo`** is a **lookup, not a heuristic** (this was corrected after a live A/B probe;
  see "How equipped is actually detected" below). Equipped ammunition is the equipped item
  whose `EquippedSlots` is `0x800000` (`EquipMask.MissileAmmo`). A fallback also accepts an
  equipped stack whose `Wielder` **value** is `0`, for a client that reports the key without
  the mask. `ammo: null` now genuinely means "nothing in the ammo slot". `ammoCandidates`
  reports ties.

* **`combatMode`** is `Actions.CombatMode`, the same value the `attack` verb sets via
  `SetCombatMode`. Values are `Peace`(1), `Melee`(2), `Missile`(4), `Magic`(8).

* **`selection`** is `Actions.CurrentSelection`, with the name resolved through
  `WorldFilter[id]` when possible. `id` 0 with `hasSelection: false` is a **successful**
  read meaning nothing is selected. **Negative id caveat:** Decal exposes object ids as
  `Int32`, so genuine AC GUIDs above 2^31 appear **negative**. A validator matching ids
  must expect negative values; do not treat a negative id as an error.

#### How "equipped" is actually detected

**`EquippedSlots != 0` is the ONLY stable equipped discriminator.** Everything else is
informational.

Established across several live sessions on the same character:

| state | `EquippedSlots` | `Wielder` |
| --- | --- | --- |
| equipped **weapon** (wand) | non-zero (`0x1000000`) | `characterId` |
| equipped **ammunition** | `0x800000` (`MissileAmmo`) | `0` in one session, **absent entirely** on a fresh login |
| **just unequipped** | **`0`** | **`0` - the key is still there** |
| packed item | absent / `0` | absent |
| worn clothing | non-zero (`196`, `384`, `14` observed) | `characterId` |

Three things follow, and the third is the one that bit us:

1. **`Wielder` is not necessary.** Equipped ammunition may carry no `Wielder` key at all.
2. **`Wielder`'s value is inconsistent.** It is the character id for weapons but `0` for
   ammunition, so a `Wielder == characterId` test silently excludes all ammo. (This was the
   first bug found here.)
3. **`Wielder` is not sufficient, because unequip ZEROES the keys rather than REMOVING
   them.** A just-unequipped item still carries a `Wielder` key. An `Exists(Wielder)` test
   therefore reports it as still equipped.

> **Ledger L8-5.** Consequence of (3): `unwield` verifies its own work by re-reading the
> equipped set, so with a `Wielder`-presence arm it saw the moved item as still equipped and
> reported `outcome: "failed"` for a move that had **actually succeeded** (server-confirmed
> via `/save-now`: `Wielder` row gone, `Container` set). **A false failure is worse than a
> false success here**, because a rig retries or aborts on it. The test is now
> `EquippedSlots != 0` only.

`wielderValue` is still reported per entry, because it usefully distinguishes a **weapon**
(`characterId`) from **ammunition** (`0` or `null`) - just never use it to decide whether
something is equipped.

**`EquipableSlots` and `EquippedSlots` are different keys**, and the distinction is the
whole puzzle:

* `EquipableSlots` (218103822) - where the item **can** go. Present whether equipped or not.
  This is what the `wield` verb uses to choose a slot mask.
* `EquippedSlots` (**10**) - where the item **currently is**. Non-zero only while equipped.
  Note the bare value `10`: it sits outside the `2181038xx` block, which is why it looks
  like a decoy until you need it.

Each `equipment` entry reports `equippedVia` (`equippedSlots` or `coverage`) plus the raw
`wielderValue` and `equippedSlots`. `equipmentDiagnostics` reports `withEquippedSlots`,
`withWielderKey` and `withWielder` so any drift in these signatures is visible immediately.

**The Coverage arm is retained but is probably redundant.** Worn clothing was observed
carrying non-zero `EquippedSlots`, so the `Coverage`-with-no-`Container` arm should never be
the only thing admitting an item. It is kept as a safety net rather than removed blind, and
`equipmentDiagnostics.withCoverageOnlyAdmitted` counts items admitted by it alone. If that
stays `0` across live runs, the arm can be removed with evidence.

**Self-test:** `unwield <something equipped>` must report `outcome: "requested"`. If it
reports `"failed"` for an item that visibly moved, the equipped test has regressed.

#### `inventory`: pack contents

Everything carried that sits **inside a container**, which is the exact complement of the
equipped set. `WorldFilter.GetInventory()` returns the full carried set **including nested
packs**, so nested contents are covered; each entry reports its `container` id so the
nesting is visible.

```json
"inventory": [
  { "id": 1073742001, "name": "Prismatic Taper", "objectClass": "SpellComponent",
    "stackCount": 178, "container": 1073741900, "type": 20630 }
],
"inventoryCount": 1,
"inventoryTotal": 1
```

* `stackCount` and `container` are `null` when the item does not carry that key.
* `type` is `LongValueKey.Type` (218103808), which is the **weenie class id (wcid)**. This
  was verified positionally on both sides rather than assumed: ACE writes
  `Name, WeenieClassId, IconId, ItemType, flags` (`WorldObject_Networking.cs:76-80`) and
  Decal's own schema names those same fields `name, type, icon, category, behavior`
  (`messages.xml`, `GameData`). So a validator can match "do I have a sword" by name
  substring **and** by wcid.
* Capped at 200 entries with `inventoryTruncated`; `inventoryTotal` is the uncapped count.

**Cost.** This is the expensive query. Equipment is scoped via `GetByOwner`, but inventory
must walk `GetInventory()`. If snapshot cost becomes a problem at a 1 to 2 second poll,
this is the section to drop first.

#### `attributes`, `skills`, `enchantments`

All three are **client-cached**: what the server last told the client. `/showstats` remains
server truth, and validators should cross-check against it for anything authoritative.

```json
"attributes": {
  "strength": { "base": 100, "buffed": 130, "creation": 10, "exp": 1234567 },
  "truthSource": "client-cached"
},
"skills": {
  "Bow": { "current": 315, "base": 290, "buffed": 315, "training": "Specialized", "known": true }
},
"skillsProbed": 48,
"skillsOmitted": 31,
"skillsTruthSource": "client-cached",
"enchantments": { "count": 12, "spellIds": [1234, 5678], "truthSource": "client-cached" }
```

* **`attributes`** covers the six from `CharacterFilter.Attributes`, each with `base`,
  `buffed`, `creation` and `exp`.
* **`skills`** only emits skills whose training state is **not** `Unusable`. There are 48
  `CharFilterSkillType` values and dumping all of them every snapshot would bloat a file
  meant for fast polling. `skillsProbed` and `skillsOmitted` make the filtering visible, so
  an absent skill is never ambiguous. `training` is one of `Untrained`, `Trained` or
  `Specialized`.
* **`enchantments`** exists because auto-buffing plugins silently move a rig's baseline.
  `count` is the primary signal; `spellIds` is included because it costs one int per
  enchantment and lets a rig assert on a specific buff. Capped at 100 with
  `spellIdsTruncated`.

**Out of scope:** abilities and points are server-custom and are readable only by chat
readback. They are deliberately not in this snapshot.

#### `spellbar`: the client spell bar

How client-initiated casting is aimed. **CLIENT-INSTANT**: `CharacterFilter.SpellBar(tab)`
is a local read.

```json
"spellbar": [
  { "slot": 1, "spellId": 1234, "known": true,  "hotkey": "Digit1" },
  { "slot": 2, "spellId": null, "known": null,  "hotkey": "Digit2" }
],
"spellbarTab": 0,
"spellbarSlots": 7,
"spellbarOccupied": 1,
"spellbarTruthSource": "client-instant"
```

* Slots are reported **1-based**, matching the hotkeys that fire them. The underlying
  Decal collection is 0-based; the oracle does that conversion so a rig never has to.
* `spellId: null` is a **successful** read meaning the slot is empty, not a failure.
* `known` is `CharacterFilter.IsSpellKnown(spellId)`, so a rig can tell "slot populated with
  a spell this character cannot actually cast" from a usable slot.
* `hotkey` is the key the `cast` verb would post for that slot.

#### What `combatMode` is NOT

`combatMode` is the **client's** belief. The filter cannot read the server's
`CurrentMotionState`, so:

* it does **not** confirm the server agrees;
* it does **not** tell you the animation stance, whether an attack is mid-swing, or whether
  the character is actually swinging;
* if a mode change was rejected or has not yet been applied server-side, this value will
  disagree with the server.

`stance` is therefore always `"unavailable: ..."` with that reason spelled out, rather than
being omitted, so a validator sees an explicit "no such truth source here" instead of a
missing key.

#### Known caveats: staleness

Two ways a snapshot can mislead. Neither is fixed here; both are avoidable.

* **Vitals are client-cached and can lag the server.** The `vitals` block reflects what the
  client was last told. For server truth use `/showstats` and read the response from
  `chatlog_<pid>.jsonl` (`source:"network"`). Prefer vitals for fast polling and
  `/showstats` for assertions that must be authoritative.

* **Reads before roughly 15 to 20 seconds after entering the world are unreliable.** A
  live run at about 4 seconds after login returned zero wielded items for a character
  demonstrably wearing several, because `WorldFilter` had not populated yet. That is now
  detected rather than reported as fact: the equipment scan is gated on a readiness check
  and reports `"unavailable: worldfilter not yet populated (<reason>)"` instead of an empty
  array. A validator polling early gets an honest "I do not know" and should retry. A
  settled read at about 20 seconds was correct in the same runs.

* **Position can be stale if `dumpstate` is batched with a movement command.** If a single
  command file contains `/teleloc ...` followed by `dumpstate`, the snapshot can be taken
  before the client has finished moving, reporting the old position. **Send them as
  separate writes to `incmds_<pid>.txt`**, and confirm the `seq` advanced between them,
  rather than relying on ordering within one batch.

#### Polling

The snapshot is on-demand: it happens once per `dumpstate` verb call, not on a timer. The
wielded-only scan plus the four oracle reads is cheap enough to poll at 1 to 2 second
intervals. The expensive part of the snapshot remains `nearby`, which walks the landscape;
that was already capped at 50 entries.

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
| state-oracle section of `dumpstate` | `ThwargLauncher\ThwargFilter\Observation\StateOracle.cs` |
| `appraise` verb | `ThwargLauncher\ThwargFilter\Observation\Appraiser.cs` |
| `inventoryhook` auto-identify toggle | `ThwargLauncher\ThwargFilter\ThwargInventory.cs` |
| `attack` / `attackstop` verbs | `ThwargLauncher\ThwargFilter\Observation\Attacker.cs` |
| `unwield` verb | `ThwargLauncher\ThwargFilter\Observation\Unwielder.cs` |
| `wield` verb | `ThwargLauncher\ThwargFilter\Observation\Wielder.cs` |
| equipped-set resolution shared by verbs | `ThwargLauncher\ThwargFilter\Observation\EquippedItems.cs` |
| verified, self-healing combat mode | `ThwargLauncher\ThwargFilter\Observation\CombatModeSetter.cs` |
| spell bar and client casting | `ThwargLauncher\ThwargFilter\Observation\SpellBar.cs` |
| wielded weapon and the mode it implies | `ThwargLauncher\ThwargFilter\Observation\WieldedWeapon.cs` |
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

Steps 1 to 3 are ordinary Decal calls. Step 4 is synthetic input, but it is no longer
guesswork: the exact recipe below is live-verified.

**`outcome:"requested"` therefore means "input was issued", NOT "the character is
confirmed to be swinging."** Verify the effect by other means: the target's health
dropping, combat chat in `chatlog_<pid>.jsonl`, or a `dumpstate` vitals read.

### Step 4: the verified attack recipe

Live-verified against a target dummy, in melee and in missile mode. After the select and
combat-mode steps, post to the client window:

```
WM_KEYDOWN  wParam = VK_END (0x23)   lParam = 0x014F0001
   ... hold about 200 ms ...
WM_KEYUP    wParam = VK_END (0x23)   lParam = 0xC14F0001
```

Two things about that lParam matter, and both were wrong in the first version of this verb:

* **The extended-key bit (bit 24) must be set.** End is an extended key. `0x014F0001` is
  repeat count 1, scan code `0x4F` in bits 16-23, extended bit set. Key up is the same
  value with bits 30 and 31 added, hence `0xC14F0001`.
* **The key cannot be expressed as a character.** `PostMsgs.CharCode()` / `ScanCode()` map
  only `a-z`, `/` and space and fall through to `0x20` for everything else, and never set
  the extended bit. The old character-typed `AttackKey` setting was therefore
  unreachable-by-construction for End, Delete and the rest. Keys are now selected by
  **name** against a table that carries the scan code and extended flag.

**One tap is enough.** The tap starts the client's own repeating attack loop; it does not
need to be held down. A second tap does **not** stop it. `attackstop` (peace mode) is the
only way to stop attacking.

### Settings

| setting | default | meaning |
| --- | --- | --- |
| `AttackKey` | `End` | key **name** from the vocabulary below |
| `AttackKeyHoldMs` | `200` | milliseconds between the key down and key up of the tap |
| `AttackCombatMode` | `Melee` | `Melee`, `Missile` or `Magic`, passed to `SetCombatMode` |
| `AttackMethod` | `key` | `key` posts the tap, `useitem` calls `Actions.UseItem(target, 0)`, `both` |

The defaults are the verified recipe. `AttackMethod=useitem` is retained only as a
fallback; it is not the proven path. An unrecognized `AttackKey` logs an error listing the
valid names and falls back to `End` rather than posting something arbitrary.

### Native key vocabulary

These are the AC combat keys, as bound in the client keymap.

| name | vk | scan | extended | what it does in game |
| --- | --- | --- | --- | --- |
| `End` | `0x23` | `0x4F` | yes | attack low / missile aim low / cast current spell |
| `Delete` | `0x2E` | `0x53` | yes | attack high / missile aim high |
| `PageDown` | `0x22` | `0x51` | yes | attack medium / missile aim medium |
| `Insert` | `0x2D` | `0x52` | yes | attack bar step down |
| `PageUp` | `0x21` | `0x49` | yes | attack bar step up |
| `Apostrophe` | `0xDE` | `0x28` | no | select closest monster |
| `Backtick` | `0xC0` | `0x29` | no | toggle combat mode |

The three attack-height keys double as **missile aim heights** in missile mode. The two bar
keys read as **power and speed** in melee and as **accuracy** in missile. The keymap also
binds `DIK_END` to `CombatCastCurrentSpell` in MagicCombat mode, so the same End tap should
fire casting once a spell is selected.

Aliases `PgDn`, `PgUp`, `Del`, `Ins`, `Grave` and `Quote` are accepted.

**Verification status:** only the `End` row is live-verified. The others are derived from
the same standard PS/2 set 1 scan-code table and share End's shape, so they are high
confidence but not individually proven.

### The keymap is the authoritative binding source

Which key does what is the player's own binding, not a constant. The authoritative file is:

```
C:\Users\danie\OneDrive\Documents\Asheron's Call\acclient.keymap
```

**DIK vs VK caveat:** that file records bindings as **DIK** (DirectInput) scan codes, not
virtual key codes. `DIK_END` is `0xCF`, which is the base make code `0x4F` with
DirectInput's extended marker `0x80` added. The table above splits that into the scan code
and the extended flag that a `WM_KEYDOWN` lParam actually wants. So: read the keymap for
**which** key is bound to a combat action, and read the table above for **how** to post it.

### Remaining caveats

* Posted input cannot be confirmed. Nothing reports back that the client acted on it, so
  `outcome:"requested"` still means "input was issued", not "the character is swinging".
  Verify by target health, combat chat, or a `dumpstate` vitals read.
* It is `PostMessage` to the game window, so a minimized or input-blocked client may
  ignore it.
* Bindings are per-player. If the keymap differs from the table, set `AttackKey`
  accordingly.

### Combat mode follows the wielded weapon

**Combat mode is not an independently settable axis.** This is the single most important
thing to know before automating anything combat-related:

* The backtick key toggles **Peace <-> Combat**.
* **Which** combat mode you get is **derived from the wielded weapon**:
  wand, staff or orb gives Magic; bow or crossbow gives Missile; a melee weapon (or
  nothing, for unarmed) gives Melee.

A player cannot reach "melee mode while carrying a wand" at all, and the client says so
outright: *"You can't enter melee mode while carrying a wand."*

**So the rig sequence is always: wield the right weapon FIRST, then enter combat.** Never
"set a mode, then wield". A `dumpstate` read of `state.equipment` tells you what is wielded
before you try.

This is very likely the real explanation for ledger **L6-76**, where `SetCombatMode`
appeared to no-op in 2 runs out of 4: those were **mode/weapon mismatches, not a race**.
Asking for a mode the wielded weapon cannot produce is a request the client can only refuse,
and `SetCombatMode` has no failure channel to report the refusal.

#### The ladder

`Actions.CombatMode` is a same-process client-truth read, so the outcome can always be
verified even though `SetCombatMode` cannot report failure.

0. **Inspect.** Read `Actions.CombatMode` and the wielded weapon.
   * Already in a combat mode that satisfies the goal: **done, no input at all**.
   * Already in a *different* combat mode than a specific request: **fail fast**. Getting
     there means changing weapon, which is the caller's job.
   * Request inconsistent with the wielded weapon: **fail fast**, naming what to wield.
1. **Optional `SetCombatMode`**, only when the request is consistent with the weapon, then
   verify. Skipped entirely when the weapon class cannot be determined, in which case the
   filter does what a player does and goes straight to the toggle.
2. **Post Backtick** (the native toggle), verify. Up to 3 toggle attempts.

Fail-fast matters: it turns an impossible request into an immediate, diagnosable error
instead of a 20 second convergence timeout that ends in silence.

Every verify logs the observed mode **alongside the wielded weapon**, so any future
mismatch is self-evident in the log without cross-referencing a `dumpstate`.

#### `attack` defaults to "any combat mode"

Because the weapon decides, `AttackCombatMode` now defaults to **`Any`**: the attack verb
only needs the client to be *in combat*, not in a particular mode. Setting it to `Melee`,
`Missile` or `Magic` is still allowed and is then treated as a specific request with the
fail-fast rules above; that is only useful when the rig is also controlling what is wielded.

The attack input is withheld until the mode is settled, because a tap posted while the
client is still in `Peace` does nothing.

#### Fields in `AttackResult`

| field | meaning |
| --- | --- |
| `combatModeRequested` | `"Any"`, or the specific mode requested |
| `combatModeFinal` | the mode actually observed on the last verify |
| `combatModeVerified` | `true` when the goal was met |
| `combatModeWeapon` | what was wielded, so a mismatch is self-evident |
| `combatModeImpossible` | `true` when the request was refused as impossible for that weapon |
| `combatModeRetries` | toggle attempts beyond the first |
| `combatModeUsedToggle` | `true` when Backtick was posted |
| `combatModeUsedSetCombatMode` | `true` when the optional `SetCombatMode` rung ran |
| `combatModeDetail` | short reason the ladder ended where it did |
| `combatModeObserved` | the mode read at **each** verify, oldest first |

```json
"combatModeRequested":"Any","combatModeFinal":"Magic","combatModeVerified":true,
"combatModeWeapon":"'Acid Wand' (WandStaffOrb -> Magic)","combatModeImpossible":false,
"combatModeRetries":0,"combatModeUsedToggle":true,"combatModeUsedSetCombatMode":false,
"combatModeDetail":"entered combat by Backtick toggle","combatModeObserved":["Peace","Magic"]
```

An impossible request reads unmistakably:

```json
"combatModeRequested":"Melee","combatModeFinal":"Peace","combatModeVerified":false,
"combatModeImpossible":true,
"combatModeDetail":"requested Melee but wielded item is 'Acid Wand' (WandStaffOrb), which produces Magic; wield a melee weapon (or nothing, for unarmed) first"
```

An unverified mode does **not** abort the attack. The input is still synthesized and the
record carries `combatModeVerified: false`, because refusing outright would hide the failure
from the harness.

**Stale ladders self-clear.** The ladder only advances on render frames, so a client that
stops rendering could otherwise leave it in flight forever and every later request would be
rejected as busy. A ladder older than 5 seconds is abandoned (its original caller is told,
not left hanging) and the new request takes over.

### Stopping, and stop latency

`attackstop` releases any key still held and sets `CombatState.Peace`.

Inside the filter, stop is handled on the **next render frame** (order of milliseconds):
the stop flag is checked before queued attacks, and it clears the attack queue, so it can
never be stuck behind a backlog. The dominant latency is the **launcher channel**, not the
filter: inbound commands are picked up by a `FileSystemWatcher` (near instant) or the 3
second heartbeat timer as fallback. A harness that writes `incmds_<pid>.txt` gets the
watcher path.

Batch 5 saw one `attackstop` appear to lag about 13 seconds in an ambiguous case, but a
controlled retest stopped within one swing. If a slow stop recurs, check first whether the
`attack` that preceded it actually resolved: an ambiguous or not-found `attack` never
started anything, so what looks like a slow stop may be an attack that never began.

### Missile

Missile needs no separate call: `SetCombatMode` takes `CombatState.Missile` through the
same path, and the attack-height keys are the missile aim heights, so the same End tap is
verified to work in missile mode. A bow must be equipped first, because nothing here
wields anything.

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
3. **Running the rig without VCS: UNVERIFIED, and observed failing.** Per the connector
   above, when VCS is *not* running the same plugins fall back to
   `Actions.AddChatTextRaw`, so in theory the text reaches the client chat window and
   becomes visible on `source:"chatbox"`.

   **Batch 5 contradicts this.** Plugin text was observed drawn in the client's own chat
   window and still did not appear in capture. So either `AddChatTextRaw` does not raise
   `ChatBoxMessage` (plausible: it is the client adding text to itself, not text arriving
   through the path Decal intercepts), or those plugins do not use this connector, or VCS
   was still active in that configuration. The cause was not isolated.

   Treat this as a lead, not a workaround. Do not build a rig on it.

What will **not** work, so nobody spends time on it: subscribing to VCS5 (no events),
scraping VCS windows (rendering only, no text model exposed), or reflecting into VCS5
internals (obfuscated and unstable).

**The reliable rule is server-routed probes only.** Anything that must be asserted on has
to come from the server (`source:"network"`) or from this filter's own verbs, which write
structured records. Plugin chat output is not a supported observation channel, in any
configuration, until somebody proves otherwise in game.


---

## 11b. `itemkeys`: the probe that settles key questions

```
itemkeys <name-substring>     itemkeys quarrel
itemkeys <wcid>               itemkeys 31716
itemkeys id:<id>              itemkeys id:-2147481121
```

Dumps **every** property the client holds for matching objects: all `LongKeys` with values
(named via the `LongValueKey` enum where they map, `unnamed(N)` where they do not), plus
`BoolKeys`, `DoubleKeys`, `StringKeys`, and a `named` convenience block pulling out
`Container`, `Wielder`, `WieldingSlot`, `EquippedSlots`, `EquipableSlots`, `EquipType`,
`Coverage`, `StackCount`, `MissileType`, `UsageMask`, `SlotLegacy`, `ItemSlots` and `Type`.

Output goes to three places so whichever suits the task wins:

* the filter log, one line per key, for eyeball diffing;
* an `ItemKeys` record in `chatlog_<pid>.jsonl`, for programmatic diffing;
* `objectkeys_<pid>.json`, full overwrite, for a side-by-side file diff.

**The method that works:** produce state A, dump, toggle one thing, dump again, diff. That
is exactly how the equipped-ammo discriminator above was found, after reasoning from
documentation had failed. When a question is "which key means X", do not theorise - probe.
`dumpkeys` is accepted as an alias.

## 12. Freeing a hand: the `unwield` verb

### Why this exists

**There was no way to empty a character's hands from the command channel at all**, which
blocked every loadout swap: no caster swap, no ammo swap, nothing. Verified in ACE source:

* `/trywield` refuses while a hand is occupied (`Player_Inventory.cs`,
  `CheckWeaponCollision`, `EquipMask.Held` refuses when mainhand or offhand is non-null).
* Moving a wielded item between slots is blocked by `WieldedLocationIsAvailable`.
* The remaining server-side dequip paths either drop the **entire** inventory on the ground
  (`/fumble`) or require the item to be the client's **last-appraised** object.

And that last route is closed too: **equipped items cannot be appraised.** The filter's
`TargetResolver` scans landscape objects and players only, so an equipped shield resolves
`notfound`.

`unwield` sidesteps all of it by working **client side** and resolving the target from the
**equipped set**, which already knows every wielded item, its id and its slot. The appraisal
problem simply never arises.

### The verb

```
unwield <name-substring>     e.g.  unwield shield
unwield <slot>               e.g.  unwield 1
```

A target that parses as a plain integer is treated as a **wielding slot**, which is how a
rig frees "whatever is in that hand" without knowing the item's name. Anything else is a
case-insensitive name substring.

Exactly one match is moved. **Zero or several move nothing** and report, on the same
never-guess rule as `appraise` and `attack`: unwielding the wrong item silently changes the
loadout a test is measuring.

### The API, with argument order settled

Verified by reflection **including parameter names**, which removes the guesswork entirely:

```
Decal.Adapter.Wrappers.HooksWrapper
  MoveItem(Int32 objectId, Int32 destinationId)
  MoveItem(Int32 objectId, Int32 destinationId, Int32 moveFlags)
  MoveItem(Int32 objectId, Int32 packId, Int32 slot, Boolean stack)    <- used
backed by Decal.Interop.Core.IACHooks
  MoveItem(Int32 lObjectID, Int32 lPackID, Int32 lSlot, Boolean bStack)
```

The parameter names settle it: object first, then the destination **pack**, then the slot
within that pack, then whether to stack.

**The main pack's container id is the character's own id.** Not a guess: items sitting in
the main pack carry `LongValueKey.Container` equal to the character id, which is exactly
what `state.inventory[].container` reports. Slot `0` lets the client pick a free slot rather
than fighting it for a specific one.

**Dropping is deliberately not a fallback.** `Actions.DropItem` exists but litters the
world, so it is never used here.

### Verification and failure

The move is verified by **re-reading the equipped set** on a later frame: if the item is no
longer in it, the move worked. There is one retry, then the verb **reports rather than
hangs**. A full pack is the likeliest cause of a genuine failure and the rig can act on it.

### `UnwieldResult` record

```json
{"utc":"...","source":"filter","type":"UnwieldResult","target":"shield","outcome":"requested",
 "resolvedId":100,"resolvedName":"Round Shield","objectClass":"Armor","fromSlot":2,
 "wasWielded":true,"detail":"moved to pack"}
```

| field | meaning |
| --- | --- |
| `target` | the argument as given |
| `outcome` | `requested`, `ambiguous`, `notfound` or `failed` |
| `resolvedId` / `resolvedName` / `objectClass` | the item acted on, absent when nothing resolved |
| `fromSlot` | the wielding slot it came out of, `null` for worn items |
| `wasWielded` | `true` for hand items, `false` for worn armour or clothing |
| `detail` | short reason, e.g. `still equipped after 2 attempts; pack may be full` |

`outcome: "requested"` here is stronger than for `attack` or `cast`: it is only written
**after** the item was confirmed gone from the equipped set.

### The `wield` verb: equip from the pack

The counterpart to `unwield`, and the answer to two independent dead ends:

* **No available server command can put ammunition into the ammo slot on this build.**
  `/trywield` with correct bare-hex guids taken from the server's own `/ci` audit lines
  produced **no message and no effect**, for Arrow (wcid 300) and Quarrel (31716), with and
  without a matching launcher wielded. Missile rigs were impossible.
* **`/ub useip` is not deterministic.** It was observed to silently no-op, unequip, or swap
  depending on state, and all three are indistinguishable from chat.

```
wield <name-substring> [slot]     wield yumi        wield yumi 2
wield <wcid> [slot]               wield 300         wield 300 2
```

A bare integer target is a **wcid**, which is what makes ammunition addressable: the module
lane's Arrow is `wield 300`, Quarrel is `wield 31716`. An optional trailing integer is the
slot, so multi-word names still work (`wield Deadly Frog Crotch Arrow`).

> **Note the deliberate asymmetry with `unwield`:** a bare integer means a **slot** for
> `unwield` and a **wcid** for `wield`. You unwield *from a place you know*; you wield *an
> item you know*. Both are documented on their own verb and both are logged, so the
> resolution path is always visible in the record.

Exactly one match is equipped. Zero or several equip nothing and report, the same never-guess
rule as everywhere else, and specifically the failure mode that made `/ub useip` unusable.

#### The API

Verified by reflection **including parameter names**:

```
HooksWrapper.AutoWield(Int32 item)
HooksWrapper.AutoWield(Int32 item, Int32 slot, Int32 explic, Int32 notexplic)
HooksWrapper.AutoWield(Int32 item, Int32 slot, Int32 explic, Int32 notexplic, Int32 zero1, Int32 zero2)
  backed by IACHooks.AutoWield / AutoWieldEx / AutoWieldRaw
```

`AutoWield` is a **dedicated equip member**, so this is not a `MoveItem` trick. `MoveItem`'s
destination parameter is literally named `packId`, and there is **no `EquipMask`-style enum
anywhere in `Decal.Adapter`**, so an equipment slot is not expressible as a `MoveItem`
destination at all. Being a client hook, `AutoWield` is almost certainly what `/ub useip`
ultimately drives; the difference is that this verb **verifies the outcome** instead of
leaving a no-op indistinguishable from a success.

#### Verification, and the ammo question it settles

The wield is confirmed by **re-reading the equipped set** on a later frame. One retry, then
it reports `failed` with a `detail` naming the likely cause (slot occupied: `unwield` first).

`outcome: "requested"` is written **only after the item is confirmed present in equipment**.

The record also reports what keys the item ended up carrying, which **settles the open
question of whether equipped ammunition is Wielder-linked client side** - something the
oracle could previously only guess at:

```json
{"utc":"...","source":"filter","type":"WieldResult","target":"300","outcome":"requested",
 "resolvedId":201,"resolvedName":"Deadly Frog Crotch Arrow","objectClass":"MissileWeapon",
 "wcid":300,"stackCount":250,"equippedAfter":true,"wieldingSlot":3,
 "carriesWielder":true,"carriesCoverage":false,"looksLikeAmmo":true,"detail":"equipped"}
```

| field | meaning |
| --- | --- |
| `wcid` / `stackCount` | from the inventory entry before the move |
| `equippedAfter` | whether the item is in the equipped set after the attempt |
| `wieldingSlot` | the slot it actually landed in |
| `carriesWielder` | **the ammo answer**: does the equipped item carry `Wielder`? |
| `carriesCoverage` | does it carry a `Coverage` mask instead? |
| `looksLikeAmmo` | would the oracle's ammo heuristic (wielded plus a stack) fire? |

If `carriesWielder` comes back `false` for ammunition on a live run, the oracle's `ammo`
heuristic needs widening and this record is the evidence for it.

### Rig pattern: swapping to a caster

```
unwield <current weapon>     await UnwieldResult outcome requested
wield <caster>               await WieldResult outcome requested
dumpstate                    confirm state.equipment shows the caster
<enter combat>               mode follows the weapon, see section 10
```

## 13. Client-side casting

### Do not use server-side `/castspell` for cast mechanics

**`/castspell` bypasses the client cast path entirely.** It skips the client's cast
animation and fires far faster than the client would ever allow.

That makes it **useless for any test that measures cast timing or exercises cast
mechanics**, and it silently produces false negatives rather than errors. This is not
theoretical: netherrush (void cast-speed stacking) never appeared to fire under server-side
casts, which retroactively explains ledger rows **L6-52/53** marking netherrush and
flatcastspeed "structurally unobservable". They were not unobservable. They modify exactly
the path `/castspell` skips.

| use case | use |
| --- | --- |
| cast speed, cast stacking, cast animation, anything timing-sensitive | **client casting** (this section) |
| "did the spell land", "does the effect apply", bulk setup | `/castspell` is fine |

### How client casting works here

The client's own cast path is triggered by the numbered spell bar hotkeys. So: clear the
bar, place the spells under test into known slots, and press the corresponding number. That
is the native path, animations and timing included.

### What Decal exposes for the spell bar

All verified by reflection against the referenced `Decal.Adapter.dll` 2.9.7.5, and all
present. This is **not** a blind-and-verify-by-effect situation:

| purpose | member |
| --- | --- |
| read a bar | `CharacterFilter.SpellBar(int tab)` -> `ReadOnlyCollection<int>` of spell ids |
| known spells | `CharacterFilter.SpellBook` -> `ReadOnlyCollection<int>` |
| is a spell known | `CharacterFilter.IsSpellKnown(int)` |
| add to a bar | `Actions.SpellTabAdd(int, int, int)` |
| remove from a bar | `Actions.SpellTabDelete(int, int)` |
| item shortcut bar | `CharacterFilter.Shortcut(int)` -> object id (items, not spells) |
| change notifications | `ChangeSpellbar` (`Tab`, `Slot`, `SpellId`), `ChangeShortcut`, `SpellbookChange`, `SpellCast` |

**Argument-order caveat.** The parameter order of `SpellTabAdd` and `SpellTabDelete` is
undocumented and cannot be settled from the assembly. `ChangeSpellbarEventArgs` carries
`Tab`, `Slot` and `SpellId`, which tells us the triple but not its order. Rather than guess,
`spellbar set` **writes then reads the bar back**, and if the spell did not land it retries
with the other plausible order and logs which one worked. The first live run therefore
settles it. `spellbar clear` does the same for the two-argument delete.

Note `Actions.CastSpell(int spellId, int targetId)` also exists. It is a client hook, so it
may well go through the native path too, but it is **untested here** and the hotkey route is
what the user specified. Worth a live comparison if the hotkey route proves awkward.

### The verbs

| command | effect |
| --- | --- |
| `spellbar clear` | remove every spell from the bar |
| `spellbar set <slot> <spellId>` | place a spell in a slot (slots are 1-based, 1 to 10) |
| `cast <slot>` | post that slot's hotkey, firing the native client cast |

Slots 1 to 9 map to their own digit key; slot 10 maps to the `0` key. These are main-row
digits, not the numpad, so none of them set the extended-key bit. An out-of-range slot is
refused rather than guessed at.

`spellbar set` warns (and proceeds) when the spell is not in the spellbook, so a rig sees
the discrepancy rather than a silently useless slot.

### `CastResult` record

Every `cast` emits one line into `chatlog_<pid>.jsonl`:

```json
{"utc":"...","source":"filter","type":"CastResult","slot":3,"outcome":"requested",
 "spellId":1234,"spellName":"spellId 1234","combatMode":"Magic","key":"Digit3"}
```

| field | meaning |
| --- | --- |
| `slot` | the requested 1-based slot |
| `outcome` | `requested`, `failed` or `invalidslot` |
| `spellId` / `spellName` | what the bar held in that slot, `null` if empty or unreadable |
| `combatMode` | the **pre-cast** combat mode |
| `key` | which hotkey was posted |

`combatMode` is there because a cast issued from `Peace` will not fire. It is the field that
shows that without a separate `dumpstate`.

As with `attack`, `outcome: "requested"` means **the input was posted**, not that a spell was
cast. Confirm the effect by target health, combat chat, or a `dumpstate` read.

### Prerequisites the rig must satisfy

These are **not** handled by the filter. A cast will silently do nothing if any is missing:

1. **Magic combat mode, which means wielding a caster FIRST.** Combat mode is derived from
   the wielded weapon (section 10): you get Magic by wielding a wand, staff or orb and then
   toggling into combat with Backtick. There is no "set magic mode" call, and asking for one
   while holding the wrong weapon is a request the client can only refuse. The keymap also
   binds `DIK_END` to `CombatCastCurrentSpell` in MagicCombat mode.
2. **A wand, staff or orb wielded.** Check `state.equipment` for a `WandStaffOrb`.
3. **A target selected** for damage spells. Use `appraise <name>` or check
   `state.selection`.
4. **The spell present in the spellbook.** `state.spellbar[].known` reports this per slot.
5. **Components**, which can be skipped server-side with `/requirecomps off`. Per ledger
   **L6-43** the foci turned out to be a component-list *selector*, not a gate, so a focus
   alone does not remove the component requirement.
