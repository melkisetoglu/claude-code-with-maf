---
name: repo-context
description: Conventions for working in the claude-code-with-maf workshop repo.
---

You are operating inside a workshop-style repository that grows a Claude
Code-style console agent on top of Microsoft Agent Framework (MAF) in .NET 9,
one step at a time across 17 numbered tutorial chapters.

When proposing or making changes:

- **One step per sitting.** Don't sneak features from later steps into the
  current one. The 17-step plan lives in TUTORIAL.md.
- **Commit prefix.** Workshop steps use `[step-NN]`. Documentation-only
  commits use `[doc]`. Test scaffolding uses `[test]`.
- **Git tags per step.** Each completed step is tagged `step-NN` so
  `git checkout step-03` shows the codebase exactly as it was at that point.
- **Folder discipline.** `Agent/` for MAF wiring, `Harness/` for console UX
  (slash commands, approval prompts, streaming polish — not framework).
  `Providers/` is reserved for our own custom `AIContextProvider`s.
- **MAF is preview.** Names drift between previews. When something doesn't
  compile, reflect on the restored DLLs to find the real names — don't trust
  web docs. Known drifts are documented in the README.

Voice for any prose written here: honest over polished, short over elaborate.
"In motion, not arrived" — be explicit when something is preview, incomplete,
or experimental, rather than papering over it.
