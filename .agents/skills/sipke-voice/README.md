# sipke-voice

A writing-voice skill: it changes how prose reads, not what it claims. The rules
were mined from this repository — 50 commit subjects, `AGENTS.md`, `README.md`,
~36,000 words of `docs/wiki/`, and `docs/v2/releases/` — and every one carries
quoted evidence in `references/voice-profile.md`.

```
sipke-voice/
  SKILL.md                       14 rules, a Never list, and register by medium
  references/voice-profile.md    the evidence, measured baselines, provenance
  references/before-after.md     six worked rewrites against real repo prose
```

## Codex

Codex reads skills from two places. The directory is self-contained, so copying
it is the whole install.

**This repository** — already in place at `.agents/skills/sipke-voice/`. Codex
picks up repo-scoped skills from `.agents/skills/` with no further step.

**Every repository** — copy it into your Codex home:

```bash
mkdir -p "${CODEX_HOME:-$HOME/.codex}/skills"
cp -R .agents/skills/sipke-voice "${CODEX_HOME:-$HOME/.codex}/skills/"
```

Codex watches these directories, so a running session picks the skill up without
a restart. Verify with `/skills` in the TUI; `sipke-voice` should list with its
short description.

## Claude Code

The same directory works unmodified — the `name` and `description` frontmatter
fields mean the same thing in both tools.

```bash
mkdir -p ~/.claude/skills
cp -R .agents/skills/sipke-voice ~/.claude/skills/
```

For repository scope in Claude Code, copy it to `.claude/skills/sipke-voice/`
instead.

## Keeping one copy

If you want the skill in both `.claude/skills/` and `.agents/skills/` in this
repository without maintaining two copies, Codex migrates Claude Code skills for
you: it reads `.claude/skills/<name>/` and writes `.agents/skills/<name>/`. Put
the canonical copy under `.claude/skills/` and let the migration produce the
Codex one, rather than editing both by hand.

## Extending it

The corpus is entirely technical writing for one project. It says nothing about
how you write a blog post, a conference talk, an email, or a reply to a
frustrated user — `SKILL.md` says so under **Register by medium** rather than
guessing. To cover one of those, add samples to `references/voice-profile.md`
under a new heading, then add rules only where the new samples actually differ
from the existing ones.
