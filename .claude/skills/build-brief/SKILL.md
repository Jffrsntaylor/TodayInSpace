---
name: build-brief
description: Build a task brief from .claude/briefs end to end — branch, implement, test, commit, push, open a PR. Use when Jeff says "build the <name> brief" or runs /build-brief.
argument-hint: <brief-name>
disable-model-invocation: true
---

Build the brief at `.claude/briefs/$ARGUMENTS.md`. If no name was given, list the files in `.claude/briefs/`
and ask Jeff which one.

1. **Read** the brief, then `CLAUDE.md` rules. Run `git status`. If there are uncommitted changes, stop and ask Jeff.
2. **Sync**: `git switch main`, then `git pull`. Create the branch named in the brief (default `feature/$ARGUMENTS`).
3. **Plan** in a few bullets: which files change and which tests you'll add. Show Jeff and wait for a go-ahead if
   the brief says `confirm-plan: yes`. Otherwise proceed.
4. **Implement** only what the brief asks. Match the existing style. Add or adjust xUnit tests for any new logic.
5. **Verify**: `dotnet build TodayInSpace.slnx` and `dotnet test TodayInSpace.slnx` must both pass. Fix failures.
   Never skip or delete tests to get green.
6. **Check every item** in the brief's "Done when" list. Say which ones you could not verify locally,
   for example visual checks.
7. **Commit** with a short imperative subject (e.g. "Add previous/next day arrows to the archive")
   and a body explaining why.
8. Run `/ship` to push and open the PR.
