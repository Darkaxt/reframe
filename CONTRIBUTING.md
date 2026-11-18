# Contributing to reframe

Thanks for helping! Bug reports, ideas and pull requests are all welcome. For anything bigger than a small fix, please open an issue first so we can agree on the approach before you put time into it.

## Reporting a bug

Please include your OS and version, your reframe and Claude Code versions (`claude --version`), what you did, what you expected and what happened. The app log helps a lot: it is `logs/main.log` in reframe's profile directory (`%APPDATA%\reframe` on Windows, `~/Library/Application Support/reframe` on macOS, `~/.config/reframe` on Linux). It contains folder paths and session ids, but no conversation content; look through it before you attach it.

## Setting up

You need the .NET SDK and a Rust toolchain on Windows. The host and the shell build separately:

```sh
cd rust && cargo build --release -p reframe-host
cd wpf && dotnet build Reframe.Shell.sln -c Release
```

## Before you open a pull request

```sh
cd rust && cargo fmt --all --check && cargo clippy --workspace --all-targets -- -D warnings && cargo test --workspace
cd wpf && dotnet test Reframe.Shell.sln -c Release
```

CI runs the same on Windows, plus a packaged smoke test.

- **Tests**: new behavior comes with a test: xunit tests in `wpf/Reframe.Shell.Tests`, Rust tests next to the code.
- **Code**: C# and Rust. Code, comments, commit messages and docs for contributors are in English. Match the surrounding code: its naming, its comment density, its idioms. Comments say why, not what.
- **Commits**: one topic per commit, with a short summary line like `fix: tiles keep their size after a restart` (`feat:`, `fix:`, `docs:`, `test:`, `chore:`) and a body that explains why.
- **Dependencies**: add as few as possible, pin exact versions, and only use releases that are at least two weeks old.

## Where things are

| Path        | What                                                                             |
| ----------- | -------------------------------------------------------------------------------- |
| `wpf/`      | The WPF shell (`Reframe.Shell`), its logic (`Reframe.Shell.Core`) and its tests  |
| `rust/`     | The PTY host and its libraries (sessions, transcripts, services)                 |
| `maintenance/` | Packaging and the managed deploy scripts                                      |
| `docs/`     | Plan, findings, release notes for maintainers, logo                              |
| `site/`     | The website on GitHub Pages (built by `scripts/site.mjs`, see docs/RELEASING.md) |

[docs/PLAN.md](docs/PLAN.md) and [docs/PHASE0-FINDINGS.md](docs/PHASE0-FINDINGS.md) (German) explain the design and what was learned about Claude Code, ConPTY and Windows along the way.

## License

By contributing, you agree that your contributions are licensed under the [MIT License](LICENSE).
