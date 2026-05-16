# 2026-05-16 — SessionStore.SaveAsync wasn't atomic; Ctrl+C mid-write could drop a session

> Affected chapters: [Step 00 — baseline](../00-baseline.md), [Step 09 — streaming polish](../09-streaming-polish.md)
> Code changes: `Persistence/SessionStore.cs`, `tests/ClaudeChat.Tests/SessionStoreTests.cs`.

## Symptom

`SaveAsync` wrote directly to `sessions/<id>.json` via `File.WriteAllTextAsync`, which opens with `FileMode.Create` — truncate first, then stream the bytes. A SIGKILL or Ctrl+C between truncate and final flush left a zero-byte (or partial) file. `Enumerate()` silently swallowed the parse error and the session disappeared from `--list`. This directly contradicted the architecture note's claim that per-turn saves protect against Ctrl+C.

## Resolution

Write to `<id>.json.tmp`, then `File.Move(tmp, final, overwrite: true)` — atomic on POSIX same-filesystem moves. `EnsureDir` sweeps orphan `.json.tmp` files at startup. New unit test (`EnsureDir_sweeps_orphan_tmp_files`) covers the sweep.
