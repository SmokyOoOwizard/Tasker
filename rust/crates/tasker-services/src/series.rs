//! Серии проекта (`SeriesService`): уникальный префикс под секцией записи, удаление с каскадом — номера этой серии убираются из задач.
use crate::Workspace;
use crate::error::{Error, Result};
use crate::rewrites;
use std::collections::HashMap;
use tasker_core::ids::parse_guid;
use tasker_core::locks::LockedEntity;
use tasker_core::model::{Series, SeriesPrefix, TaskItem};
use tasker_core::tasks::{ListPage, Page, TaskFilter};
use tasker_core::{validate, versioning};
use tasker_files::index::IndexQuery;
use uuid::Uuid;

const CASCADE_ATTEMPTS: usize = 4;

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct CreateSeries {
    pub name: String,
    pub prefix: String,
}

impl CreateSeries {
    pub fn new(name: &str, prefix: &str) -> CreateSeries {
        CreateSeries {
            name: name.to_string(),
            prefix: prefix.to_string(),
        }
    }
}

/// Поля None — не меняются. Смена префикса — переименование серии.
#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct UpdateSeries {
    pub name: Option<String>,
    pub prefix: Option<String>,
    pub version: Option<String>,
}

pub struct SeriesService<'a> {
    ws: &'a Workspace,
}

fn subject(series: &Series) -> String {
    format!("Series '{}'", series.name)
}

impl<'a> SeriesService<'a> {
    pub fn new(ws: &'a Workspace) -> SeriesService<'a> {
        SeriesService { ws }
    }

    pub fn get_by_id(&self, project_id: &Uuid, id: &Uuid) -> Result<Option<Series>> {
        self.ws.get_by_id::<Series>(project_id, id)
    }

    /// Все серии проекта по префиксу, затем по id.
    pub fn get_all(&self, project_id: &Uuid) -> Result<Vec<Series>> {
        self.ws.get_all::<Series>(project_id)
    }

    pub fn get_range(&self, project_id: &Uuid, page: Page) -> Result<ListPage<Series>> {
        self.ws.get_range::<Series>(project_id, page)
    }

    pub fn count_unreadable(&self, project_id: &Uuid) -> Result<usize> {
        self.ws.count_unreadable::<Series>(project_id)
    }

    /// Префикс уже занят в проекте (точное совпадение, регистр важен) — конфликт.
    pub fn create(&self, project_id: &Uuid, command: &CreateSeries) -> Result<Series> {
        let name = validate::entity_name(Some(&command.name), "Series name")?;
        let prefix = SeriesPrefix::validate(Some(&command.prefix))?;
        self.ws.exclusive(project_id, || {
            self.ensure_prefix_free(project_id, &prefix, None)?;
            let mut created = Series {
                id: Uuid::new_v4(),
                project_id: *project_id,
                name: name.clone(),
                prefix: prefix.clone(),
                version: versioning::NEW.to_string(),
            };
            created.version = self.ws.add(&created)?;
            Ok(created)
        })
    }

    /// None — серии нет.
    pub fn update(&self, project_id: &Uuid, id: &Uuid, command: &UpdateSeries) -> Result<Option<Series>> {
        self.ws.exclusive(project_id, || {
            let Some(current) = self.get_by_id(project_id, id)? else {
                return Ok(None);
            };
            let subject = subject(&current);
            self.ws.locks().ensure_writable(LockedEntity::Series, &current.id, &subject)?;
            let expected = versioning::check(&current.version, command.version.as_deref(), &subject)?;

            let mut updated = Series {
                name: match &command.name {
                    None => current.name.clone(),
                    Some(n) => validate::entity_name(Some(n), "Series name")?,
                },
                prefix: match &command.prefix {
                    None => current.prefix.clone(),
                    Some(p) => SeriesPrefix::validate(Some(p))?,
                },
                ..current.clone()
            };
            if updated.prefix != current.prefix {
                self.ensure_prefix_free(project_id, &updated.prefix, Some(id))?;
            }
            let version = self.ws.update(&updated, &expected)?.ok_or_else(|| versioning::modified(&subject))?;
            updated.version = version;
            Ok(Some(updated))
        })
    }

    /// Удаляет серию и в той же операции убирает ссылки на неё из задач (серия удаляется первой). false — серии нет.
    pub fn delete(&self, project_id: &Uuid, id: &Uuid, version: Option<&str>) -> Result<bool> {
        // Задачи серии не должны быть заняты другим: ждём (или отказываем) до секции записи и до первой записи.
        if self.get_by_id(project_id, id)?.is_some() {
            let referencing = self.get_referencing(project_id, id)?;
            rewrites::wait_for_locks(self.ws, &referencing, None)?;
        }
        self.ws.exclusive(project_id, || {
            let Some(current) = self.get_by_id(project_id, id)? else {
                return Ok(false);
            };
            let subject = subject(&current);
            self.ws.locks().ensure_writable(LockedEntity::Series, &current.id, &subject)?;
            let expected = versioning::check(&current.version, version, &subject)?;

            let referencing = self.get_referencing(project_id, id)?;
            rewrites::wait_for_locks(self.ws, &referencing, Some(std::time::Duration::ZERO))?;

            if !self.ws.delete::<Series>(project_id, id, &expected)? {
                return Err(versioning::modified(&subject).into());
            }
            self.ws.locks().forget(LockedEntity::Series, &current.id)?;
            self.remove_references(&referencing, id)?;
            Ok(true)
        })
    }

    /// Серия по id или по префиксу (точное совпадение). None — нет такой.
    pub fn find(&self, project_id: &Uuid, reference: &str) -> Result<Option<Series>> {
        let text = reference.trim();
        if text.is_empty() {
            return Ok(None);
        }
        if let Some(id) = parse_guid(text) {
            return self.get_by_id(project_id, &id);
        }
        Ok(self.get_all(project_id)?.into_iter().find(|x| x.prefix == text))
    }

    /// Префиксы серий проекта по id.
    pub fn prefixes(&self, project_id: &Uuid) -> Result<HashMap<Uuid, String>> {
        Ok(self.get_all(project_id)?.into_iter().map(|x| (x.id, x.prefix)).collect())
    }

    fn ensure_prefix_free(&self, project_id: &Uuid, prefix: &str, except_id: Option<&Uuid>) -> Result<()> {
        if self
            .get_all(project_id)?
            .iter()
            .any(|x| x.prefix == prefix && Some(&x.id) != except_id)
        {
            return Err(Error::in_use(format!("Series prefix '{prefix}' is already used in the project")));
        }
        Ok(())
    }

    /// Все задачи проекта с номером этой серии — страницами со сдвигом.
    fn get_referencing(&self, project_id: &Uuid, series_id: &Uuid) -> Result<Vec<TaskItem>> {
        let filter = TaskFilter {
            series_ids: Some(vec![*series_id]),
            ..TaskFilter::default()
        };
        let mut found: Vec<TaskItem> = Vec::new();
        let mut offset = 0;
        loop {
            let page = self
                .ws
                .index()
                .range::<TaskItem>(&IndexQuery::tasks(project_id, Some(&filter)), Page::new(offset, 200))?;
            if page.data.is_empty() {
                break;
            }
            offset += page.data.len();
            for task in page.data {
                if let Some(existing) = found.iter_mut().find(|x| x.id == task.id) {
                    *existing = task;
                } else {
                    found.push(task);
                }
            }
        }
        Ok(found)
    }

    /// Убирает номера этой серии из задач по одной: сбой на одной не останавливает остальные, первая ошибка — в конце.
    fn remove_references(&self, referencing: &[TaskItem], series_id: &Uuid) -> Result<()> {
        let mut first: Option<Error> = None;
        let series_id = *series_id;
        let change = move |t: &TaskItem| -> Option<TaskItem> {
            t.series_numbers.iter().any(|x| x.series_id == series_id).then(|| TaskItem {
                series_numbers: rewrites::without(t, |x| x.series_id == series_id),
                ..t.clone()
            })
        };
        for task in referencing {
            if let Err(e) = rewrites::modify_all(self.ws, [task], &change, CASCADE_ATTEMPTS) {
                first.get_or_insert(e);
            }
        }
        match first {
            Some(e) => Err(e),
            None => Ok(()),
        }
    }
}
