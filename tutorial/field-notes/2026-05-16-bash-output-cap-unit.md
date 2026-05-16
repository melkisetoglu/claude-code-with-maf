# 2026-05-16 — Bash output cap was named in bytes but measured chars; renamed to match

> Affected chapters: [Step 03 — approval gate](../03-approval-gate.md), [Step 04 — mutation tools](../04-mutation-tools.md)
> Code changes: `Tools/Bash.cs`, `tests/ClaudeChat.Tests/BashTests.cs`.

## Symptom

The constant was named `MaxOutputBytes`, the `[Description]` the model reads said "capped at 50KB", the truncation marker said "exceeded 50KB". The cap check was `output.Length + line.Length + 1 > MaxOutputBytes` — and `StringBuilder.Length` / `string.Length` return **UTF-16 code units**, not bytes. ASCII output the two are equal; `"あ"` is 1 char vs 3 UTF-8 bytes; `"😀"` is 2 chars vs 4 UTF-8 bytes. Multibyte output (CJK, emoji) was sent to the model at 2-3× the documented cap.

## Resolution

Renamed `MaxOutputBytes` → `MaxOutputChars`. Updated the `[Description]` string, the truncation marker, and the file header to say "50K characters" instead of "50KB". The cap logic was already in chars and now matches its name. New test (`Cap_measures_chars_not_bytes`) runs `yes 'あ' | head -n 20000` — ~40K chars / ~80K bytes — and asserts no truncation, pinning the unit to chars.

The alternative (byte-aware counting via `Encoding.UTF8.GetByteCount` + `Encoder.Convert` for partial truncation) was rejected as workshop-inappropriate complexity; chars are an adequate proxy for protecting model context.
