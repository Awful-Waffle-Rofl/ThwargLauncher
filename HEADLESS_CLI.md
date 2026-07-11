# Headless command-line launch

ThwargLauncher can launch a single AC client from the command line with **no
window**, then exit. This is meant for scripted / automated test iterations
(e.g. "start the local server, then log a specific character into it").

It reuses the launcher's existing configuration — the same server list
(`%AppData%\ThwargLauncher\Servers\UserServerList.xml`), account file
(`%AppData%\ThwargLauncher\Accounts.txt`) and Decal/ThwargFilter injection path
that the GUI uses. So character auto-login works exactly as it does in the UI.

## Usage

```
ThwargLauncher.exe launch --server <name> --account <name> [--character <name>]
                          [--client <path>] [--rodat on|off] [--simple]
                          [--keep-client|--no-keep-client] [--timeout <sec>]
```

| Argument | Meaning |
|---|---|
| `--server`, `-s` | Server name as it appears in the launcher's server list (**required**). |
| `--account`, `-a` | Account name as it appears in the account list (**required**). |
| `--character`, `-c` | Character to auto-select. Requires Decal/ThwargFilter. Omit to stop at char-select. |
| `--client` | Full path to `acclient.exe`. Overrides the account's LaunchPath and the global AC-location setting. |
| `--rodat on\|off` | Override the server's rodat setting. |
| `--simple` | Just spawn the client — no injection, no waiting, no character auto-login. |
| `--keep-client` | Do not kill the client if login times out (this is the default). |
| `--no-keep-client` | Allow the client to be killed on a failed/timed-out login (GUI default behavior). |
| `--timeout <sec>` | Seconds to wait for login before giving up (inject mode only). |

Exit codes: `0` success, `2` bad args, `3` unknown server, `4` unknown account,
`5` no/invalid client exe, `6` launch failed, `1` other error.

## Example

```powershell
# Log the character "My Toon" into the local dev server (WaffleHouse = 127.0.0.1:9000)
ThwargLauncher.exe launch --server WaffleHouse --account MyAcct --character "My Toon"
```

Output is written to the parent console (the process attaches to it), so it is
scriptable:

```
Launching 'My Toon' on 'WaffleHouse' (127.0.0.1:9000) as account 'MyAcct' [inject] [keep-client]...
  Waiting for game: 3/40 sec
OK PID=12345
```

## Notes

* **Decal is required for character auto-login.** Without it the tool warns and
  falls back to `--simple` (client opens at the login/character-select screen).
* This verb never shows the WPF window and does not start the server-monitor or
  auto-relaunch machinery — it launches one client and exits.
* Stopping/starting the local ACE server is intentionally out of scope here;
  drive that separately (kill/start `ACE.Server`, wait until `127.0.0.1:9000`
  accepts connections) and then invoke this command as the final step.

## Related: existing GUI command-line switches

The GUI already honored these (see `AppCoordinator.ParseCommandLine`):
`-Profile <name>`, `-AutoRelaunch <bool>`, `-NeverKillClients <bool>`,
`-AutoLaunchOnStart <bool>`. Note `-AutoLaunchOnStart` was previously a no-op
(it set a field that nothing read); it now persists to settings like the others,
so `ThwargLauncher.exe -Profile X -AutoLaunchOnStart true` will auto-launch a
whole profile through the normal windowed path.
