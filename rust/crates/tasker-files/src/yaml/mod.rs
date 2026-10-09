//! YAML файлов `.tasker`: чтение через saphyr в промежуточное дерево ([`reader`]) и собственный эмиттер ([`emitter`]),
//! повторяющий YamlDotNet 18.1 с настройками Tasker байт в байт.
pub mod emitter;
pub mod reader;

pub use emitter::{Node, Style, emit, style_of, write_scalar};
pub use reader::{Document, Mapping, Value, read};
