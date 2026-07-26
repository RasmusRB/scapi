---
name: commit
description: Write human-like git commits that explain why a change was made, following the Angular commit convention. Use when the user asks to commit changes, write a commit message, or stage and commit work.
---

# Commit

Write commits that read like a thoughtful engineer wrote them — focused on **why**
the change was made, not a restatement of the diff.

## Before writing

1. Read `Docs/commit-guidelines.md` and follow the Angular convention defined there
   (`<type>(<scope>): <subject>`, body, footer).
2. Run `git status` and `git diff` (and `git diff --staged`) to understand what
   actually changed and why it was needed.
3. Look at recent history with `git log --oneline -15` to match the existing tone,
   scope names, and formatting.

## Writing the message

- **Subject**: imperative, lower-case, no trailing period, under ~72 chars.
- **Body**: explain the motivation — the problem being solved, the previous behavior,
  and the reasoning behind the approach. Skip it only for genuinely trivial changes.
  Wrap at ~72 columns.
- Write like a person: plain language, no filler, no marketing tone. Prefer concrete
  reasoning over vague summaries.

## Hard rules

- **NEVER** add AI co-author trailers. Do not append
  `Co-Authored-By: Claude ...`, `Generated with Claude Code`, or any similar
  AI/tool attribution to the commit message or footer.
- **NEVER** pass the message with several `-m` flags. Write the full message to a
  temporary file and commit from it with `-F`.
- Don't invent a reason you can't infer from the diff or the user — ask instead.
- Group unrelated changes into separate commits when it makes the history clearer.

## Committing

Write the complete message (subject, blank line, body, footer) to a temp file, then
commit from it and clean up:

```bash
cat > /tmp/commit-msg.txt <<'EOF'
fix(algorithm): drop stuck Active Tracking devices to a normal mode

A device stuck in Active Tracking with no open search drains its battery in
hours. Flag it as a deviation so it surfaces before the battery dies.
EOF

git add <files>
git commit -F /tmp/commit-msg.txt
rm -f /tmp/commit-msg.txt
```
