---
name: sipke-voice
description: Write or rewrite prose in Sipke Schoorstra's voice — documentation, wiki pages, READMEs, release notes, blog posts, announcements, commit subjects, and issue or pull request bodies. Use when drafting any prose a reader will attribute to Sipke, or when asked to rewrite existing text so it reads as his rather than as generic assistant output. Do not use for code, code comments, or identifier naming.
metadata:
  short-description: Write prose in Sipke's voice.
---

# Sipke voice

This skill changes *how* prose reads, never *what* it claims. Never soften a
technical fact, drop a diagnostic code, or invent a rationale to fit the style.
If a rule here would require changing the substance, keep the substance and drop
the rule.

The rules are ordered by how much they change the output. `references/voice-profile.md`
carries the evidence for each one, quoted from the corpus they were derived from;
`references/before-after.md` has worked rewrites.

## The rules

1. **Lead with the verdict, then the reason.** Open on the claim, not on context
   or a restatement of the question. "No. There is no change tracking, no lazy
   loading, no navigation properties, and **no joins**."

2. **Name the rejected alternative, using "rather than".** This is the signature
   construction — 42 uses against 5 of "instead of" in the corpus. A design
   choice is never described in isolation; the thing it was chosen over is
   stated concretely. "one value rather than two that happen to agree", not
   "a single consistent value".

3. **Answer "why" with "Because", starting the sentence.** A reason gets its own
   sentence opening on the word. "Because it was NaN, an infinity, or negative
   zero (`GW-VALUE-DOUBLE-001`)."

4. **Systems are agents.** Components refuse, admit, disagree, say so, name
   themselves, and are honest or not. An error is a deliberate refusal by a
   component that knows what it is doing — not a failure that befalls the user.
   "MongoDB's in-process `Schema.Apply` reads no ledger and says so instead."
   Never anthropomorphise into cuteness: these verbs describe behaviour
   precisely, they are not jokes.

5. **State a rule with the cost of breaking it, in the same breath.** A bare
   imperative is incomplete. "Leaving a claim behind when you stop is worse than
   never making one, because the issue then looks worked when it is not."

6. **Vary sentence length deliberately.** Median 13 words, but 27% at eight or
   fewer and 10% at thirty or more. Short declarative claim, then a longer
   sentence carrying the qualification. Do not level this into uniform
   medium-length prose — the rhythm is the voice.

7. **Name the exact identifier.** Diagnostic codes, types, flags, and paths go
   inline in the prose. "refused (`GW-SEM-TYPE-006`)", never "an appropriate
   error is raised".

8. **Bold the term of art on first definition, and the word a sentence turns
   on.** About twenty bold runs per thousand words. Not emphasis-by-shouting.

9. **Em dash for a mid-sentence qualifier**, roughly once per ninety words.
   Not as a substitute for a colon at the end of a sentence, and never stacked
   in one clause.

10. **Open a document with one orienting line that states what the reader gets.**
    "Read this once and most of Groundwork's API stops being surprising."

11. **Trade-offs are bargains with named prices.** "which is a different bargain
    from the one `Double` makes", not "there are trade-offs to consider".

12. **Write about the machinery, in the present tense.** Second person is for
    instructions; explanation takes the system as its subject.

13. **Say what not to do first, in bold, with the reason it fails.** In
    operational and troubleshooting prose. "**Do not** try to download run
    artifacts or fetch a raw log URL. Both resolve to a blob host the agent
    proxy rejects with `403`, and no amount of retrying changes that."

14. **Commit subjects are imperative sentences, not labels.** No
    conventional-commit prefix, no scope tag, one line. Often a verb, an object,
    then a comma and the clause that pins down the precise qualification:
    "Count the declared key as query coverage, from one shared derivation".

## Never

These appear zero times in thirty-six thousand words of the corpus. Keep them at
zero.

- "It's important to note", "It's worth noting", "Let's dive in", "delve",
  "In today's fast-paced", "At the end of the day".
- A closing paragraph that summarises what was just said. Sections end on the
  last substantive fact, usually a cross-reference.
- Hedging stacks — "generally", "typically", "in most cases" — softening a claim
  that is then never bounded. This voice asserts, then states the exact bound.
- Enthusiasm adverbs about the software: "powerful", "seamless", "robust",
  "simply", "easily".
- Rhetorical questions as transitions. An FAQ heading is a real question, which
  is different.
- Tricolons for rhythm. Three items appear when there are exactly three things.

## Register by medium

- **Conceptual docs and wiki** — the fullest voice: definition, the reason it is
  that way, the rejected alternative, the diagnostic code, a cross-reference.
- **Release notes** — same voice, tighter. Lead with the boundary or breaking
  change; put the required user action in bold as its own paragraph.
- **Operational and agent-facing docs** — most imperative, most explicit about
  the consequence of non-compliance, bolded lead-ins on each bullet.
- **README** — flattest and most factual. Orientation and commands, no argument.
- **Commit subjects** — one imperative line, per rule 14.
- **Blog posts, talks, email, replies to users** — not covered. The corpus is
  entirely technical writing for one project. Apply rules 1-6 and the Never
  list, and say you are extrapolating.

## Spelling

The corpus is genuinely inconsistent: `AGENTS.md` uses "recognise" and
"behaviour", the wiki uses "honor" and "behavior". Match whatever the
surrounding file already does. When starting a new file with no precedent, use
American spelling, which dominates the published documentation.
