# Agent teams: master reference

A working reference for building agent teams in Claude Code: when to use one, how to set it up, how to brief it, and what goes wrong.
Compiled on 2026-11-24 from the [agent teams page](https://code.claude.com/docs/en/agent-teams), the
[hooks reference](https://code.claude.com/docs/en/hooks) and the [costs page](https://code.claude.com/docs/en/costs).
Agent teams are **experimental**; check those pages again before relying on a version-specific detail.

## Contents

1. [Decide: team, subagents or one session](#1-decide-team-subagents-or-one-session)
2. [Enable](#2-enable)
3. [How a team is built](#3-how-a-team-is-built)
4. [Start and drive a team](#4-start-and-drive-a-team)
5. [Models and effort](#5-models-and-effort)
6. [Permissions and trust](#6-permissions-and-trust)
7. [Quality gates with hooks](#7-quality-gates-with-hooks)
8. [Reusable roles: subagent definitions](#8-reusable-roles-subagent-definitions)
9. [Design rules for an efficient team](#9-design-rules-for-an-efficient-team)
10. [Prompt playbook](#10-prompt-playbook)
11. [Costs](#11-costs)
12. [Troubleshooting](#12-troubleshooting)
13. [Limitations](#13-limitations)
14. [Using teams in the Application Factory](#14-using-teams-in-the-application-factory)
15. [Pre-flight checklist](#15-pre-flight-checklist)

## 1. Decide: team, subagents or one session

Pick the lightest option that does the job. A team costs the most tokens and adds coordination overhead.

| | One session | Subagents | Agent team |
| :- | :- | :- | :- |
| Context | One window | Own window; result returns to caller | Own window per teammate; fully independent |
| Communication | n/a | Returns a result; named subagents can also message each other | Teammates message each other directly, by name |
| Coordination | You | Main agent manages all work | Self-coordination by messages plus a shared task list |
| Best for | Sequential work, same-file edits, many dependencies | Focused tasks where only the result matters | Work that needs discussion, challenge and hand-offs |
| Token cost | Lowest | Lower: results summarized back | Highest: each teammate is a separate Claude instance |

Use a team for:

- **Research and review**: several teammates investigate different aspects, then share and challenge findings.
- **New modules or features**: each teammate owns a separate piece.
- **Debugging with competing hypotheses**: teammates try to disprove each other's theories; the survivor is more likely the root cause.
- **Cross-layer changes**: frontend, backend and tests, each owned by a different teammate.

Don't use a team for sequential tasks, edits to the same file, work with many dependencies, or routine tasks. If the parallel workers only
need to report back, use subagents. If you only need to pass findings between sessions you run yourself, use
[cross-session messaging](https://code.claude.com/docs/en/cross-session-messaging.md).

## 2. Enable

Set `CLAUDE_CODE_EXPERIMENTAL_AGENT_TEAMS=1` in the shell or in a settings file:

```json
{
  "env": {
    "CLAUDE_CODE_EXPERIMENTAL_AGENT_TEAMS": "1"
  }
}
```

Side effects to know:

- **Teams can form without being asked.** While enabled, any Agent tool call with a `name` launches a teammate (unless it's a fork or passes
  `isolation`), and Claude names ordinary subagents on its own so it can message them. Claude Code doesn't ask you to confirm the launch.
- **Interactive only.** With `-p` (headless, including Agent SDK sessions) Claude doesn't spawn teammates; a named subagent runs as an ordinary subagent.
- **Turning it off:** set the variable to `0` in a settings file. Saved settings `env` values apply to the running session at once. A `0` in user
  settings beats a shell export, but project settings, local settings, `--settings` and managed settings apply later and can turn it back on.

## 3. How a team is built

| Component | Role |
| :- | :- |
| Team lead | The main session. Spawns teammates and coordinates. Fixed for the session's lifetime. |
| Teammates | Separate Claude Code instances, each on assigned tasks |
| Task list | Shared work items: pending, in progress, completed; with dependencies |
| Mailbox | Messaging between agents |

Files on disk (the team name is `session-` plus the first eight characters of the session ID):

| Path | What | Lifetime |
| :- | :- | :- |
| `~/.claude/teams/{team}/config.json` | Runtime state: `members` array (name, agent ID, agent type; the lead is `team-lead`), session and tmux pane IDs | Removed when the session ends |
| `~/.claude/teams/{team}/inboxes/{agent}.json` | Each agent's mailbox | Removed with the team directory |
| `~/.claude/tasks/{team}/` | The task list | Kept; pruned per `cleanupPeriodDays` |

- Don't edit or pre-author `config.json`; it's overwritten. A project file such as `.claude/teams/teams.json` is **not** config. Define reusable roles
  as subagent definitions instead (section 8).
- A message counts as sent only when the write to the recipient's mailbox succeeds; otherwise the sender gets an error.
- Malformed mailbox entries are reported and removed; valid ones are still delivered.
- Task claiming uses file locking, so two teammates can't claim the same task.
- Completing a task automatically unblocks tasks that depend on it.

What a teammate knows when it starts:

- It loads the same project context as a normal session: `CLAUDE.md`, MCP servers, skills (restricted to the lead's `--setting-sources` if given).
- It gets the **spawn prompt** and nothing else from the lead. **The lead's conversation history does not carry over.**
- It can read the team config to discover the other members.

How information moves:

- Messages are delivered automatically. There is no broadcast: one message per recipient.
- When a teammate stops it notifies the lead with its final answer, or with the error text if its turn ended on an API error.
- Agents with the Task tools (`TaskCreate`, `TaskGet`, `TaskList`, `TaskUpdate`) see and claim tasks; others coordinate by message only.
- A message from another agent is marked as coming from another Claude session, not from you (see section 6).

## 4. Start and drive a team

Ask in natural language, naming the roles. For predictable addressing, name each teammate yourself:

```text
Create an agent team. Spawn three teammates named ux, architect and skeptic to explore
a CLI that tracks TODO comments: ux on the user experience, architect on the technical
design, skeptic playing devil's advocate. Have them challenge each other, then synthesize.
```

If Claude used subagents instead (they share the same panel, so look closely), ask again and say "agent team" explicitly.

### Display modes

| Mode | How | Needs |
| :- | :- | :- |
| In-process | All teammates in your terminal, listed in the agent panel under the prompt | Any terminal |
| Split panes | One pane per teammate | tmux, or iTerm2 with the `it2` CLI and the Python API enabled |

Teammates run in-process until you set `teammateMode`. Set it in `~/.claude/settings.json` (or `claude --teammate-mode <mode>` for one session;
the flag is experimental and not in `--help`):

- `"auto"`: split panes inside tmux or iTerm2, in-process elsewhere.
- `"tmux"`: panes in a separate tmux session even outside tmux; run the command on the `View teammates` line in a second terminal to watch.
- `"iterm2"`: iTerm2's own panes; teammates start only in iTerm2 with `it2` set up.

### Keys (in-process)

| Key | Action |
| :- | :- |
| Up / Down | Select a teammate in the agent panel |
| Enter | Open its transcript and message it directly; on a collapsed `N idle agents` row, expand it |
| Escape | Clear the selection; while viewing a teammate, interrupt its current turn |
| `x` | Stop the selected teammate |
| Ctrl+T | Toggle the task list |
| Shift+Tab | While viewing a teammate, cycle its permission mode (applies at its next turn) |

Panel behaviour: idle rows hide 30 seconds after every agent is idle and come back on the teammate's next turn (it's still running and
addressable). More than three idle teammates collapse into one `N idle agents` row.

While viewing a teammate, plain text and skills go to the teammate; built-in commands go to the lead. `/compact`, `/clear` and `/rewind` ask for
confirmation; `/model` and `/fast` are refused there, because a teammate's model and fast mode are fixed at spawn.

### Tasks

- The lead assigns tasks, or a teammate self-claims the next unassigned, unblocked task when it finishes one.
- Teammates don't self-claim a blocked task; you or the lead can still assign it.

### Plan first, then implement

Put the lead in plan mode, then spawn the teammate: it works in plan mode until its plan is ready, then sends a plan approval request.

- If you haven't changed the teammate's permission mode since spawn, the plan is **approved automatically**, with no review by the lead.
- If you put a running in-process teammate in plan mode with Shift+Tab, the **lead** reviews that plan and approves or sends it back. You aren't asked.
- After approval the teammate takes the lead's current mode (Manual if the lead is in plan mode).

To get a real review of plans, say in the spawn prompt that the lead must check each plan against stated criteria before approving.

### Shut down

```text
Ask the researcher teammate to shut down
```

The teammate approves (exits gracefully, after its current request or tool call) or rejects with a reason. Shut teammates down as soon as their
work is done: an idle teammate still costs tokens. Team directories are cleaned up when the session ends; there's no separate cleanup step.

## 5. Models and effort

A teammate's model is the first of these that applies:

1. The model the spawn prompt names for that teammate.
2. For a teammate from a subagent definition, the definition's `model` (`inherit` means the lead's model).
3. `CLAUDE_CODE_SUBAGENT_MODEL`, unless it's `inherit`.
4. The lead's current model.

- A mod's `agent.spawn` hook that sets a model replaces source 1.
- `CLAUDE_CODE_SUBAGENT_MODEL_FORCE=1` (v2.1.257+) skips sources 1 and 2.
- `teammateDefaultModel` was removed in v2.1.234 and is ignored.
- If the org's `availableModels` blocks the choice, a family alias such as `opus` moves to the newest permitted version (Anthropic API and
  Claude Platform on AWS); anything else falls back to the lead's model.
- A teammate on the lead's model inherits the lead's effort level (split panes from v2.1.186). For other models see
  [effort for subagents and teammates](https://code.claude.com/docs/en/model-config.md#effort-level-of-subagents-and-teammates).

Rule of thumb: Sonnet for teammates doing bounded work (the costs page recommends it), the strongest model for the lead and for any teammate whose
judgment the result rests on (architecture, security review, the final synthesis).

## 6. Permissions and trust

- A new teammate starts in the **lead's mode**, including `bypassPermissions`. You can't set a per-teammate mode at spawn.
- If the lead is in `dontAsk`, teammates start in Manual (split panes need v2.1.317+ for this).
- An in-process teammate not in plan mode runs in the lead's mode while the lead is in `acceptEdits`, `auto` or `bypassPermissions`, even if its
  status bar says Manual. To make it ask, switch the lead to Manual too (Enter on the `main` row, then Shift+Tab).
- Teammate permission prompts appear in the **lead's** session. Pre-approve common commands under `permissions.allow` before spawning, or prompts pile up.
- In-process teammates follow the lead's live allow and deny rules, including `--allowedTools`, `--disallowedTools` and `--tools`.
- Split-pane teammates get `--disallowedTools` and `--tools` (v2.1.309+) but **not** `--allowedTools`. Pre-approve for them through
  `--settings`, for example `claude --settings '{"permissions":{"allow":["Bash(npm *)"]}}'`.

Trust boundary between agents:

- A message from another agent is never your consent. A teammate can't approve a prompt for you, and a denied teammate can't get another teammate
  to do the action instead.
- In auto mode the classifier treats relayed "approval" as untrusted and reviews `SendMessage` messages before delivery; a blocked message isn't delivered.
- When you design a team, keep approval with the human. Never write a spawn prompt that tells a teammate to act on another teammate's say-so for
  something destructive or outward-facing.

## 7. Quality gates with hooks

Three hook events serve teams. None supports matchers; each fires on every occurrence. All receive the common input fields
(`session_id`, `transcript_path`, `cwd`, `permission_mode`, `hook_event_name`, `agent_id` from v2.1.290).

| Event | Fires when | Extra input | Block with |
| :- | :- | :- | :- |
| `TeammateIdle` | A teammate is about to go idle after its turn | `teammate_name`, `team_name` (deprecated) | Exit 2 (stderr is fed back) or `{"decision":"block","reason":"..."}`: the teammate keeps working |
| `TaskCreated` | A task is created through `TaskCreate` (not in sessions without the Task tools) | `task_id`, `task_subject`, optional `task_description`, `teammate_name`, `team_name` | Exit 2 or a block decision: the task is deleted and the message returned as the tool's error |
| `TaskCompleted` | A task is marked completed through `TaskUpdate`, or a teammate ends its turn with tasks in progress | Same as `TaskCreated` | Exit 2 or a block decision: the task stays open and Claude keeps working |

`{"continue": false, "stopReason": "..."}` lets a teammate go idle even if another hook blocks (`TeammateIdle`, and `TaskCompleted` at the end
of a turn). `TaskCreated` ignores it, and so does `TaskCompleted` on a `TaskUpdate` call.

Example: refuse to close a task while tests fail.

```bash
#!/bin/bash
INPUT=$(cat)
TASK_SUBJECT=$(echo "$INPUT" | jq -r '.task_subject')

if ! npm test 2>&1; then
  echo "Tests not passing. Fix failing tests before completing: $TASK_SUBJECT" >&2
  exit 2
fi

exit 0
```

Example: require a ticket number in every task subject.

```bash
#!/bin/bash
INPUT=$(cat)
TASK_SUBJECT=$(echo "$INPUT" | jq -r '.task_subject')

if [[ ! "$TASK_SUBJECT" =~ ^\[TICKET-[0-9]+\] ]]; then
  echo "Task subject must start with a ticket number, e.g. '[TICKET-123] Add feature'" >&2
  exit 2
fi

exit 0
```

Good gates: a fast lint or test run on `TaskCompleted`, an "output file exists" check on `TeammateIdle`, a format rule on `TaskCreated`.
Keep them quick: they run on every event, for every teammate.

## 8. Reusable roles: subagent definitions

Define a role once as a subagent (`.claude/agents/<name>.md` in the project, `~/.claude/agents/` for the user, or from managed settings or a plugin)
and spawn teammates from it:

```text
Spawn a teammate using the security-reviewer agent type to audit the auth module.
```

| Field | In-process teammate | Split-pane teammate |
| :- | :- | :- |
| `tools` | Limits tools; `SendMessage` and the Task tools are added | Limits tools |
| `disallowedTools` | Removed, except `SendMessage` and the Task tools | (not stated) |
| `model` | Used unless the spawn prompt names one | Same |
| `effort` | Applied | (not stated) |
| `autoCompactWindow` | Applied (v2.1.296+) | Ignored |
| Body | **Appended** to the default system prompt | **Replaces** the default system prompt |
| `skills` | Not applied; loads project and user skills | Not applied |
| `mcpServers` | Ignored; loads project and user MCP servers | Applied |

- A teammate that stopped is brought back when Claude messages it again, with its saved conversation. A project `.claude/agents/` definition is
  re-applied only if you trusted the folder the agent file is in (trusting a parent doesn't count); otherwise it comes back with only the
  tools Claude Code adds to every teammate.
- Since the body replaces the whole system prompt in split panes, write definition bodies that stand alone: role, scope, deliverable, rules.

## 9. Design rules for an efficient team

**Size the team.**

- Start with **3 to 5** teammates. Fifteen independent tasks still start with about three teammates.
- Three focused teammates often beat five scattered ones. Cost scales linearly; coordination overhead grows faster; returns diminish.

**Size the tasks.**

- Aim for **5 to 6 tasks per teammate**, each a self-contained unit with a clear deliverable: a function, a test file, a review.
- Too small and coordination costs more than the work; too large and a teammate runs long without check-ins, wasting effort if it's wrong.
- If the lead makes too few tasks, ask it to split the work further.
- Encode order as task dependencies, not as instructions to wait.

**Partition ownership.**

- Give each teammate **a distinct set of files**. Two teammates editing one file overwrite each other.
- For code, give teammates separate worktrees or separate directories, and name the one teammate who integrates.

**Brief each teammate fully.** The spawn prompt is all it knows. Include:

1. The goal and why it matters.
2. Exact scope: files, directories, the boundary it must not cross.
3. Relevant facts the lead already learned (the history doesn't carry over).
4. The deliverable and its format (for example "findings with severity ratings", "a patch plus a passing test").
5. Who to message and when: peers to challenge, the lead to report to.
6. What "done" means, and the check to run before marking the task complete.

Keep it focused: every word in the spawn prompt is in the teammate's context from the start.

**Make the structure adversarial where judgment matters.** Teammates that try to disprove each other avoid the anchoring a single investigator falls into.

**Keep the lead leading.** If the lead starts doing the work itself, tell it: `Wait for your teammates to complete their tasks before proceeding`.
If it declares the team done early, tell it to keep going.

**Monitor and steer.** Check transcripts, redirect what isn't working, synthesize as results arrive. Don't leave a team unattended for long.

**Start with read-only work.** Reviews, research and investigation have clear edges and no file conflicts; move to code-writing teams after.

## 10. Prompt playbook

### Parallel review

```text
Create an agent team to review PR #142. Spawn three teammates on Sonnet:
- security: auth, injection, secrets, permissions
- performance: hot paths, allocations, N+1 queries
- coverage: missing tests and weak assertions
Each reports findings with file:line and a severity (Critical, Warning, Advisory).
Message each other when a finding crosses lenses. Wait for all three, then
deduplicate and synthesize one report.
```

### Competing hypotheses

```text
Users report the app exits after one message instead of staying connected.
Spawn 5 teammates, each owning one hypothesis. Have them talk to each other to
try to disprove each other's theories, like a scientific debate. Update the
findings doc with whatever consensus emerges, with the evidence for it.
```

### Feature across layers

```text
Create an agent team to add <feature>. Teammates:
- api: owns src/api/** only
- ui: owns src/web/** only
- tests: owns tests/** only, writes tests from the spec before the others finish
Create tasks with dependencies so ui depends on the api contract task.
Nobody edits another teammate's files; message the owner instead.
A task is complete only when its tests pass.
```

### Plan-gated refactor

```text
(Lead in plan mode.) Spawn an architect teammate to plan the refactor of the
authentication module. Before approving its plan, check that it keeps the public
API unchanged and includes a migration test; send it back otherwise.
```

### Research spike

```text
Spawn three researcher teammates: one on library A, one on library B, one on
building it ourselves. Each answers: fit, maintenance, licence, risks, with
sources. Then have them critique each other's conclusions before you summarize.
```

## 11. Costs

- Each teammate is a full Claude instance: usage scales with the number of active teammates and how long each runs. The costs page puts teams at
  roughly 7x a standard session when teammates run in plan mode.
- Cut cost by: Sonnet for teammates, small teams, short focused spawn prompts, and shutting teammates down when done.
- An in-process teammate's prompt cache holds 5 minutes by default, even on a subscription. Set `subagentPromptCacheTtl` to `1h` for long pauses
  between turns; 1-hour cache writes bill at a higher rate.
- Spend the tokens on research, review and new features; do routine work in one session.

## 12. Troubleshooting

| Symptom | Cause and fix |
| :- | :- |
| No teammates appear | In-process: look in the agent panel and use the arrows. Claude may have judged the task too small; ask for an agent team explicitly. |
| A teammate vanished | Idle rows hide after 30 seconds, or collapse into `N idle agents`. Message it by name to bring it back. |
| Split panes don't open | `which tmux`; for iTerm2 check `it2` and the Python API. Try `teammateMode` `"tmux"` or `"iterm2"`. |
| Teammates spawn when you wanted subagents | Set `CLAUDE_CODE_EXPERIMENTAL_AGENT_TEAMS` to `0` (check higher-precedence settings and managed settings). |
| Too many permission prompts | Pre-approve common commands in permission settings before spawning (and in `--settings` for split panes). |
| A teammate stopped early | Open its transcript, give it more instructions, or spawn a replacement. A message wakes one waiting on an API retry. |
| The lead stopped early | Tell it to keep going. |
| A task stays "in progress" and blocks others | Task status can lag: check the work, update the status, or have the lead nudge the teammate. |
| Lead messages teammates that don't exist after `/resume` | In-process teammates aren't restored; tell the lead to spawn new ones. |
| Orphaned tmux session | `tmux ls`, then `tmux kill-session -t <session-name>`. |

## 13. Limitations

- `/resume` and `/rewind` don't restore in-process teammates.
- Task status can lag; shutdown can be slow (teammates finish the current call first).
- One team per session, scoped to that session; no named teams, no sharing across sessions.
- No nested teams: only the lead spawns teammates.
- An in-process teammate's own subagents run in the foreground; `background: true` definitions error and `run_in_background: true` fails or runs in the foreground.
- The lead is fixed for the session; leadership can't move.
- No per-teammate permission mode at spawn.
- Split panes need tmux or iTerm2.

## 14. Using teams in the Application Factory

[`CLAUDE.md`](../CLAUDE.md) holds this repo's rules, in its "Git workflow" and "Working on the Template" sections, and it wins wherever this
section differs. What they mean for a team:

- **Enable teams in your own settings only**: `~/.claude/settings.json` or the git-ignored `.claude/settings.local.json`, never the checked-in
  `.claude/settings.json`. While teams are on, any named Agent call can launch a teammate, the reviewers `pre-push-review` starts included, so
  turning it on for every contributor changes how those skills run. The issue worker (`claude-issues.yml`, ADR 0018) runs non-interactively,
  so it never spawns teammates.
- **One branch and worktree per writing teammate.** `pre-commit` refuses commits in the primary checkout. Give each teammate its exact branch and
  folder in the spawn prompt: the branch follows CLAUDE.md's names (`feature/{issue}-{slug}`, `fix/{issue}-{slug}`, `hotfix/{issue}-{slug}` or
  `chore/{slug}`), and the folder is the branch with `/` as `-`, made with CLAUDE.md's `git worktree add` command.
- **Each branch is pushed, gated and reviewed on its own.** `pre-push` refuses a bad branch name, a dirty tree or a branch behind `origin/main`,
  and the gate refuses a branch without its own local review (ADR 0010). Have the lead do the pushes, since teammates' permission prompts
  surface there anyway.
- **Keep each pull request within CLAUDE.md's size limit.** Split the work into issues ordered with "blocked by" links and map each to a teammate
  or task, rather than one large team PR.
- **One owner per Template file.** Two teammates must not edit the same file under `template/`, and the one editing an Owned file runs
  `scripts/gate.sh --sync` in its own worktree.
- **Done means the quick gate passed.** `scripts/gate.sh --quick` takes about a minute and checks the tree it runs in, so make it the done check
  each spawn prompt names, run by the teammate in its own worktree before it marks a task complete. Keep a `TaskCompleted` hook to a narrow,
  fast check that first changes to the hook input's `cwd`. Registering a team hook means changing the checked-in `.claude/settings.json`,
  which holds only the edit-time lint hook (ADR 0011), so do it on purpose, in its own pull request.
- **Markdown and YAML with Write and Edit only**, so the edit-time lint hook sees them. Put this in every spawn prompt for a writing teammate.
- **Review lenses map well to a review team**: the `pre-push-review` skill already splits review into lenses; a team version gives each lens a
  teammate that can challenge the others.
- **Use the terms in [`GLOSSARY.md`](../GLOSSARY.md) as written** in spawn prompts, so teammates read the docs the same way.

## 15. Pre-flight checklist

- [ ] The work splits into independent pieces; subagents or one session wouldn't do.
- [ ] `CLAUDE_CODE_EXPERIMENTAL_AGENT_TEAMS=1` and an interactive session.
- [ ] 3 to 5 named teammates, each with a distinct file set (and worktree, if writing code).
- [ ] Each spawn prompt has goal, scope, known facts, deliverable, who to talk to, and the done check.
- [ ] Models chosen: Sonnet for bounded work, the strongest model where judgment decides the result.
- [ ] Common commands pre-approved; the lead isn't in a broader permission mode than the team should have.
- [ ] Hooks in place for the gates you need (`TaskCompleted`, `TeammateIdle`, `TaskCreated`).
- [ ] Tasks sized at 5 to 6 per teammate with dependencies set.
- [ ] A plan to monitor, synthesize, and shut teammates down when done.
