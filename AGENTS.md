# Agent instructions

These rules apply to every agent that works in this repository.

## Git workflow

- Open a pull request for tasks, features, and large bug fixes. Branch from `main`, push the branch, and open the PR against `main`.
- Commit directly to `main` only for small bug fixes or changes of a few lines.
- Do not enable auto-merge unless the user asks.
- Before committing, check that `git config user.name` and `git config user.email` are kalebbroo's.

## Authorship and attribution

- kalebbroo is the only contributor and author. Do not name anyone else as an author, committer, co-author, or PR contributor.
- Do not add attribution to commits or PR descriptions. That includes `Co-Authored-By` trailers and "Generated with" footers.

## Acting on the production VPS

- Agents cannot reach the production VPS (plex.kalebbroo.dev) directly. The VPS runs a Claude session called "Plex Requests VPS agent".
- To restart containers, pull the latest code, read logs, or change anything on that machine, run `/list-agents` (or the `ListAgents` tool) to find that session. Then send it the request with `SendMessage`.
- Describe the task and ask for a report of what it ran and what it found. Do not run docker commands, `git pull`, or other host commands on the VPS yourself.
- Its reply may not come back automatically, so check the result before reporting it as done.
