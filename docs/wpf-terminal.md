# The WPF shell's terminal control

The WPF shell (`wpf/`) draws each session with `Microsoft.Terminal.Wpf.TerminalControl`, Windows Terminal's
`HwndTerminal` in a native child window, fed by the pty host (`HostTerminalConnection`). This page records what the
control allows, how reframe's xterm.js behaviour (`src/renderer/terminals.ts`) is reproduced on it, and how the control
is to be packaged for production.

## Behaviour as in the Electron UI

| Electron (terminals.ts and friends) | WPF (`TerminalBehaviour`, `Reframe.Shell.Core/TerminalInput.cs`, `Shortcuts.cs`) |
| --- | --- |
| Selecting does not copy; Enter (or Shift+Enter) with a selection copies it, deselects and sends nothing | Same: PreviewKeyDown takes the selection (`GetSelectedText` clears it) and marks the key handled, so no WM_CHAR reaches the session |
| Shift+Enter without a selection types ESC CR (Claude Code's newline) | Same |
| Ctrl+C copies with a selection or with Shift, else interrupts; Ctrl+V pastes | Same |
| Right-click copies the selection or pastes; a program that reads the mouse gets it unless Shift is held | Same, by a message hook on the terminal's window (the control's own right-click paste is replaced: it sent text raw) |
| Paste: text (CR line breaks, bracketed when the program enabled DECSET 2004), else files as quoted paths, else an image saved to the drops folder and typed as its path | Same; bracketed paste and mouse tracking are read from the session's output (`TerminalModes`), as the control does not report them |
| File drops (and in-memory images) typed as paths, Linux paths for sessions in WSL | Same, through WPF drag and drop on the tile; in-memory images go to `%TEMP%\reframe-drops\<uuid>\` |
| Copy notice and "hold Shift to select" hint over the bottom of the terminal | Shown in the tile's header: nothing can be drawn over the terminal's native window |
| Find bar (Ctrl+Shift+F) over the top-right of the terminal; every match marked, the current one in amber | Find bar above the terminal (it shrinks by one bar while open); every match marked and the current one in the focused colour (the renderer's own search highlight colours), scrolled into view; "n of m" (the patched control's `TerminalSearch`) |
| Text size Ctrl +/-/0, 8–32 px, saved as `preferences.fontSize` | Same keys and range, saved the same way; the patched control's `TerminalSetFont` takes fractional points (13 px = 9.75 pt) and a row height of px × 16/13 (16 px at 13 px, as xterm's `lineHeight: 1.1`) |
| Font Cascadia Mono 13 px, line height 1.1, scrollback 5000, theme `#17181c`/`#d7dae0`/selection `#3a3f4b`, xterm's 16 colours, blinking block cursor | Same family, colours and cursor; size as above |
| xterm's 14 px overlay scrollbar, slider shown on hover/scroll | The control's WPF `ScrollBar` restyled: 14 px, no track or arrows, slider `rgba(121,121,121,.4)` while the pointer moves over the terminal |

### What the patched control still cannot match

- **Cell width.** AtlasEngine rounds cells to whole pixels: Cascadia Mono at 13 px is 8 px wide, where xterm measures
  about 7.75 px, so lines wrap a column or two earlier on wide terminals. Rows now match (16 px at 13 px).
- **Find colours.** The marks use AtlasEngine's built-in search highlight colours, not xterm's amber.
- **Renderer.** No switch to software rendering (backlog #15: no WARP switch).

## Packaging (S6b)

The shell uses `Reframe.Terminal.Wpf`, built by `.github/workflows/terminal-wpf.yml` on a Windows runner from
microsoft/terminal `v1.25.3131.0` (commit `f88b209f1e57e1d8ca35977260c3cddacccaeb31`, checked) with
`wpf/terminal-patch/*.patch`: `Microsoft.Terminal.Control.dll` (x64) and `Microsoft.Terminal.Wpf.dll`, packed with
microsoft/terminal's `LICENSE` (MIT), `NOTICE.md` and the patch. The package is a workflow artifact and, once per
version, a release asset (`terminal-wpf-v<tag>-reframe.N`); `wpf/Get-TerminalPackage.ps1` downloads it into
`wpf/packages-local` (the local feed in `wpf/nuget.config`) and checks the SHA-256 pinned in the script. CI's wpf job runs
it before building; run it once before a local build. A patch change bumps `reframe.N` in the workflow, the script and
`Reframe.Shell.csproj`, then pins the new hash.

The patch (all in `HwndTerminal` and the WPF wrapper; the wrapper's existing API is unchanged):

- `TerminalSetFont(terminal, family, float points, cellWidth, cellHeight, dpi)`: as `TerminalSetTheme`'s font part,
  with a fractional size and `FontInfoDesired::SetCellSize` (the setting behind Windows Terminal's `font.cellWidth` /
  `font.cellHeight`, CSS lengths such as `16px`); the cell size is kept across later `TerminalSetTheme` calls.
  Wrapper: `TerminalControl.SetFont`.
- `TerminalSetDefaultHistorySize(lines)`: scrollback for terminals created afterwards (was a fixed 9001).
  Wrapper: `TerminalControl.SetDefaultHistorySize`.
- `TerminalSearch` / `TerminalClearSearch`: `ControlCore::Search` / `ClearSearch` over the console's `Search`, with
  `Terminal::SetSearchHighlights`, so the renderer marks every match. Wrapper: `TerminalControl.Search` / `ClearSearch`.

### Earlier decision (S6)


Keep the pinned `CI.Microsoft.Terminal.Wpf` package (`[1.25.260303002]`, exact version) behind the shell's own wrapper
(`HostTerminalConnection` and `TerminalBehaviour` are the only code that touches it) for the prototype and until
cutover. **Production (S14) builds the control from a tagged microsoft/terminal release** with a small patch, because
the remaining visual gap (line height) and the find marks need native exports that the package does not have, and
because the CI package is an unofficial feed build without a stable licence/notice bundle.

Plan for the production build (its own slice, before S14):

1. Pin a release tag (currently `v1.25.3131.0`, the 1.25 line the CI package comes from) as a git submodule or a
   fetched source archive with its SHA-256 recorded; keep its `LICENSE` and `NOTICE.md` with the shipped DLLs.
2. Patch `src/cascadia/TerminalControl/HwndTerminal.{h,cpp}` (kept as one reviewed `.patch` in `wpf/terminal/`):
   `TerminalSetCellSize(terminal, cellWidth, cellHeight)` (sets `FontInfoDesired::SetCellSize`, as Windows Terminal's
   `font.cellHeight` does) so 1.1 line height and xterm's cell width can be matched; `TerminalSetHistorySize` (5000 as
   in Electron); `TerminalSearch(terminal, text, forward, caseSensitive)` and `TerminalClearSearch` over the
   `til::... Search` the console already has, so every match can be marked; optionally a software-rendering flag.
3. Build only `Microsoft.Terminal.Control` (the HwndTerminal DLL) and `WpfTerminalControl` for x64 and arm64 with the
   tag's Visual Studio toolchain on a Windows CI runner (msbuild `OpenConsole.sln /t:Terminal\\Control\\...`, Release),
   publish them as a CI artifact and as a local NuGet package `Reframe.Terminal.Wpf` with the same assembly API, so the
   shell switches by changing one `PackageReference`.
4. Extend `TerminalBehaviour` to call the new exports when present (cell size from the CSS line height, find marks),
   rerun the visual report and the shell tests, then drop the CI package.

Rebuilds follow microsoft/terminal servicing tags; the patch is small and confined to `HwndTerminal`, so a rebase per
tag is cheap.
