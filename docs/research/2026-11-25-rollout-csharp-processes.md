# How rollout's C# can hold the lock, run process groups and keep signals

Research note, 2026-11-25, for mpaulosky/Application_Factory#157 (increment F3-0 of mpaulosky/Application_Factory#122, map mpaulosky/Application_Factory#93). It decides nothing;
ADR 0017's and ADR 0016's Updates for mpaulosky/Application_Factory#157 do. It asks what `auto_rollout.py` does with `fcntl.flock`, `pass_fds`, `start_new_session`, `waitid`'s
`WNOWAIT`, `os.killpg` and `signal.getsignal`, and how a .NET 11 file-based app does each.

**[V]** marks a fact verified here by experiment: Linux x64 (kernel 7.0, glibc 2.43, util-linux 2.41.3), SDK 11.0.100, each spike a file-based app in `/tmp/spike` with the
Factory's `global.json` and the three empty shields beside it, built with `dotnet build <file>.cs -o <folder>` and run as `dotnet <folder>/<file>.dll`. The spikes were thrown
away; the code each one ran is below, cut to what it shows. **[D]** marks a fact from source or documentation only. **[I]** is inference.

## Answer

- **[V] The lock: a `FileStream` with `FileShare.None` is `flock(LOCK_EX | LOCK_NB)`.** It shows in `/proc/locks` as `FLOCK ADVISORY WRITE`, `flock -n` on the file fails while it
  is held, and a second app's `FileShare.None` open fails with `IOException` (HResult 11, `EAGAIN`). So it excludes Python's `fcntl.flock` round as well (the same `flock(2)`).
- **[V] .NET opens it close-on-exec, so no child holds it.** A `Process.Start` child had no descriptor for it, and once the app was SIGKILLed the lock was free with the child
  still running. Clearing `FD_CLOEXEC` (`fcntl(fd, F_SETFD, 0)`) made the child hold it past the app's SIGKILL, but then every child `Process.Start` starts holds it, `gh` and `git`
  among them (and, **[I]**, anything they leave running, such as a `git gc --auto` that detaches), not only the groups `pass_fds` gives it to.
- **[V] `posix_spawnp`, called through `DllImport` from libc, gives one child the lock, a session of its own and a leader left unreaped.** `posix_spawn_file_actions_adddup2(fd, fd)`
  clears `FD_CLOEXEC` in that child alone (as POSIX.1-2024 says it must; here on glibc 2.43), so a spawned `sleep` held the lock after the app's SIGKILL, and the lock was free once its group
  was killed, while a `Process.Start` child of the same app had no descriptor for it. `POSIX_SPAWN_SETSID` made the child a session and group leader (pgid and sid its pid).
- **[V] .NET reaps every child `Process.Start` started, the `setsid` program's among them, and none it didn't.** `Process.Start("setsid", ...)` gave a group whose id is the
  child's pid (it doesn't fork, not being a group leader), but the leader was gone from `/proc` as soon as it exited, so `waitid(WNOWAIT)` found nothing (`ECHILD`) and the group's id
  was held only by what was left in it. A `posix_spawnp` leader stayed a zombie (`Z`) after it exited, even while a `Process.Start` child exited and was reaped meanwhile, and
  `waitid(P_PID, pid, WEXITED | WNOHANG | WNOWAIT)` read its exit code (3) and left it a zombie, so the group's id couldn't be another's until the app reaped it, after the kill.
  `kill(-pgid, SIGKILL)` through `DllImport` killed the group, as `os.killpg` does.
- **[V] `Process.Start` children start with SIGPIPE ignored.** The .NET runtime ignores SIGPIPE, and `Process.Start` passes that on: a child's `SigIgn` was `0x1000` (bit 13) where
  the shell's was 0. Python's `subprocess` puts SIGPIPE back to its default in a child (`restore_signals`). A `posix_spawnp` child with SIGPIPE in `posix_spawnattr_setsigdefault`
  and `POSIX_SPAWN_SETSIGDEF` started with `SigIgn` 0.
- **[V] An ignored SIGHUP stays ignored, and so does an ignored SIGINT; an ignored SIGTERM doesn't.** Under `nohup`, the app's `/proc/self/status` showed SIGHUP ignored and not caught as
  it started; a `PosixSignalRegistration` for it left it ignored (`SigIgn` unchanged), a SIGHUP sent to itself ran no handler and didn't end it, and a `Process.Start` child started
  with it ignored. Not ignored, the handler ran, and `Cancel = true` kept the app running. SIGINT ignored by the caller (`trap '' INT; exec ...`) behaved the same. SIGTERM ignored by
  the caller was caught (`SigCgt`) and no longer ignored by the time the app's first line ran, and the app's handler ran on a SIGTERM: the runtime takes SIGTERM over as it starts.
  The caller's ignored set can be read before it does, by the wrapper's shell from `/proc/$$/status` (`SigIgn`), which a later `exec dotnet` keeps (`0x4004` with SIGTERM ignored).
- **[V] `rollout.sh` has to run the built dll: through `dotnet run --file`, a SIGTERM to the wrapper didn't reach the app.** `exec dotnet run --file app.cs` leaves the CLI at the
  wrapper's pid, and the app runs as its child. In 8 runs out of 8 a SIGTERM to that pid ended the CLI (exit 143) and left the app running, its handler never called; a SIGHUP did
  the same (exit 129), and a SIGKILL left it running too. (One earlier run, 3 s after the start, did reach the app; 8 more at 4 s never did, so it can't be relied on.) With
  `dotnet build app.cs -o <folder>` first, then `exec dotnet <folder>/app.dll`, the app is the wrapper's pid: the SIGTERM ran its handler in 8 runs out of 8, the wrapper's exit
  was the app's, and a SIGKILL of the wrapper is a SIGKILL of the round, as for `python3` today.

## The spikes

### The lock

`lock.cs <fs|fs-inherit> <file>` takes the lock, starts `sleep 300` with `Process.Start`, prints both pids and waits; the shell then checks the lock with `flock -n`, a second
app, the child's `/proc/<pid>/fd`, and again after `kill -KILL` of the app and after the child ends.

```csharp
var fs = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);   // IOException, HResult 11, when held
if (args[0] == "fs-inherit") Native.fcntl((int)fs.SafeFileHandle.DangerousGetHandle(), 2 /* F_SETFD */, 0);
var child = Process.Start("sleep", "300")!;
```

| Mode | `flock -n` while held | Child holds the descriptor | After the app's SIGKILL, child running | After the child ends |
| --- | --- | --- | --- | --- |
| `fs` | busy | no | lock free | free |
| `fs-inherit` | busy | yes | lock held | free |

`spawnlock.cs <file>` takes the lock the same way and starts `sleep 300` with `posix_spawnp` (`POSIX_SPAWN_SETSID`, `adddup2(fd, fd)`) and `sleep 301` with `Process.Start`. The
spawned child held the descriptor and the `Process.Start` one didn't; after `kill -KILL` of the app the lock was busy, and after `kill -KILL -<spawned pid>` it was free.

### The process group

`group.cs <setsid|spawn> <lockfile>` starts `sh -c 'sleep 300 & ...; sleep 1; exit 3'` in a session of its own, starts and waits `true` with `Process.Start` meanwhile, sleeps
2.5 s, then reads the leader's state, `waitid(WNOWAIT)`, the group's live members (a `/proc` scan of `stat`'s pgid, zombies left out, as `group_alive` does), and kills the group.

```csharp
N.posix_spawnattr_setsigmask(attr, empty);                       // no signal blocked
N.posix_spawnattr_setsigdefault(attr, sigpipe);                  // SIGPIPE back to its default
N.posix_spawnattr_setflags(attr, 0x80 | 0x04 | 0x08);            // POSIX_SPAWN_SETSID | SETSIGDEF | SETSIGMASK
N.posix_spawn_file_actions_adddup2(acts, lockFd, lockFd);        // the lock, in this child only
N.posix_spawnp(out pid, "sh", acts, attr, ["sh", "-c", script, null], env);
N.waitid(1 /* P_PID */, pid, info, 4 | 1 | 0x01000000);          // WEXITED | WNOHANG | WNOWAIT
N.kill(-pid, 9);
```

| Mode | pgid, sid | Leader after it exited | `waitid(WNOWAIT)` | Members before, after the kill | Child's `SigIgn` | Child has the lock |
| --- | --- | --- | --- | --- | --- | --- |
| `setsid` (`Process.Start`) | its pid | reaped | -1, `ECHILD` | 1, 0 | `0x1000` | no |
| `spawn` (`posix_spawnp`) | its pid | zombie | 0, exit 3, still a zombie | 1, 0 | `0` | yes |

`posix_spawnattr_t` and `posix_spawn_file_actions_t` were 1024-byte unmanaged buffers, larger than glibc's x86-64 sizes (336 and 80 bytes, **[D]** `<spawn.h>`); `siginfo_t`'s
`si_code`, `si_pid` and `si_status` were read at offsets 8, 16 and 24 (**[D]** glibc's x86-64 layout).

### The signals

`hup.cs <respect|always>` reads `SigIgn` and `SigCgt` from `/proc/self/status`, registers a SIGHUP handler that cancels (`respect`: only when SIGHUP isn't ignored), sends itself a
SIGHUP, and reads a `Process.Start` child's `SigIgn`. `term.cs` does the same for SIGTERM and SIGINT.

| Run | Ignored as it starts | Handler | Outcome | Child |
| --- | --- | --- | --- | --- |
| `nohup dotnet hup.dll respect` | SIGHUP | none | still running | SIGHUP ignored |
| `nohup dotnet hup.dll always` | SIGHUP | registered | ran no handler, still running, still ignored | SIGHUP ignored |
| `dotnet hup.dll respect` | none | registered | handler ran, still running | not ignored |
| `trap '' TERM INT; exec dotnet term.dll` | SIGINT only (`SigIgn` `0x1002`) | both registered | TERM handler ran, INT's didn't | |
| `dotnet term.dll` | none | both registered | both ran | |

### The wrapper

`app.cs <marker>` prints its pid and its parent's, handles SIGTERM by writing the marker and exiting 143, and sleeps 30 s. `run.sh` runs it as `status.sh` runs `status.cs`
(`exec dotnet run --file`, with the same environment); `dll.sh` runs `dotnet build "$PWD/app.cs" -o "$PWD/out"` and then `exec dotnet "$PWD/out/app.dll"`. The shell sends
`kill -TERM` to the wrapper's pid 4 s after it started. `defer.cs` holds SIGTERM and SIGHUP (`Cancel = true`) and exits 7 five seconds after the first, to see whether the CLI waits.

| Wrapper | App's parent | SIGTERM reached the app | After the signal |
| --- | --- | --- | --- |
| `run.sh` (`dotnet run --file`) | `dotnet`, the wrapper's pid | 0 of 8 runs | CLI exited 143, app left running |
| `dll.sh` (built dll) | the caller (the app is the wrapper's pid) | 8 of 8 | the app's exit |
| `run.sh` with `defer.cs`, SIGTERM / SIGHUP / SIGKILL | `dotnet` | never | CLI exited 143 / 129 / 137 at once, app left running, nothing written |

## What this leaves to the port

- **[I]** The parts in `DllImport` (`posix_spawnp` and its attribute and file-action calls, `waitid`, `kill`) are small, but each struct size and constant above is glibc's on
  x86-64, and nothing here ran on another architecture or libc. `rollout.sh --auto` runs on Linux only (ADR 0016); a port that passes the structs as buffers larger than they can be
  and reads `siginfo_t` at its 64-bit offsets holds on 64-bit glibc, which the port's tests show on the runners it has. musl wasn't tried.
- **[I]** `Process.Start` stays right for the short calls (`gh`, `git`), whose output the round reads whole: a SIGPIPE they ignore turns into a write error they report, not a hang.
- **[V]** `dotnet build <file>.cs -o <folder>` builds a file-based app into a folder of the caller's choosing. Two wrappers building at once right after a change trip over each
  other's build (ADR 0017's research), as `status.sh` already says for itself.
