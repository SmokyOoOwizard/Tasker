//! Типы связей проекта (`LinkTypeService`): пока в проекте нет ни одного сохранённого типа, чтение показывает типы по умолчанию
//! «виртуально» (те же детерминированные id, ничего не записывая); записываются они при первой записи
//! ([`LinkTypeService::ensure_defaults`]). «Parent/Child», добавленный в набор позже, в проектах с уже сохранёнными типами
//! показывается и записывается так же, пока в проекте нет иерархического типа и типа с таким названием.
use crate::Workspace;
use crate::error::{Error, Result};
use crate::usages::Usages;
use tasker_core::ids::{self, DEFAULT_LINK_TYPES, DefaultLinkType, guid_d, parse_guid};
use tasker_core::locks::LockedEntity;
use tasker_core::model::LinkType;
use tasker_core::tasks::{ListPage, Page, TaskFilter};
use tasker_core::validate::{self, eq_ignore_case};
use tasker_core::versioning;
use tasker_files::index::IndexQuery;
use tasker_files::layout::EntityKind;
use uuid::Uuid;

/// Версия типа по умолчанию, которого ещё нет в хранилище.
pub const DEFAULT_VERSION: &str = "default";

/// `inward_name` None — как `outward_name` (связь без направления); `allow_cycles` None — допускает; `hierarchical` — иерархический тип.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct CreateLinkType {
    pub name: String,
    pub outward_name: String,
    pub inward_name: Option<String>,
    pub allow_cycles: Option<bool>,
    pub hierarchical: Option<bool>,
}

impl CreateLinkType {
    pub fn new(name: &str, outward: &str, inward: Option<&str>) -> CreateLinkType {
        CreateLinkType {
            name: name.to_string(),
            outward_name: outward.to_string(),
            inward_name: inward.map(str::to_string),
            allow_cycles: None,
            hierarchical: None,
        }
    }
}

/// Поля None — не меняются.
#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct UpdateLinkType {
    pub name: Option<String>,
    pub outward_name: Option<String>,
    pub inward_name: Option<String>,
    pub version: Option<String>,
    pub allow_cycles: Option<bool>,
    pub hierarchical: Option<bool>,
}

/// С какой стороны связи смотрит задача (для [`LinkTypeService::resolve`]).
pub use crate::links::LinkDirection;

pub struct LinkTypeService<'a> {
    ws: &'a Workspace,
}

fn subject(link_type: &LinkType) -> String {
    format!("Link type '{}'", link_type.name)
}

/// Типы по умолчанию проекта: по имени, затем по id (`LinkTypeService.Defaults`).
pub fn defaults(project_id: &Uuid) -> Vec<LinkType> {
    defaults_of(project_id, DEFAULT_LINK_TYPES.iter())
}

fn defaults_of<'d>(project_id: &Uuid, definitions: impl Iterator<Item = &'d DefaultLinkType>) -> Vec<LinkType> {
    let mut result: Vec<LinkType> = definitions
        .map(|x| LinkType {
            id: ids::default_link_type_id(project_id, x.key),
            project_id: *project_id,
            name: x.name.to_string(),
            outward_name: x.outward.to_string(),
            inward_name: x.inward.to_string(),
            allow_cycles: x.allow_cycles,
            hierarchical: x.hierarchical,
            version: DEFAULT_VERSION.to_string(),
        })
        .collect();
    result.sort_by(|a, b| tasker_core::tasks::ordinal(&a.name, &b.name).then_with(|| a.id.cmp(&b.id)));
    result
}

/// Типы, добавленные в набор позже и отсутствующие в проекте с уже сохранёнными типами: нужны, пока нет иерархического типа и типа с
/// этим названием.
fn missing(project_id: &Uuid, stored: &[LinkType]) -> Vec<LinkType> {
    if stored.iter().any(|x| x.hierarchical) {
        return vec![];
    }
    defaults_of(project_id, DEFAULT_LINK_TYPES.iter().filter(|x| x.hierarchical))
        .into_iter()
        .filter(|x| !stored.iter().any(|s| s.id == x.id || eq_ignore_case(&s.name, &x.name)))
        .collect()
}

impl<'a> LinkTypeService<'a> {
    pub fn new(ws: &'a Workspace) -> LinkTypeService<'a> {
        LinkTypeService { ws }
    }

    pub fn count_unreadable(&self, project_id: &Uuid) -> Result<usize> {
        self.ws.count_unreadable::<LinkType>(project_id)
    }

    /// Сохранённые типы проекта; нет ни одного — типы по умолчанию (ничего не записывая).
    pub fn get_all(&self, project_id: &Uuid) -> Result<Vec<LinkType>> {
        let stored = self.ws.get_all::<LinkType>(project_id)?;
        if stored.is_empty() {
            return Ok(defaults(project_id));
        }
        let later = missing(project_id, &stored);
        if later.is_empty() {
            return Ok(stored);
        }
        let mut all = stored;
        all.extend(later);
        all.sort_by(|a, b| tasker_core::tasks::ordinal(&a.name, &b.name).then_with(|| a.id.cmp(&b.id)));
        Ok(all)
    }

    pub fn get_range(&self, project_id: &Uuid, page: Page) -> Result<ListPage<LinkType>> {
        let stored = self.ws.get_range::<LinkType>(project_id, page)?;
        if stored.total_count == 0 {
            return Ok(apply(page, defaults(project_id)));
        }
        if missing(project_id, &self.ws.get_all::<LinkType>(project_id)?).is_empty() {
            Ok(stored)
        } else {
            Ok(apply(page, self.get_all(project_id)?))
        }
    }

    /// None — типа нет.
    pub fn get_by_id(&self, project_id: &Uuid, id: &Uuid) -> Result<Option<LinkType>> {
        if let Some(stored) = self.ws.get_by_id::<LinkType>(project_id, id)? {
            return Ok(Some(stored));
        }
        let all = self.ws.get_all::<LinkType>(project_id)?;
        let candidates = if all.is_empty() {
            defaults(project_id)
        } else {
            missing(project_id, &all)
        };
        Ok(candidates.into_iter().find(|x| x.id == *id))
    }

    /// Записывает типы по умолчанию, если в проекте нет ни одного сохранённого; в проекте с типами — только «позже добавленные».
    /// Id детерминированные: проигравший гонку просто видит уже созданное.
    pub fn ensure_defaults(&self, project_id: &Uuid) -> Result<()> {
        let to_add = |stored: &[LinkType]| {
            if stored.is_empty() {
                defaults(project_id)
            } else {
                missing(project_id, stored)
            }
        };
        if to_add(&self.ws.get_all::<LinkType>(project_id)?).is_empty() {
            return Ok(());
        }
        // Записать есть что — в секции записи проекта и с повторным чтением: иначе параллельный запрос, увидев уже записанную часть
        // типов по умолчанию (набор не пуст), счёл бы остальные удалёнными и не нашёл бы свой тип (TSK-157).
        self.ws.exclusive(project_id, || {
            self.add_defaults(project_id, to_add(&self.ws.get_all::<LinkType>(project_id)?))
        })
    }

    fn add_defaults(&self, project_id: &Uuid, to_add: Vec<LinkType>) -> Result<()> {
        for definition in to_add {
            let link_type = LinkType {
                version: versioning::NEW.to_string(),
                ..definition
            };
            if let Err(e) = self.ws.add(&link_type) {
                // Тот же тип мог только что создать другой запрос или процесс: тогда он уже есть и всё в порядке.
                if self.ws.get_by_id::<LinkType>(project_id, &link_type.id)?.is_none() {
                    return Err(e);
                }
            }
        }
        Ok(())
    }

    /// Тип по id или по названию (без учёта регистра). Несколько типов с одним названием — ошибка. None — нет.
    pub fn find(&self, project_id: &Uuid, reference: &str) -> Result<Option<LinkType>> {
        let text = reference.trim();
        if text.is_empty() {
            return Ok(None);
        }
        let all = self.get_all(project_id)?;
        if let Some(id) = parse_guid(text)
            && let Some(by_id) = all.iter().find(|x| x.id == id)
        {
            return Ok(Some(by_id.clone()));
        }
        let by_name: Vec<&LinkType> = all.iter().filter(|x| eq_ignore_case(&x.name, text)).collect();
        match by_name.len() {
            0 => Ok(None),
            1 => Ok(Some(by_name[0].clone())),
            _ => Err(Error::validation(format!(
                "Several link types are named '{text}', use the id: {}",
                by_name.iter().map(|x| guid_d(&x.id)).collect::<Vec<_>>().join(", ")
            ))),
        }
    }

    /// Тип и сторона связи по тому, что написал человек: id или название типа (исходящая) либо любое из названий сторон.
    pub fn resolve(&self, project_id: &Uuid, phrase: &str) -> Result<(LinkType, LinkDirection)> {
        let text = phrase.trim();
        if text.is_empty() {
            return Err(Error::validation("Link type is required"));
        }
        let all = self.get_all(project_id)?;
        if let Some(id) = parse_guid(text)
            && let Some(by_id) = all.iter().find(|x| x.id == id)
        {
            return Ok((by_id.clone(), LinkDirection::Outward));
        }
        let same = |a: &str, b: &str| eq_ignore_case(a.trim(), b);
        let mut found: Vec<(LinkType, LinkDirection)> = Vec::new();
        for link_type in &all {
            if same(&link_type.name, text) || same(&link_type.outward_name, text) {
                found.push((link_type.clone(), LinkDirection::Outward));
            } else if same(&link_type.inward_name, text) {
                found.push((link_type.clone(), LinkDirection::Inward));
            }
        }
        match found.len() {
            1 => Ok(found.remove(0)),
            0 => {
                let mut available: Vec<&str> = Vec::new();
                for name in all.iter().flat_map(|x| [x.outward_name.as_str(), x.inward_name.as_str()]) {
                    if !available.iter().any(|a| eq_ignore_case(a, name)) {
                        available.push(name);
                    }
                }
                Err(Error::validation(format!(
                    "No link type or link name '{text}'; available: {}",
                    available.join(", ")
                )))
            }
            _ => Err(Error::validation(format!(
                "'{text}' fits several link types ({}); use the type id",
                found
                    .iter()
                    .map(|(t, _)| format!("{} [{}]", t.name, guid_d(&t.id)))
                    .collect::<Vec<_>>()
                    .join(", ")
            ))),
        }
    }

    pub fn create(&self, project_id: &Uuid, command: &CreateLinkType) -> Result<LinkType> {
        self.ensure_defaults(project_id)?;
        let name = validate::entity_name(Some(&command.name), "Link type name")?;
        let outward = validate::entity_name(Some(&command.outward_name), "Outward name")?;
        let inward = match &command.inward_name {
            None => outward.clone(),
            Some(i) => validate::entity_name(Some(i), "Inward name")?,
        };
        self.ensure_name_free(project_id, &name, None)?;
        let hierarchical = command.hierarchical.unwrap_or(false);
        ensure_hierarchy_valid(hierarchical, command.allow_cycles, &outward, &inward)?;

        let mut link_type = LinkType {
            id: Uuid::new_v4(),
            project_id: *project_id,
            name,
            outward_name: outward,
            inward_name: inward,
            allow_cycles: !hierarchical && command.allow_cycles.unwrap_or(true),
            hierarchical,
            version: versioning::NEW.to_string(),
        };
        link_type.version = self.ws.add(&link_type)?;
        Ok(link_type)
    }

    /// None — типа нет.
    pub fn update(&self, project_id: &Uuid, id: &Uuid, command: &UpdateLinkType) -> Result<Option<LinkType>> {
        self.ensure_defaults(project_id)?;
        let Some(link_type) = self.get_by_id(project_id, id)? else {
            return Ok(None);
        };
        let subject = subject(&link_type);
        self.ws.locks().ensure_writable(LockedEntity::LinkType, &link_type.id, &subject)?;
        let expected = versioning::check(&link_type.version, command.version.as_deref(), &subject)?;

        let mut updated = LinkType {
            name: match &command.name {
                None => link_type.name.clone(),
                Some(n) => validate::entity_name(Some(n), "Link type name")?,
            },
            outward_name: match &command.outward_name {
                None => link_type.outward_name.clone(),
                Some(n) => validate::entity_name(Some(n), "Outward name")?,
            },
            inward_name: match &command.inward_name {
                None => link_type.inward_name.clone(),
                Some(n) => validate::entity_name(Some(n), "Inward name")?,
            },
            allow_cycles: command.allow_cycles.unwrap_or(link_type.allow_cycles),
            hierarchical: command.hierarchical.unwrap_or(link_type.hierarchical),
            ..link_type.clone()
        };
        ensure_hierarchy_valid(
            updated.hierarchical,
            command.allow_cycles,
            &updated.outward_name,
            &updated.inward_name,
        )?;
        if updated.hierarchical {
            updated.allow_cycles = false;
        }
        if updated.name != link_type.name {
            self.ensure_name_free(project_id, &updated.name, Some(id))?;
        }
        let version = self.ws.update(&updated, &expected)?.ok_or_else(|| versioning::modified(&subject))?;
        updated.version = version;
        Ok(Some(updated))
    }

    /// Нельзя удалить тип, по которому есть связи. false — типа нет.
    pub fn delete(&self, project_id: &Uuid, id: &Uuid, version: Option<&str>) -> Result<bool> {
        self.ensure_defaults(project_id)?;
        let Some(link_type) = self.get_by_id(project_id, id)? else {
            return Ok(false);
        };
        let subject = subject(&link_type);
        self.ws.locks().ensure_writable(LockedEntity::LinkType, &link_type.id, &subject)?;
        let expected = versioning::check(&link_type.version, version, &subject)?;

        let mut usages = Usages::new(&subject);
        let filter = TaskFilter {
            link_type_ids: Some(vec![*id]),
            ..TaskFilter::default()
        };
        let linked = self
            .ws
            .index()
            .count(EntityKind::Task, &IndexQuery::tasks(project_id, Some(&filter)))?;
        if linked > 0 {
            usages.add(format!("links of {linked} task(s)"));
        }
        usages.throw_if_any("deleted")?;

        if !self.ws.delete::<LinkType>(project_id, id, &expected)? {
            return Err(versioning::modified(&subject).into());
        }
        self.ws.locks().forget(LockedEntity::LinkType, &link_type.id)?;
        Ok(true)
    }

    fn ensure_name_free(&self, project_id: &Uuid, name: &str, except_id: Option<&Uuid>) -> Result<()> {
        if self
            .ws
            .get_all::<LinkType>(project_id)?
            .iter()
            .any(|x| Some(&x.id) != except_id && eq_ignore_case(&x.name, name))
        {
            return Err(Error::in_use(format!("Link type '{name}' already exists in the project")));
        }
        Ok(())
    }
}

/// Иерархический тип: циклы запрещены, стороны названы по-разному.
fn ensure_hierarchy_valid(hierarchical: bool, allow_cycles: Option<bool>, outward: &str, inward: &str) -> Result<()> {
    if !hierarchical {
        return Ok(());
    }
    if allow_cycles == Some(true) {
        return Err(Error::validation(
            "A hierarchical link type does not allow cycles: AllowCycles cannot be true",
        ));
    }
    if eq_ignore_case(outward, inward) {
        return Err(Error::validation(
            "A hierarchical link type needs two different side names (e.g. 'includes' / 'is part of'): the parent and the child must be told apart",
        ));
    }
    Ok(())
}

/// Страница из списка в памяти (`Page.Apply`).
pub fn apply<T: Clone>(page: Page, all: Vec<T>) -> ListPage<T> {
    ListPage {
        total_count: all.len(),
        offset: page.offset,
        limit: page.limit,
        data: all.into_iter().skip(page.offset).take(page.limit).collect(),
    }
}
