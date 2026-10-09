//! `users/<id>.yaml` (`UserFile.cs`): `id`, `username`, `kind: agent` (только у агента), `createdAt`. Ни почты, ни пароля,
//! ни признака администратора в файлах нет.
use super::{Entries, Versioned, or_empty, read_document, write_document};
use crate::error::Result;
use std::path::Path;
use tasker_core::model::{User, UserKind};

const AGENT_KIND: &str = "agent";

pub fn parse(bytes: &[u8], path: &Path) -> Result<Versioned<User>> {
    let (document, version) = read_document(bytes, path)?;
    let m = document.mapping();
    let kind = m.str_opt("kind")?;
    Ok(Versioned {
        model: User {
            id: m.guid("id")?,
            username: or_empty(m.str_opt("username")?),
            kind: if kind.as_deref().is_some_and(|k| k.eq_ignore_ascii_case(AGENT_KIND)) {
                UserKind::Agent
            } else {
                UserKind::Human
            },
            owner_id: None,
            email: None,
            is_admin: false,
            created_at: m.timestamp("createdAt")?,
            version: version.clone(),
        },
        version,
    })
}

pub fn serialize(user: &User) -> Vec<u8> {
    write_document(
        Entries::new()
            .guid("id", &user.id)
            .str("username", &user.username)
            .str_opt("kind", user.is_agent().then_some(AGENT_KIND))
            .timestamp("createdAt", &user.created_at)
            .into_vec(),
    )
}
