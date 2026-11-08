# Releasing a new version

Players get the plugin through the shared [DalamudPlugins](https://github.com/link-0402/DalamudPlugins)
repository. A release has two halves, and players only see it once both are done:

1. **The GitHub release** of this repository, built by `.github/workflows/release.yml` when a `v*` tag is
   pushed. The install and update links in `repo.json` always download the latest release's zip.
2. **The `repo.json` entry** in DalamudPlugins. Dalamud compares its `AssemblyVersion` with the installed
   version, so without this step nobody who has the plugin is offered the update.

Pushing to `main` alone publishes nothing.

## 1. Set the version

The version lives in one place: `<Version>` in `UniversalModConverter.csproj`, four parts (`1.5.2.0`).
Bump it in its own commit ("Bump to 1.5.2.0"), or check that an earlier bump already did.

## 2. Test and push `main`

```bash
dotnet run --project UniversalModConverter.Tests
```

The runner ends with `N/N tests passed.` Commit everything that belongs in the release and push `main`.
`REVIEW_HANDOFF.md` is a local working note and is never committed.

## 3. Tag the release

The tag is `v` plus the csproj version (`v1.5.2.0`). The workflow stops before building when they differ.

```bash
git tag -a v1.5.2.0 -m "v1.5.2.0"
```

```bash
git push origin v1.5.2.0
```

The workflow checks the version, builds, runs the tests and publishes a release with
`UniversalModConverter.zip` and `latest.zip`. Follow it and check the result:

```bash
gh run list -R link-0402/Universal-Mod-Converter -L 1
```

```bash
gh api repos/link-0402/Universal-Mod-Converter/releases/latest --jq .tag_name
```

## 4. Update DalamudPlugins

Clone or pull it fresh right before editing: other plugins (XIV Instant Edit, WindowResize+, XIV Port
Studio) are bumped in the same file, often from other sessions, and a stale copy gets its push rejected.

In `repo.json`, change only the `UniversalModConverter` entry:

| Field | New value |
|---|---|
| `AssemblyVersion` | the csproj version, `1.5.2.0` |
| `LastUpdate` | the current Unix time in seconds |
| `Changelog` | a new `### 1.5.2` section at the top, above the previous one |
| `Description`, `Punchline` | only when `UniversalModConverter.json` changed them; keep the two in step |

The changelog is written for players: what they can now do or what no longer goes wrong, one bullet
each, no commit hashes or internal names. Draft it from `git log v<previous>..v<new>`.

`repo.json` uses LF line endings and two-space indentation. Clone it with
`git -c core.autocrlf=false clone`, since a checkout with `autocrlf` on turns it CRLF. Edit the fields
in place rather than reloading and re-serializing the whole file, which rewrites every other entry.

Commit as "Bump Universal Mod Converter to 1.5.2.0" and push. If the push is rejected, someone bumped
another plugin in the meantime:

```bash
git pull --rebase origin main
```

then push again. The repository's `validate.yml` workflow checks that every entry has its required fields
and uses latest-release download links.

## Doing this from Claude Code

In auto mode, the safety check blocks pushing to DalamudPlugins ("Modify Shared Resources") and blocks
Claude from changing its own permissions ("Self-Modification"). For Claude to run the whole release,
add the allow rules yourself from an interactive `claude` terminal (`/permissions`), or put them in
`~/.claude/settings.json`:

```json
{
  "permissions": {
    "allow": [
      "Bash(git push:*)",
      "Bash(git commit:*)",
      "Bash(git tag:*)"
    ]
  }
}
```

Without them, Claude prepares the commit in a clone and you run the push yourself.
