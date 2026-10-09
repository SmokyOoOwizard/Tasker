//! Поля задач: канонизация значений (`FieldValues`).
mod values;

pub use values::{DATE_FORMAT, MAX_STRING_LENGTH, format_double, normalize, texts, try_parse};
