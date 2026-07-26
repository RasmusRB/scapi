# AI usage note

**Tool:** Claude Code (Anthropic) as the primary pair-programmer.

**How much:** heavy on boilerplate — DTOs, JSON parsing, DI wiring, test skeletons — and on the first draft of the
algorithm's structure. The thresholds, the safety-first priority order and the battery model were decided and
reviewed by hand.

**Where it was wrong.** It produces work that is locally plausible and globally inconsistent. Two examples, both
caught by re-reading the brief against the code and running the service against the dataset rather than by any
test. The README asserted that a deactivate-search couldn't interleave with a recommendation — the mutation lock
was real, but reads handed out the live mutable `DeviceState`, so the claim was simply false. And the target-battery
parameter table contradicted the drain model *within a single response*: "meets the 24h battery target" printed
next to `8.2 %/h`, which is 12 hours, not 24. Neither surfaced in tests, because the tests encoded the same
assumptions the code did.

The general lesson: AI output that looks finished and self-consistent often isn't, and prose it writes *about*
code (docs, comments, rationale strings) is the least trustworthy part — nothing checks it. Both bugs above were
assertions, not logic.
