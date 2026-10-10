//! `tasker manual` (`howto`): микро-справочник «как сделать то-то» (`ManualCommands`, `ManualCatalog` в .NET). Страницы
//! встроены в бинарник (`include_str!` из `src/Tasker.Cli/Manual/<язык>/`), рабочая область не нужна. Язык — `--lang`,
//! переменная `TASKER_LANG`, иначе язык по умолчанию; нет страницы — откат на язык по умолчанию с пометкой.
use crate::context::Context;
use crate::errors::{CliError, Result};
use crate::json::object;
use crate::kit;
use clap::ArgMatches;
use serde_json::Value;
use tasker_core::validate::{to_lower_invariant, utf16_len};

pub const LANGUAGE_VARIABLE: &str = "TASKER_LANG";

/// Язык, на который откатываются недостающие страницы и неизвестный язык.
pub const DEFAULT_LANGUAGE: &str = "ru";

/// Встроенные страницы: язык, имя файла, текст.
const PAGES: &[(&str, &str, &str)] = &[
    (
        "ru",
        "01-first-project.md",
        include_str!("../../../../../src/Tasker.Cli/Manual/ru/01-first-project.md"),
    ),
    (
        "ru",
        "02-tasks.md",
        include_str!("../../../../../src/Tasker.Cli/Manual/ru/02-tasks.md"),
    ),
    (
        "ru",
        "03-series.md",
        include_str!("../../../../../src/Tasker.Cli/Manual/ru/03-series.md"),
    ),
    (
        "ru",
        "04-links.md",
        include_str!("../../../../../src/Tasker.Cli/Manual/ru/04-links.md"),
    ),
    (
        "ru",
        "05-boards.md",
        include_str!("../../../../../src/Tasker.Cli/Manual/ru/05-boards.md"),
    ),
    (
        "ru",
        "06-fields.md",
        include_str!("../../../../../src/Tasker.Cli/Manual/ru/06-fields.md"),
    ),
    (
        "ru",
        "07-locks.md",
        include_str!("../../../../../src/Tasker.Cli/Manual/ru/07-locks.md"),
    ),
    (
        "ru",
        "08-agent.md",
        include_str!("../../../../../src/Tasker.Cli/Manual/ru/08-agent.md"),
    ),
    (
        "ru",
        "09-migrate.md",
        include_str!("../../../../../src/Tasker.Cli/Manual/ru/09-migrate.md"),
    ),
    (
        "ru",
        "10-sync-cleanup.md",
        include_str!("../../../../../src/Tasker.Cli/Manual/ru/10-sync-cleanup.md"),
    ),
    (
        "ru",
        "11-install-daemon.md",
        include_str!("../../../../../src/Tasker.Cli/Manual/ru/11-install-daemon.md"),
    ),
    (
        "ru",
        "12-statuses-types.md",
        include_str!("../../../../../src/Tasker.Cli/Manual/ru/12-statuses-types.md"),
    ),
    (
        "ru",
        "13-windows.md",
        include_str!("../../../../../src/Tasker.Cli/Manual/ru/13-windows.md"),
    ),
];

/// Страница справочника: тема на одном языке.
#[derive(Debug, Clone)]
pub struct ManualPage {
    pub language: String,
    /// Имя темы: имя файла без номера порядка и расширения (`01-first-project.md` → `first-project`).
    pub id: String,
    pub title: String,
    pub description: String,
    pub markdown: &'static str,
}

impl ManualPage {
    /// Страница в виде для терминала: без разметки, с подчёркнутыми заголовками и блоками кода с отступом.
    pub fn render_text(&self) -> String {
        let mut text = String::new();
        let mut in_code = false;
        for line in self.markdown.replace("\r\n", "\n").split('\n') {
            if line.starts_with("```") {
                in_code = !in_code;
                continue;
            }
            if in_code {
                if !line.is_empty() {
                    text.push_str("    ");
                    text.push_str(line);
                }
                text.push('\n');
            } else if let Some(heading) = line.strip_prefix("## ") {
                if !(text.is_empty() || text.ends_with("\n\n")) {
                    text.push('\n');
                }
                text.push_str(heading);
                text.push('\n');
                text.push_str(&"-".repeat(utf16_len(line) - 3));
                text.push('\n');
            } else if let Some(heading) = line.strip_prefix("# ") {
                text.push_str(heading);
                text.push('\n');
                text.push_str(&"=".repeat(utf16_len(line) - 2));
                text.push('\n');
            } else {
                text.push_str(line);
                text.push('\n');
            }
        }
        text.trim_end_matches('\n').to_string()
    }
}

pub struct ManualLanguage {
    pub language: String,
    /// Есть ли страницы на запрошенном языке (нет — это откат на язык по умолчанию).
    pub found: bool,
}

pub struct ManualCatalog {
    /// Язык → страницы в порядке оглавления (порядок имён файлов).
    pages: Vec<(String, Vec<ManualPage>)>,
}

impl ManualCatalog {
    pub fn embedded() -> ManualCatalog {
        Self::from_pages(PAGES)
    }

    /// Каталог из страниц `(язык, имя файла, markdown)`; тесты подставляют свой.
    pub fn from_pages(pages: &[(&'static str, &'static str, &'static str)]) -> ManualCatalog {
        let mut files: Vec<&(&str, &str, &str)> = pages.iter().collect();
        files.sort_by(|a, b| a.0.cmp(b.0).then_with(|| a.1.cmp(b.1)));
        let mut pages: Vec<(String, Vec<ManualPage>)> = Vec::new();
        for (language, file_name, markdown) in files {
            let page = parse(&language.to_lowercase(), file_name, markdown);
            match pages.iter_mut().find(|(l, _)| *l == page.language) {
                Some((_, list)) => list.push(page),
                None => pages.push((page.language.clone(), vec![page])),
            }
        }
        ManualCatalog { pages }
    }

    /// Языки, на которых есть хотя бы одна страница.
    pub fn languages(&self) -> Vec<String> {
        let mut languages: Vec<String> = self.pages.iter().map(|(l, _)| l.clone()).collect();
        languages.sort();
        languages
    }

    fn of(&self, language: &str) -> Option<&Vec<ManualPage>> {
        self.pages.iter().find(|(l, _)| l == language).map(|(_, p)| p)
    }

    /// Язык, на котором показывать: запрошенный, если на нём есть страницы (`en-US` → `en`), иначе язык по умолчанию.
    pub fn resolve(&self, requested: Option<&str>) -> ManualLanguage {
        let language = requested.map(|r| r.trim().to_lowercase()).unwrap_or_default();
        if language.is_empty() {
            return ManualLanguage {
                language: DEFAULT_LANGUAGE.into(),
                found: true,
            };
        }
        if self.of(&language).is_some() {
            return ManualLanguage { language, found: true };
        }
        if let Some(dash) = language.find('-')
            && dash > 0
            && self.of(&language[..dash]).is_some()
        {
            return ManualLanguage {
                language: language[..dash].to_string(),
                found: true,
            };
        }
        ManualLanguage {
            language: DEFAULT_LANGUAGE.into(),
            found: false,
        }
    }

    /// Оглавление: все темы (набор и порядок задаёт язык по умолчанию, темы только на других языках идут следом), у каждой —
    /// страница на выбранном языке или, если её нет, на языке по умолчанию.
    pub fn topics(&self, requested: Option<&str>) -> Vec<ManualPage> {
        let language = self.resolve(requested).language;
        let all: Vec<&ManualPage> = self.pages.iter().flat_map(|(_, p)| p.iter()).collect();
        let mut ids: Vec<&str> = Vec::new();
        for id in self
            .of(DEFAULT_LANGUAGE)
            .map(|p| p.iter().map(|x| x.id.as_str()).collect::<Vec<_>>())
            .unwrap_or_default()
            .into_iter()
            .chain(all.iter().map(|x| x.id.as_str()))
        {
            if !ids.contains(&id) {
                ids.push(id);
            }
        }
        ids.iter()
            .map(|id| {
                self.page(&language, id)
                    .or_else(|| self.page(DEFAULT_LANGUAGE, id))
                    .or_else(|| all.iter().find(|x| x.id == *id).copied())
                    .cloned()
                    .expect("a page of the id")
            })
            .collect()
    }

    fn page(&self, language: &str, id: &str) -> Option<&ManualPage> {
        self.of(language)?.iter().find(|x| x.id == id)
    }

    /// Темы по запросу: точное имя или заголовок — одна тема; иначе темы, в имени, заголовке или описании которых есть все
    /// слова запроса, а если таких нет — темы, где слова встречаются в тексте. Пусто — ничего не найдено.
    pub fn find(&self, requested: Option<&str>, query: &str) -> Vec<ManualPage> {
        let topics = self.topics(requested);
        let text = query.trim();
        let exact: Vec<ManualPage> = topics
            .iter()
            .filter(|x| tasker_core::validate::eq_ignore_case(&x.id, text) || tasker_core::validate::eq_ignore_case(&x.title, text))
            .cloned()
            .collect();
        if !exact.is_empty() {
            return exact;
        }
        let words: Vec<String> = text.split_whitespace().map(to_lower_invariant).collect();
        if words.is_empty() {
            return Vec::new();
        }
        let all = |haystack: &str| {
            let lower = to_lower_invariant(haystack);
            words.iter().all(|w| lower.contains(w.as_str()))
        };
        let by_heading: Vec<ManualPage> = topics
            .iter()
            .filter(|x| all(&format!("{} {} {}", x.id, x.title, x.description)))
            .cloned()
            .collect();
        if !by_heading.is_empty() {
            return by_heading;
        }
        topics.into_iter().filter(|x| all(x.markdown)).collect()
    }
}

fn parse(language: &str, file_name: &str, markdown: &'static str) -> ManualPage {
    let stem = file_name.strip_suffix(".md").unwrap_or(file_name);
    let id = match stem.find('-') {
        Some(dash) if stem[..dash].bytes().all(|b| b.is_ascii_digit()) && dash > 0 => stem[dash + 1..].to_string(),
        _ => stem.to_string(),
    };
    let normalized = markdown.replace("\r\n", "\n");
    let lines: Vec<&str> = normalized.split('\n').collect();
    let first = lines.iter().position(|x| !x.trim().is_empty()).expect("a title line");
    let title = lines[first].strip_prefix("# ").expect("a '# ' title").trim().to_string();
    let description: Vec<String> = lines[first + 1..]
        .iter()
        .skip_while(|x| x.trim().is_empty())
        .take_while(|x| !x.trim().is_empty())
        .map(|x| x.trim().to_string())
        .collect();
    ManualPage {
        language: language.to_string(),
        id,
        title,
        description: description.join(" "),
        markdown,
    }
}

pub fn run(ctx: &mut Context<'_>, leaf: &ArgMatches) -> Result<()> {
    let catalog = ManualCatalog::embedded();
    let requested = kit::text(leaf, "lang").or_else(|| std::env::var(LANGUAGE_VARIABLE).ok());
    let words: Vec<String> = leaf.get_many::<String>("topic").map(|v| v.cloned().collect()).unwrap_or_default();
    let language = catalog.resolve(requested.as_deref());
    if !language.found {
        let _ = writeln!(
            ctx.err(),
            "Note: no pages in language '{}' (available: {}); showing '{}'",
            requested.as_deref().unwrap_or_default().trim(),
            catalog.languages().join(", "),
            language.language
        );
    }
    if words.is_empty() {
        let topics = catalog.topics(requested.as_deref());
        show(ctx, &catalog, requested.as_deref(), None, &topics);
        return Ok(());
    }
    let query = words.join(" ");
    let found = catalog.find(requested.as_deref(), &query);
    if found.is_empty() {
        return Err(CliError::new(format!(
            "No topic matches '{query}': 'tasker manual' lists the topics"
        )));
    }
    show(ctx, &catalog, requested.as_deref(), Some(&query), &found);
    Ok(())
}

fn show(ctx: &mut Context<'_>, catalog: &ManualCatalog, requested: Option<&str>, query: Option<&str>, pages: &[ManualPage]) {
    let language = catalog.resolve(requested).language;
    let fallback: Vec<&str> = pages.iter().filter(|x| x.language != language).map(|x| x.id.as_str()).collect();
    if !fallback.is_empty() {
        let _ = writeln!(
            ctx.err(),
            "Note: no '{language}' page for {}; shown in {DEFAULT_LANGUAGE}",
            fallback.join(", ")
        );
    }
    let info = |x: &ManualPage| {
        object(vec![
            ("id", Value::String(x.id.clone())),
            ("title", Value::String(x.title.clone())),
            ("description", Value::String(x.description.clone())),
            ("language", Value::String(x.language.clone())),
            ("fallback", Value::Bool(x.language != language)),
        ])
    };

    // Одна тема из поиска (или точное имя) — сама страница; иначе оглавление или список найденного.
    if let Some(query) = query
        && pages.len() == 1
    {
        let _ = query;
        let page = &pages[0];
        ctx.print(
            &object(vec![
                ("id", Value::String(page.id.clone())),
                ("title", Value::String(page.title.clone())),
                ("description", Value::String(page.description.clone())),
                ("language", Value::String(page.language.clone())),
                ("requestedLanguage", Value::String(language.clone())),
                ("fallback", Value::Bool(page.language != language)),
                ("content", Value::String(page.markdown.to_string())),
            ]),
            &page.render_text(),
        );
        return;
    }

    let mut text = match query {
        None => format!("Tasker manual ({language}): 'tasker manual <topic>' shows a recipe, 'tasker manual <word>' searches\n\n"),
        Some(query) => format!("Several topics match '{query}':\n\n"),
    };
    let width = pages.iter().map(|x| utf16_len(&x.id)).max().unwrap_or(0);
    for page in pages {
        text.push_str(&format!(
            "  {:<width$}  {}{}\n",
            page.id,
            page.title,
            if page.language == language {
                String::new()
            } else {
                format!(" [{}]", page.language)
            }
        ));
    }
    ctx.print(
        &object(vec![
            ("query", query.map(|q| Value::String(q.into())).unwrap_or(Value::Null)),
            ("language", Value::String(language.clone())),
            (
                "languages",
                Value::Array(catalog.languages().into_iter().map(Value::String).collect()),
            ),
            ("topics", Value::Array(pages.iter().map(info).collect())),
        ]),
        text.trim_end(),
    );
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn the_catalog_has_the_russian_pages_in_file_order() {
        let catalog = ManualCatalog::embedded();
        let ids: Vec<String> = catalog.topics(None).into_iter().map(|x| x.id).collect();
        assert_eq!(ids[0], "first-project");
        assert_eq!(ids.len(), 13);
        assert_eq!(catalog.find(None, "tasks").len(), 1);
        assert!(!catalog.resolve(Some("en")).found);
        assert_eq!(catalog.resolve(Some("RU-ru")).language, "ru");
    }

    /// `ManualCompletionTests`: ru и en переведены частично, de — языка нет в сборке; тема only-en есть только на en.
    #[test]
    fn languages_and_topics_follow_the_catalog_and_the_typed_language() {
        let catalog = ManualCatalog::from_pages(&[
            ("ru", "01-first-project.md", "# Первый проект\n\nО.\n"),
            ("ru", "02-tasks.md", "# Задачи\n\nО.\n"),
            ("en", "01-first-project.md", "# First project\n\nAbout.\n"),
            ("en", "03-only-en.md", "# Only English\n\nAbout.\n"),
            ("de", "02-tasks.md", "# Aufgaben\n\nUeber.\n"),
        ]);
        assert_eq!(catalog.languages(), ["de", "en", "ru"]);
        let ids = |lang: Option<&str>| catalog.topics(lang).into_iter().map(|p| p.id).collect::<Vec<_>>();
        // Набор тем задаёт язык по умолчанию (ru), темы только на других языках идут следом — на любом выбранном языке.
        assert_eq!(ids(None), ["first-project", "tasks", "only-en"]);
        assert_eq!(ids(Some("de")), ["first-project", "tasks", "only-en"]);
        assert_eq!(ids(Some("en")), ["first-project", "tasks", "only-en"]);
    }
}
