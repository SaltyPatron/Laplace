# Preserved branch history

inventory.json maps every original branch and pull-request head to its exact commit.
The parents of this archive commit retain complete Git histories and file trees.
This is a recovery snapshot, not a claim that all changes are active on main.

Restore a listed head with `git fetch origin refs/tags/branch-preservation/25a19122b460520b41ab` followed by
`git switch -c recovered-work <listed-commit-sha>`.
