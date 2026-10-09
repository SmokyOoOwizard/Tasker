use uuid::Uuid;

/// Условия по полям (`FieldOperator`): в файле досок — camelCase (`greaterOrEqual`).
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum FieldOperator {
    Equal,
    NotEqual,
    Greater,
    GreaterOrEqual,
    Less,
    LessOrEqual,
    Set,
    Unset,
    Attached,
    Detached,
}

impl FieldOperator {
    pub const ALL: [FieldOperator; 10] = [
        Self::Equal,
        Self::NotEqual,
        Self::Greater,
        Self::GreaterOrEqual,
        Self::Less,
        Self::LessOrEqual,
        Self::Set,
        Self::Unset,
        Self::Attached,
        Self::Detached,
    ];

    /// Имя в PascalCase (`ToString()`).
    pub fn pascal(self) -> &'static str {
        match self {
            Self::Equal => "Equal",
            Self::NotEqual => "NotEqual",
            Self::Greater => "Greater",
            Self::GreaterOrEqual => "GreaterOrEqual",
            Self::Less => "Less",
            Self::LessOrEqual => "LessOrEqual",
            Self::Set => "Set",
            Self::Unset => "Unset",
            Self::Attached => "Attached",
            Self::Detached => "Detached",
        }
    }

    /// Слово в файле доски: первая буква строчная (`BoardFile.OperatorWord`).
    pub fn word(self) -> String {
        let p = self.pascal();
        let mut s = String::with_capacity(p.len());
        s.push(p.chars().next().unwrap().to_ascii_lowercase());
        s.push_str(&p[1..]);
        s
    }

    /// `Enum.TryParse(ignoreCase: true)`.
    pub fn parse(text: &str) -> Option<Self> {
        Self::ALL.into_iter().find(|op| op.pascal().eq_ignore_ascii_case(text))
    }
}

#[derive(Debug, Clone, PartialEq)]
pub struct ColumnFieldFilter {
    pub field_id: Uuid,
    pub operator: FieldOperator,
    pub value: Option<String>,
}

#[derive(Debug, Clone, PartialEq)]
pub struct BoardColumn {
    pub id: Uuid,
    pub name: String,
    pub status_ids: Vec<Uuid>,
    pub field_conditions: Vec<ColumnFieldFilter>,
    /// Набор статусов → статус, который получает задача, перенесённая в колонку. Порядок как в файле.
    pub drop_statuses: Vec<(Uuid, Uuid)>,
}

impl BoardColumn {
    pub fn drop_status(&self, status_set_id: &Uuid) -> Option<Uuid> {
        self.drop_statuses
            .iter()
            .find(|(set, _)| set == status_set_id)
            .map(|(_, status)| *status)
    }
}

#[derive(Debug, Clone, PartialEq)]
pub struct Board {
    pub id: Uuid,
    pub project_id: Uuid,
    pub name: String,
    pub status_set_ids: Vec<Uuid>,
    pub columns: Vec<BoardColumn>,
    pub version: String,
}
