//! Пользователи и агенты (`UserService`, `AgentService` в .NET) поверх файлов `users/<id>.yaml` (`UserStorage` файлового
//! хранилища): в файлах только имена — ни почты, ни паролей, ни администраторов, прав нет (десктопный режим,
//! `UserOptions.RequireCredentials = false`). Имя уникально среди всех пользователей, включая агентов, без учёта регистра.
use crate::error::{Error, Result};
use tasker_core::locks::LockedEntity;
use tasker_core::model::{User, UserKind};
use tasker_core::tasks::{ListPage, Page};
use tasker_core::{validate, versioning};
use tasker_files::files;
use tasker_files::index::IndexQuery;
use tasker_files::index::normalize_username;
use tasker_files::write;
use uuid::Uuid;

/// `AgentService.LocalAgentName`: агент, от имени которого работает MCP без токена.
pub const LOCAL_AGENT_NAME: &str = "agent";

/// Поля None — не меняются; `version` — версия, которую видел клиент.
#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct UpdateUser {
    pub username: Option<String>,
    pub version: Option<String>,
}

pub struct UserService<'a> {
    ws: &'a crate::Workspace,
}

fn subject(user: &User) -> String {
    if user.is_agent() {
        format!("Agent '{}'", user.username)
    } else {
        format!("User '{}'", user.username)
    }
}

impl<'a> UserService<'a> {
    pub fn new(ws: &'a crate::Workspace) -> UserService<'a> {
        UserService { ws }
    }

    /// Пользователь по id — из файла; None — нет.
    pub fn get_by_id(&self, id: &Uuid) -> Result<Option<User>> {
        let path = self.ws.directory().user_file(id);
        let Some(bytes) = write::read_bytes(&path)? else {
            return Ok(None);
        };
        match files::user::parse(&bytes, &path) {
            Ok(versioned) => Ok(Some(versioned.model)),
            Err(tasker_files::Error::Yaml(message)) if message.ends_with(": the file is empty") => Ok(None),
            Err(e) => Err(e.into()),
        }
    }

    /// Без учёта регистра (`UserStorage.GetByUsername`).
    pub fn get_by_username(&self, username: &str) -> Result<Option<User>> {
        Ok(self
            .ws
            .index()
            .first::<User>(&IndexQuery::users(None).with_sort_key(&normalize_username(username)))?)
    }

    /// Страница пользователей по имени; `kind` None — все.
    pub fn get_range(&self, kind: Option<UserKind>, page: Page) -> Result<ListPage<User>> {
        Ok(self.ws.index().range::<User>(&IndexQuery::users(kind), page)?)
    }

    pub fn get_all(&self, kind: Option<UserKind>) -> Result<Vec<User>> {
        Ok(self.ws.index().all::<User>(&IndexQuery::users(kind))?)
    }

    /// Локальный пользователь: только имя (`CreateUser` без почты и пароля).
    pub fn create(&self, username: &str) -> Result<User> {
        self.add(username, UserKind::Human)
    }

    /// Пользователь-агент без почты и пароля (`UserService.AddAgent`).
    pub fn add_agent(&self, username: &str) -> Result<User> {
        self.add(username, UserKind::Agent)
    }

    fn add(&self, username: &str, kind: UserKind) -> Result<User> {
        let name = validate::username(Some(username))?;
        let user = User {
            id: Uuid::new_v4(),
            username: name,
            kind,
            owner_id: None,
            email: None,
            is_admin: false,
            created_at: self.ws.now(),
            version: String::new(),
        };
        // Проверка уникальности и запись — под одной блокировкой каталога пользователей.
        let path = self.ws.directory().user_file(&user.id);
        let bytes = files::user::serialize(&user);
        let version: Result<String> = write::locked(&self.ws.directory().users(), || {
            Ok(self.ensure_unique(&user.username, None).and_then(|()| {
                Ok(self
                    .ws
                    .index()
                    .written(std::slice::from_ref(&path), || write::write(&path, &bytes))?)
            }))
        })?;
        Ok(User { version: version?, ..user })
    }

    /// Имя уникально среди всех пользователей, включая агентов.
    pub fn ensure_unique(&self, username: &str, except: Option<&Uuid>) -> Result<()> {
        if let Some(taken) = self.get_by_username(username)?
            && except != Some(&taken.id)
        {
            return Err(Error::in_use(format!("Username '{username}' is already taken")));
        }
        Ok(())
    }

    fn write(&self, user: &User, expected: &str) -> Result<Option<User>> {
        let path = self.ws.directory().user_file(&user.id);
        let bytes = files::user::serialize(user);
        let version: Result<Option<String>> = write::locked(&self.ws.directory().users(), || {
            Ok(self.ensure_unique(&user.username, Some(&user.id)).and_then(|()| {
                Ok(self
                    .ws
                    .index()
                    .written(std::slice::from_ref(&path), || write::write_if_match(&path, &bytes, expected))?)
            }))
        })?;
        Ok(version?.map(|version| User { version, ..user.clone() }))
    }

    /// Переименование человека; None — пользователя нет.
    pub fn update(&self, id: &Uuid, command: &UpdateUser) -> Result<Option<User>> {
        let Some(user) = self.get_by_id(id)? else {
            return Ok(None);
        };
        let subject = subject(&user);
        self.ws.locks().ensure_writable(LockedEntity::User, &user.id, &subject)?;
        let expected = versioning::check(&user.version, command.version.as_deref(), &subject)?;
        ensure_human(&user)?;
        let username = match &command.username {
            Some(name) => validate::username(Some(name))?,
            None => user.username.clone(),
        };
        let updated = User { username, ..user.clone() };
        match self.write(&updated, &expected)? {
            Some(saved) => Ok(Some(saved)),
            None => Err(versioning::modified(&subject).into()),
        }
    }

    /// Удаление человека; false — пользователя нет.
    pub fn delete(&self, id: &Uuid, version: Option<&str>) -> Result<bool> {
        let Some(user) = self.get_by_id(id)? else {
            return Ok(false);
        };
        let subject = subject(&user);
        self.ws.locks().ensure_writable(LockedEntity::User, &user.id, &subject)?;
        let expected = versioning::check(&user.version, version, &subject)?;
        ensure_human(&user)?;
        self.remove(&user, &expected, &subject)
    }

    fn remove(&self, user: &User, expected: &str, subject: &str) -> Result<bool> {
        let path = self.ws.directory().user_file(&user.id);
        let deleted = self
            .ws
            .index()
            .written(std::slice::from_ref(&path), || write::delete_if_match(&path, expected))?;
        if !deleted {
            return Err(versioning::modified(subject).into());
        }
        self.ws.locks().forget(LockedEntity::User, &user.id)?;
        Ok(true)
    }
}

fn ensure_human(user: &User) -> Result<()> {
    if user.is_agent() {
        return Err(Error::validation(format!(
            "'{}' is an agent: manage it through /api/agents",
            user.username
        )));
    }
    Ok(())
}

/// Агенты — пользователи с `UserKind::Agent`; на десктопе входа нет, агенты общие.
pub struct AgentService<'a> {
    ws: &'a crate::Workspace,
}

impl<'a> AgentService<'a> {
    pub fn new(ws: &'a crate::Workspace) -> AgentService<'a> {
        AgentService { ws }
    }

    fn users(&self) -> UserService<'a> {
        UserService::new(self.ws)
    }

    pub fn get_range(&self, page: Page) -> Result<ListPage<User>> {
        self.users().get_range(Some(UserKind::Agent), page)
    }

    /// None — агента нет (или это человек).
    pub fn get_by_id(&self, id: &Uuid) -> Result<Option<User>> {
        Ok(self.users().get_by_id(id)?.filter(User::is_agent))
    }

    pub fn create(&self, username: &str) -> Result<User> {
        self.users().add_agent(username)
    }

    pub fn update(&self, id: &Uuid, command: &UpdateUser) -> Result<Option<User>> {
        let Some(agent) = self.get_by_id(id)? else {
            return Ok(None);
        };
        let subject = subject(&agent);
        self.ws.locks().ensure_writable(LockedEntity::User, &agent.id, &subject)?;
        let expected = versioning::check(&agent.version, command.version.as_deref(), &subject)?;
        let username = match &command.username {
            Some(name) => validate::username(Some(name))?,
            None => agent.username.clone(),
        };
        let updated = User { username, ..agent.clone() };
        match self.users().write(&updated, &expected)? {
            Some(saved) => Ok(Some(saved)),
            None => Err(versioning::modified(&subject).into()),
        }
    }

    pub fn delete(&self, id: &Uuid, version: Option<&str>) -> Result<bool> {
        let Some(agent) = self.get_by_id(id)? else {
            return Ok(false);
        };
        let subject = subject(&agent);
        self.ws.locks().ensure_writable(LockedEntity::User, &agent.id, &subject)?;
        let expected = versioning::check(&agent.version, version, &subject)?;
        self.users().remove(&agent, &expected, &subject)
    }

    /// Агент, от имени которого работает MCP без токена. Создаётся при первом обращении; имя «agent» занято человеком — берётся
    /// следующее свободное (`agent-2`, …).
    pub fn ensure_local_agent(&self) -> Result<User> {
        let users = self.users();
        let mut name = LOCAL_AGENT_NAME.to_string();
        let mut i = 2;
        while let Some(taken) = users.get_by_username(&name)? {
            if taken.is_agent() && taken.owner_id.is_none() {
                return Ok(taken);
            }
            name = format!("{LOCAL_AGENT_NAME}-{i}");
            i += 1;
        }
        users.add_agent(&name)
    }
}
