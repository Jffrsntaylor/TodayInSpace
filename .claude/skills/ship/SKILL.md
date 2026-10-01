---
name: ship
description: Push the current branch and open a pull request to main with a clear description. Use after work is committed and tests pass.
disable-model-invocation: true
---

1. Confirm you're **not on `main`**. If you are, stop and tell Jeff.
2. Run `dotnet test TodayInSpace.slnx` one more time. Don't ship red.
3. `git push -u origin HEAD`.
4. Open a PR into `main`. If the `gh` CLI is available, use `gh pr create --base main --fill-first` and then edit
   the body. Otherwise, give Jeff the compare link: https://github.com/Jffrsntaylor/TodayInSpace/compare/<branch>?expand=1
5. PR body:
   - **What**: one or two sentences in plain English. Jeff and recruiters read these.
   - **Why**
   - **How to check it**: what to click on the live site after deploy
   - **Tests**: what was added
   - **Noticed, not changed**: anything out of scope (omit if none)
6. Report back with the PR link. Jeff reviews and merges. Never merge it yourself.
