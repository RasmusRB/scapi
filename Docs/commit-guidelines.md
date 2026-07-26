# Commit Guidelines

We follow the **Angular commit convention**. Every commit message has a header, an
optional body, and an optional footer:

```
<type>(<scope>): <subject>

<body>

<footer>
```

## Header

- **type** — one of:
  - `feat` — a new feature
  - `fix` — a bug fix
  - `docs` — documentation only
  - `style` — formatting, whitespace, no code-behavior change
  - `refactor` — code change that neither fixes a bug nor adds a feature
  - `perf` — a change that improves performance
  - `test` — adding or correcting tests
  - `build` — build system or external dependencies
  - `ci` — CI configuration and scripts
  - `chore` — other changes that don't modify src or test files
  - `revert` — reverts a previous commit
- **scope** (optional) — the area affected, e.g. `algorithm`, `store`, `devices`, `docs`.
- **subject** — imperative, present tense ("add", not "added"/"adds"); no capital
  first letter; no trailing period; keep under ~72 chars.

## Body

- Explain **why** the change was made and what problem it solves — not what the diff
  already shows. Contrast with previous behavior where it helps.
- Wrap at ~72 columns. Use present-tense imperative.

## Footer

- Breaking changes start with `BREAKING CHANGE:` followed by a description.
- Reference issues being closed, e.g. `Closes #123`.

## Examples

```
fix(algorithm): drop stuck Active Tracking devices back to a normal mode

A device left in Active Tracking with no open search drains the battery in
hours. Flag it as a deviation and recommend WM8/WM9 so the fleet view can
surface it before the battery dies.
```

```
feat(devices): add GET /devices/deviations endpoint

Care staff need a single list of devices in an abnormal state instead of
polling each device individually.
```
