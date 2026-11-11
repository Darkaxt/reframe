//! A session's terminal as the host keeps it: a headless VT emulator (vt100) fed with everything the process wrote,
//! from which a view that opens gets a snapshot. The snapshot is what the renderer writes into a terminal it has
//! just reset and sized to the snapshot's columns and rows (terminals.ts): the scrollback and the screen as text with
//! SGR attributes, line by line (lines that wrapped are left to wrap again), then the cursor, the current attributes
//! and the input modes. That is what xterm's serialize addon gives the Node host, in the same terms.

use std::fmt::Write as _;
use std::num::NonZeroU16;

use vt100::{Color, Parser, Screen};

/// Lines kept above the screen, as the Node host's mirror keeps them.
pub const SCROLLBACK: usize = 5000;

pub struct Mirror {
    parser: Parser,
}

fn size(value: u16) -> NonZeroU16 {
    NonZeroU16::new(value).unwrap_or(NonZeroU16::MIN)
}

impl Mirror {
    pub fn new(cols: u16, rows: u16) -> Self {
        Self {
            parser: Parser::new(size(rows), size(cols), SCROLLBACK),
        }
    }

    pub fn write(&mut self, data: &str) {
        self.parser.process(data.as_bytes());
    }

    pub fn resize(&mut self, cols: u16, rows: u16) {
        self.parser.screen_mut().set_size(size(rows), size(cols));
    }

    /// (cols, rows)
    pub fn size(&self) -> (u16, u16) {
        let (rows, cols) = self.parser.screen().size();
        (cols.get(), rows.get())
    }

    /// Escape sequences that repaint this terminal into a reset one of the same size.
    pub fn snapshot(&mut self) -> String {
        let screen = self.parser.screen_mut();
        let mut out = String::new();
        if screen.alternate_screen() {
            // The normal screen behind it is not kept apart by vt100: the snapshot is the alternate screen alone.
            out.push_str("\x1b[?1049h\x1b[H");
        }
        let (rows, _) = screen.size();
        let mut pen = Pen::default();
        screen.set_scrollback(usize::MAX);
        let history = screen.scrollback();
        // Each line of the scrollback, oldest first: at offset `history - i`, line `i` is the top row in view.
        for i in 0..history {
            screen.set_scrollback(history - i);
            write_row(&mut out, screen, 0, &mut pen, true);
        }
        screen.set_scrollback(0);
        for row in 0..rows.get() {
            write_row(&mut out, screen, row, &mut pen, row + 1 < rows.get());
        }
        out.push_str("\x1b[0m");
        let (row, col) = screen.cursor_position();
        let (_, cols) = screen.size();
        let _ = write!(out, "\x1b[{};{}H", row + 1, col.min(cols.get() - 1) + 1);
        let current = Pen::of_screen(screen);
        if current != Pen::default() {
            current.write(&mut out);
        }
        out.push_str(&screen.input_mode_formatted());
        if screen.hide_cursor() {
            out.push_str("\x1b[?25l");
        }
        out
    }
}

/// Writes one row in view; `more`: another row follows (a line break unless this one wrapped onto it).
fn write_row(out: &mut String, screen: &Screen, row: u16, pen: &mut Pen, more: bool) {
    let (_, cols) = screen.size();
    let cols = cols.get();
    let wrapped = screen.row_wrapped(row);
    // A row that wrapped is written whole, so the next one wraps on from it; others end at their last mark.
    let end = if wrapped {
        cols
    } else {
        (0..cols)
            .rev()
            .find(|&col| {
                screen
                    .cell(row, col)
                    .is_some_and(|c| c.has_contents() || Pen::of(c).paints())
            })
            .map_or(0, |col| col + 1)
    };
    // Empty cells are skipped over (cursor forward), so they stay empty rather than become spaces.
    let mut gap = 0u16;
    for col in 0..end {
        let Some(cell) = screen.cell(row, col) else { break };
        if cell.is_wide_continuation() {
            continue;
        }
        let attrs = Pen::of(cell);
        if !cell.has_contents() && !attrs.paints() {
            gap += 1;
            continue;
        }
        if gap > 0 {
            let _ = write!(out, "\x1b[{gap}C");
            gap = 0;
        }
        if attrs != *pen {
            attrs.write(out);
            *pen = attrs;
        }
        if cell.has_contents() {
            out.push_str(cell.contents());
        } else {
            out.push(' ');
        }
    }
    if gap > 0 {
        // The end of a row that wrapped: written out, so that what follows wraps as it did.
        if *pen != Pen::default() {
            out.push_str("\x1b[0m");
            *pen = Pen::default();
        }
        out.push_str(&" ".repeat(usize::from(gap)));
    }
    if more && !wrapped {
        // A new line takes the background in use: plain attributes first.
        if *pen != Pen::default() {
            out.push_str("\x1b[0m");
            *pen = Pen::default();
        }
        out.push_str("\r\n");
    }
}

/// The attributes a cell is drawn with.
#[derive(Clone, Copy, PartialEq, Eq, Default)]
struct Pen {
    fg: Color,
    bg: Color,
    bold: bool,
    dim: bool,
    italic: bool,
    underline: bool,
    inverse: bool,
}

impl Pen {
    fn of(cell: &vt100::Cell) -> Self {
        Self {
            fg: cell.fgcolor(),
            bg: cell.bgcolor(),
            bold: cell.bold(),
            dim: cell.dim(),
            italic: cell.italic(),
            underline: cell.underline(),
            inverse: cell.inverse(),
        }
    }

    fn of_screen(screen: &Screen) -> Self {
        Self {
            fg: screen.fgcolor(),
            bg: screen.bgcolor(),
            bold: screen.bold(),
            dim: screen.dim(),
            italic: screen.italic(),
            underline: screen.underline(),
            inverse: screen.inverse(),
        }
    }

    /// Whether a blank cell drawn with these attributes looks different from an empty one.
    fn paints(&self) -> bool {
        self.bg != Color::Default || self.inverse || self.underline
    }

    /// SGR from plain attributes to these.
    fn write(&self, out: &mut String) {
        out.push_str("\x1b[0");
        for (on, code) in [
            (self.bold, 1),
            (self.dim, 2),
            (self.italic, 3),
            (self.underline, 4),
            (self.inverse, 7),
        ] {
            if on {
                let _ = write!(out, ";{code}");
            }
        }
        color(out, self.fg, 30, 90, 38);
        color(out, self.bg, 40, 100, 48);
        out.push('m');
    }
}

fn color(out: &mut String, color: Color, base: u8, bright: u8, extended: u8) {
    let _ = match color {
        Color::Default => Ok(()),
        Color::Idx(i) if i < 8 => write!(out, ";{}", base + i),
        Color::Idx(i) if i < 16 => write!(out, ";{}", bright + i - 8),
        Color::Idx(i) => write!(out, ";{extended};5;{i}"),
        Color::Rgb(r, g, b) => write!(out, ";{extended};2;{r};{g};{b}"),
    };
}

#[cfg(test)]
mod tests {
    use super::*;

    /// Text of every line (scrollback, then screen) of a terminal fed `data`, with trailing blanks dropped.
    fn lines(cols: u16, rows: u16, data: &str) -> (Vec<String>, (u16, u16)) {
        let mut parser = Parser::new(size(rows), size(cols), SCROLLBACK);
        parser.process(data.as_bytes());
        let screen = parser.screen_mut();
        screen.set_scrollback(usize::MAX);
        let history = screen.scrollback();
        let mut out = Vec::new();
        for i in 0..history {
            screen.set_scrollback(history - i);
            out.push(screen.rows(0, cols).next().unwrap_or_default().trim_end().to_string());
        }
        screen.set_scrollback(0);
        out.extend(screen.rows(0, cols).map(|r| r.trim_end().to_string()));
        (out, screen.cursor_position())
    }

    /// A snapshot written into a fresh terminal of the same size gives back the same lines and cursor.
    fn round_trip(cols: u16, rows: u16, data: &str) {
        let mut mirror = Mirror::new(cols, rows);
        mirror.write(data);
        let snapshot = mirror.snapshot();
        assert_eq!(
            lines(cols, rows, data),
            lines(cols, rows, &snapshot),
            "snapshot {snapshot:?}"
        );
        let mut again = Mirror::new(cols, rows);
        again.write(&snapshot);
        let (a, b) = (mirror.parser.screen(), again.parser.screen());
        for row in 0..rows {
            for col in 0..cols {
                let (x, y) = (a.cell(row, col).unwrap(), b.cell(row, col).unwrap());
                assert_eq!(x.contents(), y.contents(), "cell {row},{col}");
                assert!(Pen::of(x) == Pen::of(y), "attributes of cell {row},{col}");
            }
        }
        assert_eq!(a.hide_cursor(), b.hide_cursor());
        assert_eq!(a.bracketed_paste(), b.bracketed_paste());
        assert_eq!(a.application_cursor(), b.application_cursor());
    }

    #[test]
    fn plain_text_and_the_cursor() {
        round_trip(20, 5, "hello\r\nworld");
        round_trip(20, 5, "");
        round_trip(20, 5, "\x1b[3;7Hx");
    }

    #[test]
    fn scrollback_is_kept_in_order() {
        let data: String = (0..40).map(|i| format!("line {i}\r\n")).collect();
        round_trip(20, 5, &data);
        let mut mirror = Mirror::new(20, 5);
        mirror.write(&data);
        let snapshot = mirror.snapshot();
        assert!(snapshot.starts_with("line 0\r\nline 1\r\n"), "{snapshot:?}");
    }

    #[test]
    fn wrapped_lines_wrap_again() {
        round_trip(10, 4, "0123456789abcdefghij-wrapped twice\r\nnext");
        let mut mirror = Mirror::new(10, 4);
        mirror.write("0123456789abc");
        assert!(
            mirror.snapshot().starts_with("0123456789abc"),
            "no line break inside a wrapped line"
        );
    }

    #[test]
    fn colors_and_attributes() {
        round_trip(
            30,
            4,
            "\x1b[1;31mred\x1b[0m plain \x1b[4;38;5;200mpink\x1b[0m\r\n\x1b[7;48;2;1;2;3m rgb \x1b[0m\x1b[44m   \x1b[0m\r\n\x1b[92mbright",
        );
    }

    #[test]
    fn wide_characters() {
        round_trip(10, 3, "日本語テキスト\r\nok");
    }

    #[test]
    fn modes_and_hidden_cursor() {
        round_trip(20, 4, "\x1b[?2004h\x1b[?1h\x1b[?25lprompt> ");
    }

    #[test]
    fn current_attributes_carry_over() {
        let mut mirror = Mirror::new(20, 4);
        mirror.write("a\x1b[1;32m");
        let snapshot = mirror.snapshot();
        assert!(snapshot.contains("\x1b[1;2H\x1b[0;1;32m"), "{snapshot:?}");
    }

    #[test]
    fn resize_changes_the_size() {
        let mut mirror = Mirror::new(80, 24);
        mirror.resize(100, 30);
        assert_eq!(mirror.size(), (100, 30));
        mirror.resize(0, 0);
        assert_eq!(mirror.size(), (1, 1));
    }

    #[test]
    fn the_alternate_screen_is_marked() {
        let mut mirror = Mirror::new(20, 4);
        mirror.write("main\x1b[?1049halt");
        let snapshot = mirror.snapshot();
        assert!(snapshot.starts_with("\x1b[?1049h\x1b[Halt"), "{snapshot:?}");
    }
}
