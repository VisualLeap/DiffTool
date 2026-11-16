# Agent guidance

## General working preferences

- Discuss proposals before implementing them; an explicit request to implement authorizes that work without another confirmation.
- Let the user lead open-ended cleanup discussions; do not invent a large batch of unrelated decisions.
- Challenge suggestions when there is a concrete downside, explain it, then respect the user's decision.
- Explain concepts in plain language; distinguish observations, hypotheses, estimates and verified results.
- Build the conceptual model first when debugging; find the root cause and try to falsify the hypothesis before calling it confirmed.
- Keep documentation and code comments concise; explain necessary reasoning, not change history or obvious code.
- Do not add permanent tests or test infrastructure without the user's agreement; use existing checks and temporary offline probes when appropriate.
- Build changes and let the user run the app for visual testing unless they explicitly authorize agent-run testing; do not close their running app.
- Explain actual costs and tradeoffs; do not dismiss work as negligible or claim measured performance from an estimate.
- Search within the project first; ask for a location rather than searching unrelated filesystem roots or home directories.
- Use structured patches for source/configuration edits.
- Preserve unrelated working changes; do not stash, reset or otherwise move the user's uncommitted work.
- Commit or push only when requested, after the full commit message is agreed; use that message exactly and omit agent attribution trailers.
- Prefer small, settled changes; squash unpublished task commits into one for submission unless instructed otherwise.
- "Submit" means commit and push the agreed change; describe the final behavior in the message rather than the implementation journey.

## DiffTool

- [README.md](README.md) is the description of user-visible behavior; update its relevant bullet when behavior changes.
- The app is a read-only viewer: never add Git commands that stage, modify, discard, commit or check out.
- Build with `build.bat`; `build.bat -Test` also builds and runs `tests\Tests.cs`. There is no project file or package restore.
- The compiler is the .NET Framework `csc.exe` (C# 5): no string interpolation, `?.`, `nameof`, expression-bodied members, `out var` or tuples.
- Source files are listed explicitly on the compiler line in `build.bat`; add new files there.
- If the user's running app locks `bin\DiffTool.exe`, report that they should close it and rerun `build.bat` rather than closing it or retrying. To verify meanwhile, compile to a temporary folder outside the project using the same compiler arguments.
- The tests open GUI windows and write `bin\smoke-test.png`.
- When the console code page is UTF-8, .NET Framework sends a byte-order mark to a child's redirected stdin at process start. Give Git its input as arguments where possible; otherwise go through `GitRepository.Run`, whose line-based input tolerates the mark.
- Run the tests under both `chcp 65001` and `chcp 437` after changing how Git is invoked.
- The UI is custom-painted WinForms: paint only visible rows, keep scrolling direct and pixel-level with no animation, and take colors from `Theme` in `Program.cs`.
- Push to GitHub `main`, never a feature branch; fetch first and preserve unfinished local work while integrating published changes.
- Keep temporary probes and diagnostics under `bin/`, which is excluded from Git; do not commit generated output, local settings or credentials.

## Shared guidance

- This file is the canonical, provider-neutral guidance for this checkout; use it from Codex, Claude Code and other agents.
- Read applicable `CLAUDE.md` guidance if present, but maintain shared rules here rather than creating duplicate provider-specific instruction files.
- Record durable findings as concise rules in the DiffTool section, replacing superseded ones; do not append session history.
- Do not use a provider's private auto-memory directory as a second project knowledge store.
- Treat guidance as context, not proof: verify file paths and technical claims against the checkout; current user instructions take precedence.
- Always include pending `AGENTS.md` changes in the current commit; guidance updates alone do not authorize a separate commit or push.
