# Eval capture — measuring the Revit MCP connector

## What this is for

Every prompt change, model swap and new command raises the same question: *did that
make it better?* Without a way to answer it, the connector improves by impression.

This design captures **task → outcome pairs** from real use, so any change can be
measured against work APG actually does. It is for **measurement**, not for training a
model — see [What this is not](#what-this-is-not).

## Two streams, one writer

The crash journal (`docs/REVIT_MCP.md` → work journal) and eval capture share the same
durable writer and differ in schema and lifetime:

| | Journal | Eval capture |
|---|---|---|
| Question | "what did I lose?" | "was the answer right?" |
| Lifetime | discarded at the next sync | kept indefinitely |
| Scope | everything that changed the model | only Claude/ChatGPT-driven tasks |
| Location | `RevitMCP\Journals\<model-guid>\` | `RevitMCP\Eval\` |

Both are append-only JSONL, one complete record per `Write`, `FileOptions.WriteThrough`
plus `Flush(true)`, `FileShare.Read`, and a reader that discards a torn final line. The
durability rules are in the journal design and are not repeated here.

## The hard part: where the label comes from

A trace of "Claude called `operate_element` with these 12 ids" is not an eval. An eval
needs a **verdict** — was that the right answer? Labelling by hand does not survive
contact with a working week, so the design leans on three sources, cheapest first.

### 1. Deterministic graders (best — and APG already has them)

The DM audit and the BIM health checks are rule engines over the model. They are
**automatic graders**:

> Model in state X → ask the AI to fix finding `DM-04-017` → re-run the audit →
> assert `DM-04-017` is no longer reported, and no new finding appeared.

That is an objective pass/fail with no human in the loop, on the work that matters most.
Every rule in `Resources/DmKnowledgeBase` is a potential grader. **Build this first** —
it produces a real eval set from day one.

### 2. Implicit signals (free, noisy)

Recorded automatically, never shown as truth on their own:

- the user undid the change within N minutes → negative
- the user re-ran the same task with different wording → negative
- the user synced with the change in place → weak positive
- the command threw → negative

### 3. Explicit verdict (rare, highest quality)

One thumbs up / down in the MCP Setup status line or the DM dashboard after an
AI-driven operation, with an optional note. Asked sparingly — on a sample, not every
action — because a prompt on every operation gets ignored and then resented.

## Record schema

```json
{
  "case_id": "ev_2026-09-25_00412",
  "ts": "2026-09-25T09:14:02.118Z",
  "revit": "2026",
  "model_key": "sha256:9f2c…",
  "task": "Set the DM usage code on every room on Level 02",
  "source": "dm-standards-checklist",
  "context": {
    "finding_ids": ["DM-04-017"],
    "element_count": 38,
    "categories": ["Rooms"]
  },
  "actions": [
    {"command": "operate_element", "params": {"action": "Select", "…": "…"}},
    {"command": "modify_element",  "params": {"…": "…"}}
  ],
  "outcome": {
    "changed": {"Rooms": 38},
    "errors": []
  },
  "grader": {
    "kind": "dm_audit",
    "rule": "DM-04-017",
    "before": "fail",
    "after": "pass",
    "new_findings": []
  },
  "verdict": "pass",
  "verdict_source": "grader"
}
```

Notes:

- `model_key` is a **hash**, never the path — see [Redaction](#redaction).
- `actions` carries commands and parameters, not element ids: an eval case has to be
  replayable against a fresh copy of the model, exactly as in the journal design.
- `verdict_source` is one of `grader` | `implicit` | `user`. Never mix them when
  scoring; a grader verdict and a thumbs-down mean different things.

## Redaction

Capture runs on live project data, so redaction happens **at write time**, not at export:

- model path, central path, project and client name → salted hash, salt kept locally
- Windows account name → stable pseudonym (`user_7a3f`)
- free-text `task` is kept (it is the prompt being evaluated) but flagged for review
  before any case leaves the machine
- nothing is uploaded anywhere by the plugin; export is always an explicit action

If colleagues' sessions are captured, agree internally what is recorded and who may read
it before switching capture on.

## Lifecycle

1. **Capture** — plugin-side, automatic, off by default; a checkbox in MCP Setup.
2. **Accumulate** — local JSONL under `RevitMCP\Eval\`, one file per month.
3. **Export** — `export_eval_set` produces a reviewed, redacted set plus a manifest.
4. **Run** — the harness replays each `task` against the model under test with the MCP
   tools available, then runs the recorded grader and scores pass/fail.
5. **Compare** — same set, different model or prompt, side by side.

Step 4 is where a Claude-vs-ChatGPT comparison stops being a matter of opinion.

## What to build

| Component | Where | Notes |
|---|---|---|
| `EvalWriter` | `Core/Mcp` | shares the journal's durable-append writer |
| Capture hook | `McpSocketService` | already logs `-> method` at line 250; add params, result and timing |
| Task attribution | MCP server | the natural-language task has to reach the plugin — a `task` field on the request envelope |
| DM grader adapter | `Core/Dm` | re-runs `DmAuditService` for one rule and diffs findings |
| Implicit signals | `Core/Mcp` | undo/re-run/sync watchers over `DocumentChanged` |
| `export_eval_set` | MCP tool | redacted export + manifest |
| Harness | `revit-mcp` repo | replay + score; see the `build-eval` and `hillclimb` workflows in the `claude-api` skill |

The one piece that needs a protocol change is **task attribution**: the plugin sees
commands, not the request that produced them. Without the originating task, cases are
not replayable. Simplest fix is an optional `task` string on the JSON-RPC envelope, set
by the MCP server from the current turn.

## What this is not

This is **not** a training corpus. Fine-tuning teaches form, not facts, and the volume
here is orders of magnitude short of what a fine-tune needs. Using outputs from Claude or
ChatGPT to train a competing model is also restricted by both providers' terms.

The value is measurement: knowing whether a change helped, on APG's own work, before it
ships to everyone.
