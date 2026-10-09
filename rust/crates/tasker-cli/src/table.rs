//! Таблица-строки для консоли (`Table` в .NET): колонки выравниваются по самой широкой ячейке (ширина — в знаках терминала,
//! а не в символах), между колонками два пробела, последняя колонка не дополняется. Колонка, пустая во всех строках, не занимает
//! места; пустая ячейка в остальных занимает место своей колонки. Если задан предел ширины, строка, не помещающаяся в него,
//! обрезается справа с «…»: так как последняя колонка (заголовок) идёт в конце строки, укорачивается именно она.
use unicode_general_category::{GeneralCategory, get_general_category};
use unicode_segmentation::UnicodeSegmentation;

const SEPARATOR: &str = "  ";

/// Многоточие, которым заканчивается обрезанный текст.
pub const ELLIPSIS: &str = "…";

/// Строки таблицы; `indent` добавляется перед каждой строкой. Ширина колонок считается по переданным строкам (по странице).
/// `max_width` — предел ширины строки в знаках вместе с отступом; None — не обрезать.
pub fn format<S: AsRef<str>>(rows: &[Vec<S>], indent: &str, max_width: Option<usize>) -> Vec<String> {
    let count = rows.iter().map(Vec::len).max().unwrap_or(0);
    let mut widths = vec![0; count];
    for row in rows {
        for (i, cell) in row.iter().enumerate().take(row.len().saturating_sub(1)) {
            widths[i] = widths[i].max(width(cell.as_ref()));
        }
    }

    rows.iter()
        .map(|row| {
            let mut line = String::from(indent);
            let mut first = true;
            for (i, cell) in row.iter().enumerate() {
                let last = i == row.len() - 1;
                if !last && widths[i] == 0 {
                    continue; // колонка пуста во всех строках
                }
                if !first {
                    line.push_str(SEPARATOR);
                }
                first = false;
                line.push_str(cell.as_ref());
                if !last {
                    line.extend(std::iter::repeat_n(' ', widths[i] - width(cell.as_ref())));
                }
            }
            match max_width {
                Some(limit) => truncate(&line, limit),
                None => line,
            }
        })
        .collect()
}

/// Текст не шире `limit` знаков: если не помещается, обрезается справа и заканчивается «…» (он входит в ширину).
/// Режет по графемам, поэтому суррогатные пары, эмодзи с модификаторами и буквы с комбинируемыми знаками не разрываются.
pub fn truncate(text: &str, limit: usize) -> String {
    if width(text) <= limit {
        return text.to_string();
    }
    let keep = limit.saturating_sub(width(ELLIPSIS));
    let mut result = String::new();
    let mut current = 0;
    for element in text.graphemes(true) {
        let next = current + element_width(element);
        if next > keep {
            break;
        }
        result.push_str(element);
        current = next;
    }
    result.push_str(ELLIPSIS);
    result
}

/// Ширина текста в знаках терминала: кириллица и латиница — 1, CJK и эмодзи — 2, комбинируемые знаки — 0.
pub fn width(text: &str) -> usize {
    text.graphemes(true).map(element_width).sum()
}

fn element_width(element: &str) -> usize {
    let first = element.chars().next().expect("a grapheme has a character");
    if element.contains('\u{FE0F}') {
        return 2; // вариант «эмодзи»
    }
    if is_wide(first as u32) {
        return 2;
    }
    match get_general_category(first) {
        GeneralCategory::NonspacingMark | GeneralCategory::EnclosingMark | GeneralCategory::Format | GeneralCategory::Control => 0,
        _ => 1,
    }
}

fn is_wide(c: u32) -> bool {
    matches!(c,
        0x1100..=0x115F
        | 0x2E80..=0x303E
        | 0x3041..=0xA4CF
        | 0xAC00..=0xD7A3
        | 0xF900..=0xFAFF
        | 0xFE30..=0xFE6F
        | 0xFF00..=0xFF60
        | 0xFFE0..=0xFFE6
        | 0x1F1E6..=0x1F1FF // региональные индикаторы (флаги)
        | 0x1F300..=0x1F64F
        | 0x1F680..=0x1F6FF
        | 0x1F900..=0x1F9FF
        | 0x1FA70..=0x1FAFF
        | 0x20000..=0x3FFFD
        | 0x231A | 0x231B | 0x23E9..=0x23EC | 0x23F0 | 0x23F3 | 0x25FD | 0x25FE
        | 0x2614 | 0x2615 | 0x2648..=0x2653 | 0x267F | 0x2693 | 0x26A1 | 0x26AA | 0x26AB
        | 0x26BD | 0x26BE | 0x26C4 | 0x26C5 | 0x26CE | 0x26D4 | 0x26EA | 0x26F2 | 0x26F3 | 0x26F5
        | 0x26FA | 0x26FD | 0x2705 | 0x270A | 0x270B | 0x2728 | 0x274C | 0x274E
        | 0x2753..=0x2755 | 0x2757 | 0x2795..=0x2797 | 0x27B0 | 0x27BF
        | 0x2B1B | 0x2B1C | 0x2B50 | 0x2B55)
}

#[cfg(test)]
mod tests {
    use super::*;

    fn f(indent: &str, rows: &[&[&str]]) -> Vec<String> {
        let rows: Vec<Vec<&str>> = rows.iter().map(|r| r.to_vec()).collect();
        format(&rows, indent, None)
    }

    fn t(rows: &[&[&str]], indent: &str, limit: usize) -> Vec<String> {
        let rows: Vec<Vec<&str>> = rows.iter().map(|r| r.to_vec()).collect();
        format(&rows, indent, Some(limit))
    }

    // ---- TableTests ----

    #[test]
    fn references_of_different_length_are_aligned() {
        assert_eq!(
            f(
                "",
                &[
                    &["T-7", "Не начата", "Баг", "Один"],
                    &["T-10", "В работе", "Баг", "Два"],
                    &["TSK-70", "Готово", "Баг", "Три"]
                ]
            ),
            [
                "T-7     Не начата  Баг  Один",
                "T-10    В работе   Баг  Два",
                "TSK-70  Готово     Баг  Три"
            ]
        );
    }

    #[test]
    fn width_is_the_display_width_not_the_utf16_length() {
        assert_eq!(width("я"), 1);
        assert_eq!(width("🎉"), 2);
        assert_eq!(width("日本"), 4);
        assert_eq!(width("é"), 1);
        assert_eq!(width("❤️"), 2);
        assert_eq!(width("🇷🇺"), 2);
        assert_eq!(
            f("", &[&["A", "🎉 раз", "x"], &["B", "日本", "y"], &["C", "é", "z"]]),
            ["A  🎉 раз  x", "B  日本    y", "C  é       z"]
        );
    }

    #[test]
    fn the_last_column_is_not_padded_and_an_empty_cell_keeps_the_place() {
        assert_eq!(
            f(
                "",
                &[&["TSK-1", "Todo", "Bug", "Длинный заголовок"], &["TSK-2", "Done", "", "Короткий"]]
            ),
            ["TSK-1  Todo  Bug  Длинный заголовок", "TSK-2  Done       Короткий"]
        );
    }

    #[test]
    fn a_column_empty_in_every_row_is_left_out_and_the_indent_is_added() {
        assert_eq!(
            f("  ", &[&["TSK-1", "Todo", "", "One"], &["TSK-22", "Todo", "", "Two"]]),
            ["  TSK-1   Todo  One", "  TSK-22  Todo  Two"]
        );
    }

    #[test]
    fn an_empty_list_is_empty() {
        assert!(f("", &[]).is_empty());
    }

    // ---- TruncateTests ----

    #[test]
    fn a_line_that_fits_is_left_alone() {
        assert_eq!(t(&[&["TSK-1", "Todo", "Bug", "Short"]], "", 40), ["TSK-1  Todo  Bug  Short"]);
        assert_eq!(t(&[&["TSK-1", "Todo", "Bug", "Short"]], "", 23), ["TSK-1  Todo  Bug  Short"]);
    }

    #[test]
    fn the_last_column_is_cut_with_an_ellipsis_and_the_others_stay_aligned() {
        let lines = t(
            &[
                &["TSK-1", "Todo", "Bug", "A very long title of a task"],
                &["TSK-10", "Done", "", "Another very long title"],
            ],
            "",
            25,
        );
        assert_eq!(lines, ["TSK-1   Todo  Bug  A ver…", "TSK-10  Done       Anoth…"]);
        assert!(lines.iter().all(|x| width(x) == 25));
    }

    #[test]
    fn the_indent_counts_towards_the_width() {
        assert_eq!(
            t(&[&["TSK-1", "Todo", "Bug", "Long long title"]], "  ", 25),
            ["  TSK-1  Todo  Bug  Long…"]
        );
    }

    #[test]
    fn cyrillic_is_one_column_and_emoji_and_cjk_are_two_at_the_boundary() {
        assert_eq!(truncate("Заголовок задачи", 8), "Заголов…");
        assert_eq!(truncate("Ab🎉🎉🎉", 5), "Ab🎉…");
        assert_eq!(truncate("Ab🎉🎉🎉", 4), "Ab…");
        assert_eq!(truncate("日本語の題名", 6), "日本…");
        assert_eq!(truncate("日本語の題名", 4), "日…");
    }

    #[test]
    fn graphemes_and_surrogate_pairs_are_never_split() {
        let text = "e\u{301}e\u{301}e\u{301}e\u{301}e\u{301}";
        assert_eq!(truncate(text, 3), "e\u{301}e\u{301}…");
        assert_eq!(truncate("🇷🇺🇷🇺🇷🇺", 3), "🇷🇺…");
        assert_eq!(truncate("👨‍👩‍👧👨‍👩‍👧", 3), "👨‍👩‍👧…");
    }

    #[test]
    fn a_title_shorter_than_the_width_is_not_touched_and_the_head_longer_than_the_width_is_cut() {
        assert_eq!(truncate("TSK-1  Todo", 20), "TSK-1  Todo");
        assert_eq!(truncate("TSK-1  Todo  Bug  Title", 16), "TSK-1  Todo  Bu…");
        assert_eq!(truncate("long", 1), "…");
    }
}
