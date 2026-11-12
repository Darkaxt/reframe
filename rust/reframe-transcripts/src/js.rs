//! JavaScript string semantics the TypeScript relies on: `trim()` and `\s` (which include U+FEFF but not U+0085),
//! and lengths counted in UTF-16 code units.

/// What JavaScript's `trim()` and `\s` count as white space (WhiteSpace and LineTerminator).
pub fn is_space(c: char) -> bool {
    matches!(
        c,
        '\t' | '\n' | '\u{0B}' | '\u{0C}' | '\r' | ' ' | '\u{A0}' | '\u{1680}' | '\u{2000}'
            ..='\u{200A}' | '\u{2028}' | '\u{2029}' | '\u{202F}' | '\u{205F}' | '\u{3000}' | '\u{FEFF}'
    )
}

/// `s.trim()`.
pub fn trim(s: &str) -> &str {
    s.trim_matches(is_space)
}

/// `s.replace(/\s+/g, ' ')`.
pub fn collapse_spaces(s: &str) -> String {
    let mut out = String::with_capacity(s.len());
    let mut in_space = false;
    for c in s.chars() {
        if is_space(c) {
            if !in_space {
                out.push(' ');
            }
            in_space = true;
        } else {
            out.push(c);
            in_space = false;
        }
    }
    out
}

/// `s.length > n ? s.slice(0, n - 1) + '…' : s`, in UTF-16 code units. A surrogate pair the cut would split is left
/// out whole (JavaScript would keep its lone first half).
pub fn truncate(s: &str, n: usize) -> String {
    if s.encode_utf16().count() <= n {
        return s.to_string();
    }
    let mut out = String::new();
    let mut units = 0;
    for c in s.chars() {
        units += c.len_utf16();
        if units > n - 1 {
            break;
        }
        out.push(c);
    }
    out.push('…');
    out
}

/// `text.slice(text.indexOf('\n') + 1)`: without a line break, all of it.
pub fn after_first_line(text: &str) -> &str {
    text.find('\n').map_or(text, |i| &text[i + 1..])
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn trims_as_javascript_does() {
        assert_eq!(trim("\u{FEFF} a \u{3000}"), "a");
        assert_eq!(trim("\u{85}a"), "\u{85}a");
        assert_eq!(collapse_spaces("a \t\n b\u{A0}c"), "a b c");
    }

    #[test]
    fn truncates_in_utf16_units() {
        assert_eq!(truncate("abcdef", 6), "abcdef");
        assert_eq!(truncate("abcdefg", 6), "abcde…");
        assert_eq!(truncate("ab😀defg", 5), "ab😀…");
        assert_eq!(truncate("ab😀defg", 4), "ab…");
        assert_eq!(truncate("abc😀efg", 4), "abc…");
        assert_eq!(after_first_line("partial\nnext"), "next");
        assert_eq!(after_first_line("none"), "none");
    }
}
