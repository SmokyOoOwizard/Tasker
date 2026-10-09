//! Конвенции JSON Tasker (`TaskerJson`): camelCase, enum строками, экранирование как у `RelaxedEncoder` — сырой UTF-8,
//! экранируются только `"`, `\\`, знаки < 0x20 (короткие формы `\n \r \t \b \f`, остальные `\u00XX`), 0x7F–0x9F,
//! U+2028/U+2029 (`\uXXXX`, hex заглавными). Используется для `--json` консоли, `settings.json` и MCP.
use serde_json::Value;
use std::fmt::Write as _;

/// Без отступов (`--json` консоли, MCP).
pub fn to_string(value: &Value) -> String {
    let mut out = String::new();
    write_value(&mut out, value, None, 0);
    out
}

/// С отступами System.Text.Json (`WriteIndented = true`): два пробела, `"key": value`, пустые `[]` и `{}`.
pub fn to_string_pretty(value: &Value) -> String {
    let mut out = String::new();
    write_value(&mut out, value, Some(2), 0);
    out
}

pub fn write_string(out: &mut String, text: &str) {
    out.push('"');
    for c in text.chars() {
        match c {
            '"' => out.push_str("\\\""),
            '\\' => out.push_str("\\\\"),
            '\n' => out.push_str("\\n"),
            '\r' => out.push_str("\\r"),
            '\t' => out.push_str("\\t"),
            '\u{8}' => out.push_str("\\b"),
            '\u{c}' => out.push_str("\\f"),
            c if (c as u32) < 0x20 || (0x7F..0xA0).contains(&(c as u32)) || c == '\u{2028}' || c == '\u{2029}' => {
                let _ = write!(out, "\\u{:04X}", c as u32);
            }
            c => out.push(c),
        }
    }
    out.push('"');
}

fn indent(out: &mut String, step: Option<usize>, depth: usize) {
    if let Some(step) = step {
        out.push('\n');
        for _ in 0..step * depth {
            out.push(' ');
        }
    }
}

fn write_value(out: &mut String, value: &Value, step: Option<usize>, depth: usize) {
    match value {
        Value::Null => out.push_str("null"),
        Value::Bool(b) => out.push_str(if *b { "true" } else { "false" }),
        Value::Number(n) => out.push_str(&n.to_string()),
        Value::String(s) => write_string(out, s),
        Value::Array(items) => {
            out.push('[');
            for (i, item) in items.iter().enumerate() {
                if i > 0 {
                    out.push(',');
                }
                indent(out, step, depth + 1);
                write_value(out, item, step, depth + 1);
            }
            if !items.is_empty() {
                indent(out, step, depth);
            }
            out.push(']');
        }
        Value::Object(map) => {
            out.push('{');
            for (i, (key, item)) in map.iter().enumerate() {
                if i > 0 {
                    out.push(',');
                }
                indent(out, step, depth + 1);
                write_string(out, key);
                out.push(':');
                if step.is_some() {
                    out.push(' ');
                }
                write_value(out, item, step, depth + 1);
            }
            if !map.is_empty() {
                indent(out, step, depth);
            }
            out.push('}');
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use serde_json::json;

    #[test]
    fn escaping_matches_relaxed_encoder() {
        let v = json!({"t": "Привет, `мир` «ёлочки» 🙂 <b>&</b> \"кавычки\" \\ \t\n\u{1}\u{7f}\u{2028}"});
        assert_eq!(
            to_string(&v),
            "{\"t\":\"Привет, `мир` «ёлочки» 🙂 <b>&</b> \\\"кавычки\\\" \\\\ \\t\\n\\u0001\\u007F\\u2028\"}"
        );
    }

    #[test]
    fn pretty_matches_system_text_json() {
        let v = json!({"userName": null, "mcp": {"port": 5719, "workspaces": [{"kind": "files", "path": "/x"}]}, "empty": [], "none": {}});
        assert_eq!(
            to_string_pretty(&v),
            "{\n  \"userName\": null,\n  \"mcp\": {\n    \"port\": 5719,\n    \"workspaces\": [\n      {\n        \"kind\": \"files\",\n        \"path\": \"/x\"\n      }\n    ]\n  },\n  \"empty\": [],\n  \"none\": {}\n}"
        );
    }
}
