//! Пользователи и агенты области в файловом режиме — минимум, нужный MCP (`UserStorage` файлового хранилища, `UserService.AddAgent`,
//! `AgentService.EnsureLocalAgent` в .NET): файлы `users/<id>.yaml` — только имена, без почты и паролей; список и поиск по имени —
//! из индекса (имя без учёта регистра и пробелов по краям); уникальность имени и запись — под одной блокировкой папки пользователей.
//!
//! Полный перенос `UserService`/`AgentService` (команды консоли `user`/`agent`) — TSK-135; этот модуль покрывает только то,
//! что нужно `whoami`, `list_users` и локальному агенту.
use crate::Workspace;
use crate::error::{Error, Result};
use tasker_core::model::{User, UserKind};
use tasker_core::tasks::{ListPage, Page};
use tasker_core::{validate, versioning};
use tasker_files::files::user as user_file;
use tasker_files::index::{IndexQuery, normalize_username};
use tasker_files::write;
use uuid::Uuid;

/// Имя локального агента десктопа и демона (`AgentService.LocalAgentName`).
pub const LOCAL_AGENT_NAME: &str = "agent";

pub struct UserService<'a> {
    ws: &'a Workspace,
}

impl<'a> UserService<'a> {
    pub fn new(ws: &'a Workspace) -> UserService<'a> {
        UserService { ws }
    }

    /// Пользователь по id — из файла `users/<id>.yaml`; None — файла нет.
    pub fn get_by_id(&self, id: &Uuid) -> Result<Option<User>> {
        let path = self.ws.directory().user_file(id);
        match write::read_bytes(&path)? {
            None => Ok(None),
            Some(bytes) => Ok(Some(user_file::parse(&bytes, &path)?.model)),
        }
    }

    /// Пользователь по имени без учёта регистра (`UserStorage.GetByUsername`).
    pub fn get_by_username(&self, username: &str) -> Result<Option<User>> {
        Ok(self
            .ws
            .index()
            .first::<User>(&IndexQuery::users(None).with_sort_key(&normalize_username(username)))?)
    }

    /// Страница пользователей по имени; `kind` None — все (`UserStorage.GetRange`).
    pub fn get_range(&self, kind: Option<UserKind>, page: Page) -> Result<ListPage<User>> {
        Ok(self.ws.index().range::<User>(&IndexQuery::users(kind), page)?)
    }

    /// Пользователь-агент без почты и пароля (`UserService.AddAgent`): имя проверяется, уникальность — под блокировкой папки
    /// пользователей, как в файловом `UserStorage.Add`.
    pub fn add_agent(&self, username: Option<&str>) -> Result<User> {
        let name = validate::username(username)?;
        let user = User {
            id: Uuid::new_v4(),
            username: name,
            kind: UserKind::Agent,
            owner_id: None,
            email: None,
            is_admin: false,
            created_at: self.ws.now(),
            version: versioning::NEW.to_string(),
        };
        let version = self.add(&user)?;
        Ok(User { version, ..user })
    }

    /// Агент, от имени которого работает MCP без токена (`AgentService.EnsureLocalAgent`): создаётся при первом обращении; если имя
    /// «agent» занято человеком — берётся следующее свободное (`agent-2`, …).
    pub fn ensure_local_agent(&self) -> Result<User> {
        let mut name = LOCAL_AGENT_NAME.to_string();
        let mut i = 2;
        while let Some(taken) = self.get_by_username(&name)? {
            if taken.is_agent() && taken.owner_id.is_none() {
                return Ok(taken);
            }
            name = format!("{LOCAL_AGENT_NAME}-{i}");
            i += 1;
        }
        self.add_agent(Some(&name))
    }

    fn add(&self, user: &User) -> Result<String> {
        let directory = self.ws.directory();
        directory.ensure_created()?;
        let users = directory.users();
        let path = directory.user_file(&user.id);
        let bytes = user_file::serialize(user);
        let result: std::io::Result<Result<String>> = write::locked(&users, || {
            match self.get_by_username(&user.username) {
                Ok(Some(taken)) if taken.id != user.id => {
                    return Ok(Err(Error::in_use(format!("Username '{}' is already taken", user.username))));
                }
                Ok(_) => {}
                Err(e) => return Ok(Err(e)),
            }
            self.ws
                .index()
                .written(std::slice::from_ref(&path), || write::write(&path, &bytes))
                .map(Ok)
        });
        result?
    }
}
