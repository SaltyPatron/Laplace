# Preserved branch history

inventory.json maps every original branch and pull-request head to its exact commit.
The parents of this archive commit retain complete Git histories and file trees.
This is a recovery snapshot, not a claim that all changes are active on main.

Restore a listed head with `git fetch origin refs/tags/branch-preservation/e270e10acb4d12462518` followed by
`git switch -c recovered-work <listed-commit-sha>`.
