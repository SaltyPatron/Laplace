# Preserved branch history

inventory.json maps every original branch and pull-request head to its exact commit.
The parents of this archive commit retain their complete Git histories and file trees.
This is a recovery snapshot, not an assertion that all changes are active on main.

Restore any listed head with `git fetch origin refs/tags/branch-preservation/a34ba02629ee481b0008` followed by
`git switch -c recovered-work <listed-commit-sha>`.
