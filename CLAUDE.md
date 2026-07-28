# Jobnet — Agent Instructions

## Authorship — there is ONE coder on this app

Every line in this repo was written by Claude, across many sessions, for a single
user. There are no other contributors, no teammates, no inherited legacy from
someone else.

**So never characterise uncommitted changes as belonging to anyone else.** Don't
say "your work", "pre-existing work from another author", or talk about changes
being "interleaved" with a third party's. If the working tree is dirty when a
session starts, that is simply work from an earlier session — same author, same
project, and fair game to commit, refactor, or replace.

The only distinction ever worth drawing is temporal: *changes made this session*
vs *changes already in the tree*. That matters for structuring commits. It is not
an authorship boundary and must not be described as one.

## Working style

- **Answer the question that was asked.** Direct answers first. Don't bury a
  one-line answer under options, caveats, or a proposal.
- **Don't ask permission for things already asked for.** If the request is clear,
  act. Reserve questions for genuine forks where the wrong guess wastes real work.
- **State a concern once, then proceed.** If a request is reaffirmed, that is the
  decision — carry it out without relitigating.
- **Never fabricate data in a tabular/query format.** Rendering a proposal as if
  it were query output has caused real confusion here. If output is illustrative,
  say so plainly.

## Database

- SQLite at `%LOCALAPPDATA%\Jobnet\jobnet.db` (~19 MB). WAL mode, so concurrent
  readers are fine while the app runs; **close the app before writing to it.**
- **Never commit the database.** `config` holds live API keys (Gemini, Groq,
  Google CSE, Brave) plus resume text, generated cover letters, and the full
  applied/interest history. `.gitignore` covers `*.db`, `*.db-wal`, `*.db-shm`.
- When copying it to inspect, take `jobnet.db`, `-wal` and `-shm` together — the
  WAL routinely holds megabytes of writes not yet in the main file.
- Schema changes go in `Data/Migrations/NNN_name.sql` as embedded resources, run
  in filename order by `MigrationRunner` and tracked in `schema_migrations`. Use
  `INSERT OR IGNORE` for new config keys so user edits are never clobbered.

## Build

- The running app locks `bin\Debug\net8.0-windows\Jobnet.exe`. Compilation still
  succeeds — only the final copy fails, with MSB3021/MSB3027. Close Jobnet before
  building; a `dotnet build` "failure" with no `error CS` lines is this and
  nothing more.
- `Jobnet.exe test` runs the CLI self-test suite (classifier, filters, location,
  rate limiter, migrations) and applies pending migrations first.
- Unit tests are xunit in `Jobnet.Tests`. Method naming is
  `MethodUnderTest_lowercase_snake_description`.

## Filter rules

All block/allow/greylist lists live in the `filter_rule` table — one table, five
match types (`domain`, `regex`, `substring`, `exact`, `word`), scoped `crawl` /
`discovery` / everywhere. Editable in-app via Refresh → **Filters…**.

Do not add new hardcoded string arrays of blocked hosts or paths. That is exactly
what this table replaced: four divergent C# lists that disagreed with each other.

**Always dry-run a new pattern against real data before committing to it.** The
Filters window's Preview button does this. Two seeded rules looked obviously safe
and were not: `/legal` would have killed Y Combinator's legal-roles job board, and
a global `ycombinator.com` rule would have silently broken Vanta, which sits on
`account.ycombinator.com`.
