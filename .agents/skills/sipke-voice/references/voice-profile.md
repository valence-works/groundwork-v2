# Sipke voice — mined profile

Derived from `valence-works/groundwork-v2` on 2026-08-29: 50 commit subjects,
`AGENTS.md`, `README.md`, ~36,000 words of `docs/wiki/`, and the
`docs/v2/releases/` notes. Every claim below is followed by evidence from that
corpus. Nothing here comes from prior sessions — see "Provenance" at the end.

## Measured baseline (prose only; code blocks and inline code stripped)

| Metric | Value |
| --- | --- |
| Sentences sampled | 1,121 |
| Mean words per sentence | 15.4 |
| Median words per sentence | 13 |
| Sentences ≤ 8 words | 27% |
| Sentences ≥ 30 words | 10% |
| Em dashes per 1,000 words | 11 |
| Bold runs per 1,000 words | ~20 (715 total) |
| "it's important to note" / "let's dive" / "delve" / "in today's" | 0 |

The shape that produces: a short declarative claim, then a longer sentence that
carries the reason. Not uniform medium-length sentences.

## Rules, with evidence

1. **Lead with the verdict, then the reason.** No throat-clearing, no restating
   the question.
   > "Is Groundwork an ORM?" → "No. There is no change tracking, no lazy
   > loading, no navigation properties, and **no joins**."
   > "Can I store a `double`?" → "Yes — `PortableType.Double` is storable on all
   > four providers and round-trips bit-for-bit. You cannot *compare* one."

2. **Answer "why" with "Because", starting the sentence.** 25 occurrences.
   > "Because `IQueryable` accepts *any* expression tree and only decides at
   > runtime whether it can translate it…"
   > "Because it was NaN, an infinity, or negative zero (`GW-VALUE-DOUBLE-001`)."

3. **Frame every choice as *X* rather than *Y*.** 42 occurrences of "rather
   than" against 5 of "instead of" — this is the single most characteristic
   construction in the corpus. The rejected alternative is always named
   concretely, never as a vague "the other approach".
   > "plans as a rename that brings the rows with it, rather than as a drop and
   > a create"
   > "one value rather than two that happen to agree"
   > "refuses them at the write rather than storing a value a different provider
   > would hand back differently"
   > "is refused by name rather than failing opaquely"

4. **Systems are agents. Give them verbs of speech, honesty, and judgement.**
   117 uses of "refus*", 15 of "admit", 8 of "disagree", plus "say so", "name
   itself", "honest". Errors are deliberate refusals by a component that knows
   what it is doing — not failures that befall the user.
   > "MongoDB's in-process `Schema.Apply` reads no ledger and says so instead"
   > "an index seek and a scan can disagree about the same row"
   > "Bound the concurrency job and make a hang name itself"
   > "Make the async terminals, counts, and analyzer surface honest"
   > "Never let a failed disposal strand the connection it was releasing"

5. **State a rule with the cost of breaking it, in the same breath.** Rules are
   never bare imperatives; the consequence is the justification.
   > "Leaving a claim behind when you stop is worse than never making one,
   > because the issue then looks worked when it is not."
   > "Without the number in the name there is no way to ask whether an issue is
   > already being worked short of reading every branch, and two people can pick
   > different reasonable names for the same issue and never recognise each
   > other's work."
   > "A **pass** under load is trustworthy; a **failure** under load is not."

6. **Name the exact identifier every time.** Diagnostic codes, types, flags and
   file paths appear inline in prose, never as "the relevant error" or "a
   configuration setting".
   > "predicates and ordering are refused (`GW-SEM-TYPE-006`), and a key, index,
   > or grouping column is refused at declaration (`GW-PORT-012`)"
   > "the deployment tool needs the plan fingerprint plus
   > `--allow-semantic rename-column:orders.buyer`"

7. **Bold the term of art on first definition; bold the word the sentence turns
   on.** Not for emphasis-by-shouting.
   > "A **storage unit** is one logical typed table or collection."
   > "The logical id is what **carries identity across a rename**."
   > "Renames are **authorized** work, not automatic"

8. **Em dash for a mid-sentence qualifier or a sharpened restatement**, roughly
   once per 90 words. Never as a substitute for a colon at the end of a
   sentence, and never in pairs stacked in one clause.
   > "A provider-owned schema definition whose payload changes — a SQL Server
   > batch table type follows its unit's columns, for example — now re-applies…"

9. **Open a document with one orienting line that states what the reader gets.**
   > "Read this once and most of Groundwork's API stops being surprising."
   > "Groundwork is a provider-neutral persistence kernel for .NET."
   > "This release marks a **persisted schema boundary**."

10. **Trade-offs are bargains with named prices**, not "considerations".
    > "A `Single` column would be a widened binary64 column on half the
    > supported providers, which is a different bargain from the one `Double`
    > makes."
    > "would either constrain the model to the weakest provider or produce a
    > different plan on each"

11. **The subject is usually the system, not the reader.** Second person appears
    for instructions ("Discard every catalog…", "Read that file.") but
    explanation is written about the machinery, in the present tense.

12. **Commit subjects: imperative mood, no conventional-commit prefix, no
    scope tag, one line, and they read like sentences rather than labels.**
    > "Reclaim abandoned live-provider databases"
    > "Count the declared key as query coverage, from one shared derivation"
    > "Give issues a claim protocol, and correct where the work lives"
    > "Stop asserting a native plan on a table too small to have one"
    > "Serialize the PostgreSQL lazy ledger bootstrap on the object it creates"
    Note the pattern in the last three: a verb, the object, then a comma or
    subordinate clause that pins down the *precise* qualification.

13. **Operational guidance says what not to do first, in bold, with the reason
    it fails.**
    > "**Do not** try to download run artifacts or fetch a raw log URL. Both
    > resolve to a blob host the agent proxy rejects with `403`, and no amount
    > of retrying changes that."

14. **British-leaning spelling in prose written by hand** ("recognise",
    "behaviour" in `AGENTS.md`), but American spelling in the docs corpus
    ("honor", "behavior"). The corpus is genuinely inconsistent here — decide
    which you want before the skill freezes it. Flagged rather than averaged.

## Never (absent from 36,000 words — keep them absent)

- "It's important to note", "It's worth noting", "Let's dive in", "delve",
  "In today's fast-paced…", "At the end of the day" — zero occurrences.
- Closing paragraphs that summarise what was just said. Sections end on the last
  substantive fact, usually a cross-reference link.
- Hedging stacks: "generally", "typically", "in most cases" softening a claim
  that is then not bounded. This voice asserts, then states the exact bound.
- Enthusiasm adverbs about the software: "powerful", "seamless", "robust",
  "simply", "easily".
- Rhetorical questions used as transitions (FAQ headings are real questions,
  which is different).
- Tricolons for rhythm. Lists of three appear only when there are exactly three
  things.

## Register by medium

- **Wiki / conceptual docs** — the fullest voice: definition, the reason it is
  that way, the rejected alternative, the diagnostic code, a cross-reference.
- **Release notes** — same voice, tighter; leads with the boundary or breaking
  change, and puts the required user action in bold as its own paragraph.
- **`AGENTS.md` / operational** — most imperative, and the most explicit about
  consequence-of-non-compliance; bolded lead-ins on each bullet.
- **Commit subjects** — one imperative line, per rule 12.
- **README** — flattest and most factual; orientation and commands, no argument.

## Provenance — read before trusting this

- I have no memory of you across sessions. This profile was mined today from
  the repository in front of me; if it feels accurate, that is because the
  corpus is yours, not because I remembered anything.
- 7 of 50 commits carry Claude co-author trailers, and this repo has an
  `AGENTS.md` and Copilot commits — so an unknown share of this prose was
  drafted by agents and then reviewed by you. Treat it as **the voice you ship
  and sign off on**, which is the right target for a voice skill, but not as
  proof of unassisted authorship.
- The corpus is entirely technical writing for one project. It says nothing
  about how you write a conference talk, a blog post, an email, or a reply to a
  frustrated user. Those need separate samples.
