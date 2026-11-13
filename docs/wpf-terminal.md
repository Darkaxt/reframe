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
| Find bar (Ctrl+Shift+F) over the top-right of the terminal; every match marked, the current one in amber | Find bar above the terminal (it shrinks by one bar while open); the current match is the terminal's selection, scrolled into view; "n of m" counted up to 1000. The control exports no search, so find uses its UI Automation text pattern; the other matches are not marked |
| Text size Ctrl +/-/0, 8–32 px, saved as `preferences.fontSize` | Same keys and range, saved the same way; the control takes whole points (`TerminalSetTheme`'s `short fontSize`), so px are rounded to points (13 px → 10 pt) and some steps look the same |
| Font Cascadia Mono 13 px, line height 1.1, theme `#17181c`/`#d7dae0`/selection `#3a3f4b`, xterm's 16 colours, blinking block cursor | Same family, colours and cursor; size as above |
| xterm's 14 px overlay scrollbar, slider shown on hover/scroll | The control's WPF `ScrollBar` restyled: 14 px, no track or arrows, slider `rgba(121,121,121,.4)` while the pointer moves over the terminal |

### What the packaged control cannot match

- **Line height and cell width.** `HwndTerminal` sizes cells from the font alone (`FontInfoDesired` without a cell
  size), and its exported `TerminalSetTheme` takes only a family and a whole point size. At 10 pt Cascadia Mono the
  cells are 8 x 15 px; xterm draws 13 px with `lineHeight: 1.1` as about 7.75 x 16 px. So the WPF terminals show about
  one more row per 16 rows and wrap a column or two earlier. Windows Terminal itself has `cellWidth`/`cellHeight`
  (`font.cellHeight` in its settings), but the WPF control does not pass them.
- **Find marks.** No search export: only the current match is shown (as the selection), not every match.
- **Scrollback.** Fixed at 9001 lines (`_terminal->Create({ 80, 25 }, 9001, ...)`); xterm keeps 5000.
- **Renderer.** No switch to software rendering (backlog #15: no WARP switch).

## Packaging decision

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
