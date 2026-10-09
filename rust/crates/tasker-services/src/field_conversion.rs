//! Преобразование значений задач при правке определения поля (`FieldConversion` в .NET): тип, множественность, перечисление.
//! Допустимые смены типа: любой → string, int → float, string → int/float/bool/date/enum, enum → другое перечисление. Остальные
//! теряли бы данные: через string их можно сделать в два шага.
use crate::error::{Error, Result};
use tasker_core::fields;
use tasker_core::ids::{guid_d, parse_guid};
use tasker_core::model::{FieldDefinition, FieldEnum, FieldEnumValue, FieldType};
use tasker_core::validate::eq_ignore_case;
use uuid::Uuid;

/// Что сделать у задачи с несколькими значениями, когда поле становится «с одним значением».
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum SeveralValues {
    /// Оставить первое значение, остальные убрать.
    KeepFirst,
    /// Убрать все значения.
    Clear,
}

/// Соответствие значения при смене перечисления: старое значение → значение нового перечисления (id или название).
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct FieldValueMapping {
    pub from: String,
    pub to: String,
}

/// Явный выбор пользователя при правке поля, которая затрагивает значения задач.
#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct FieldChangeChoice {
    /// Значения, которые не получилось привести, убрать у задач (остальные преобразуются).
    pub clear_unconvertible: bool,
    pub several: Option<SeveralValues>,
    /// Явное соответствие значений для нового перечисления; только при новом типе enum.
    pub mapping: Option<Vec<FieldValueMapping>>,
}

/// Результат для значений одного поля одной задачи.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ConversionResult {
    /// Новые значения (без повторов), с применённым выбором для «нескольких значений».
    pub values: Vec<String>,
    /// Прежние значения (для показа), которые привести не удалось.
    pub unconvertible: Vec<String>,
    /// Значений несколько, а поле станет «с одним»; без выбора — это проблема.
    pub several: bool,
}

pub struct FieldConversion {
    old: FieldDefinition,
    old_enum: Option<FieldEnum>,
    new: FieldDefinition,
    new_enum: Option<FieldEnum>,
    choice: Option<FieldChangeChoice>,
    /// Ключ — каноническое прежнее значение (у enum — id), без учёта регистра; значение — id значения нового перечисления.
    mapping: Vec<(String, Uuid)>,
}

fn type_name(t: FieldType) -> &'static str {
    t.name()
}

impl FieldConversion {
    pub fn create(
        old: &FieldDefinition,
        old_enum: Option<&FieldEnum>,
        updated: &FieldDefinition,
        new_enum: Option<&FieldEnum>,
        choice: Option<&FieldChangeChoice>,
    ) -> Result<FieldConversion> {
        Self::ensure_allowed(old.field_type, updated.field_type)?;
        let mut conversion = FieldConversion {
            old: old.clone(),
            old_enum: old_enum.cloned(),
            new: updated.clone(),
            new_enum: new_enum.cloned(),
            choice: choice.cloned(),
            mapping: Vec::new(),
        };
        if let Some(mapping) = choice.and_then(|c| c.mapping.as_ref())
            && !mapping.is_empty()
        {
            conversion.read_mapping(mapping)?;
        }
        Ok(conversion)
    }

    /// Значения задач меняются: другой тип или перечисление, либо «несколько → одно».
    pub fn changes_values(&self) -> bool {
        self.old.field_type != self.new.field_type || self.old.enum_id != self.new.enum_id || (self.old.multiple && !self.new.multiple)
    }

    fn ensure_allowed(from: FieldType, to: FieldType) -> Result<()> {
        let allowed = from == to
            || to == FieldType::String
            || (from == FieldType::Int && to == FieldType::Float)
            || (from == FieldType::String
                && matches!(
                    to,
                    FieldType::Int | FieldType::Float | FieldType::Bool | FieldType::Date | FieldType::Enum
                ));
        if allowed {
            Ok(())
        } else {
            Err(Error::validation(format!(
                "Type: cannot change a field from {} to {} directly (it would lose values): change it to string first, then to the type you need",
                type_name(from),
                type_name(to)
            )))
        }
    }

    fn read_mapping(&mut self, mapping: &[FieldValueMapping]) -> Result<()> {
        if self.new.field_type != FieldType::Enum {
            return Err(Error::validation("Choice.Mapping: only when the new type is enum"));
        }
        for item in mapping {
            let from = item.from.trim();
            if from.is_empty() {
                return Err(Error::validation("Choice.Mapping: 'from' must not be empty"));
            }
            let target = find(self.new_enum.as_ref(), Some(item.to.trim())).map(|x| x.id).ok_or_else(|| {
                Error::validation(format!(
                    "Choice.Mapping: '{}' is not a value of enum '{}'",
                    item.to,
                    self.new_enum.as_ref().map(|e| e.name.as_str()).unwrap_or("")
                ))
            })?;
            let key = if self.old.field_type == FieldType::Enum {
                guid_d(
                    &find(self.old_enum.as_ref(), Some(from))
                        .ok_or_else(|| {
                            Error::validation(format!(
                                "Choice.Mapping: '{from}' is not a value of enum '{}'",
                                self.old_enum.as_ref().map(|e| e.name.as_str()).unwrap_or("")
                            ))
                        })?
                        .id,
                )
            } else {
                from.to_string()
            };
            if self.mapping.iter().any(|(k, _)| eq_ignore_case(k, &key)) {
                return Err(Error::validation(format!("Choice.Mapping: '{from}' is mapped twice")));
            }
            self.mapping.push((key, target));
        }
        Ok(())
    }

    pub fn convert(&self, values: &[String]) -> ConversionResult {
        let mut converted: Vec<String> = Vec::new();
        let mut failed: Vec<String> = Vec::new();
        for value in values {
            match self.one(value) {
                None => failed.push(self.shown(value)),
                Some(result) => {
                    if !converted.contains(&result) {
                        converted.push(result);
                    }
                }
            }
        }
        let several = !self.new.multiple && converted.len() > 1;
        if several {
            converted = match self.choice.as_ref().and_then(|c| c.several) {
                Some(SeveralValues::KeepFirst) => vec![converted[0].clone()],
                _ => vec![],
            };
        }
        ConversionResult {
            values: converted,
            unconvertible: failed,
            several,
        }
    }

    /// Значение для показа: у enum — название, у остальных — само значение.
    fn shown(&self, value: &str) -> String {
        fields::texts(
            self.old.field_type,
            self.old_enum.as_ref(),
            std::slice::from_ref(&value.to_string()),
        )
        .into_iter()
        .next()
        .unwrap_or_default()
    }

    fn one(&self, value: &str) -> Option<String> {
        if self.old.field_type == self.new.field_type && (self.new.field_type != FieldType::Enum || self.old.enum_id == self.new.enum_id) {
            return Some(value.to_string());
        }
        if self.new.field_type == FieldType::Enum {
            if let Some((_, mapped)) = self.mapping.iter().find(|(k, _)| eq_ignore_case(k, value)) {
                return Some(guid_d(mapped));
            }
            // Потерянный id прежнего значения enum сопоставить не с чем.
            if self.old.field_type == FieldType::Enum && find(self.old_enum.as_ref(), Some(value)).is_none() {
                return None;
            }
            return find(self.new_enum.as_ref(), Some(&self.shown(value))).map(|x| guid_d(&x.id));
        }
        let text = self.shown(value);
        if self.new.field_type == FieldType::String {
            return (tasker_core::validate::utf16_len(&text) <= fields::MAX_STRING_LENGTH).then_some(text);
        }
        fields::try_parse(self.new.field_type, &text)
    }
}

fn find<'e>(enumeration: Option<&'e FieldEnum>, reference: Option<&str>) -> Option<&'e FieldEnumValue> {
    let e = enumeration?;
    let reference = reference.filter(|r| !r.is_empty())?;
    if let Some(id) = parse_guid(reference)
        && let Some(by_id) = e.values.iter().find(|x| x.id == id)
    {
        return Some(by_id);
    }
    e.values.iter().find(|x| eq_ignore_case(&x.name, reference))
}
