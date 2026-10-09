//! `tasker migrate` (`FileMigration` в .NET): приводит файлы сущностей `.tasker` к текущему формату на диске — добавляет или
//! обновляет `formatVersion`, остальной текст не трогает — и называет файлы сущностей проекта по их названию
//! (`<slug>-<id8>.yaml`). Файлы более нового формата и файлы с конфликтом слияния git не меняются, а попадают в отчёт. Каждый файл
//! переписывается под блокировкой записи, индекс обновляется после ([`IndexRefresh`]).
use crate::format;
use crate::layout::{EntityFolder, EntityHead, LayoutEntry, TaskerDirectory, classify, folder_of};
use crate::names;
use crate::write::{self, IndexRefresh};
use std::io;
use std::path::{Path, PathBuf};
use tasker_core::io::read_all_text;

/// `dry_run` — ничего не записывать, только рассказать, что было бы изменено.
#[derive(Debug, Clone, Copy, Default, PartialEq, Eq)]
pub struct MigrationOptions {
    pub dry_run: bool,
}

/// Файл старого или более нового формата: путь относительно `.tasker` через `/` и версия формата (0 — версии в файле нет).
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct MigrationFile {
    pub path: String,
    pub version: u32,
}

/// Переименование: прежний и новый путь относительно `.tasker`, через `/`.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct MigrationRename {
    pub from: String,
    pub to: String,
}

/// Файл пропущен: конфликт слияния, ошибка чтения, id внутри не согласуется с именем.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct MigrationProblem {
    pub path: String,
    pub reason: String,
}

/// Что нашла и что сделала миграция файлов (`MigrationReport`).
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct MigrationReport {
    /// Версия формата, которую пишет этот Tasker.
    pub current_format: u32,
    /// Сколько файлов сущностей проверено.
    pub scanned: usize,
    /// Сколько уже в текущем формате.
    pub up_to_date: usize,
    /// Файлы старого формата: переписаны в текущий (при `dry_run` — были бы переписаны).
    pub migrated: Vec<MigrationFile>,
    /// Файлы сущностей проекта, имя которых не по названию: переименованы (при `dry_run` — были бы).
    pub renamed: Vec<MigrationRename>,
    /// Файлы более нового формата: не тронуты, нужен более новый Tasker.
    pub newer: Vec<MigrationFile>,
    /// Файлы, которые не удалось проверить: не тронуты.
    pub unreadable: Vec<MigrationProblem>,
}

impl MigrationReport {
    /// Есть что мигрировать или о чём нужно знать: старые файлы, файлы нового формата, нечитаемые.
    pub fn needs_attention(&self) -> bool {
        !self.migrated.is_empty() || !self.renamed.is_empty() || !self.newer.is_empty() || !self.unreadable.is_empty()
    }
}

pub const CONFLICT_REASON: &str = "Unresolved git merge conflict: fix the file first";
pub const MISMATCH_REASON: &str = "The id inside the file does not match the file name: not touched";

struct Migration<'a> {
    directory: &'a TaskerDirectory,
    options: MigrationOptions,
    renamed: Vec<MigrationRename>,
    unreadable: Vec<MigrationProblem>,
    changed: Vec<PathBuf>,
}

/// Запускает миграцию области `directory`. Ошибка — только когда сорвалась сама запись (блокировка занята, диск); проблемы
/// отдельных файлов попадают в отчёт.
pub fn run(directory: &TaskerDirectory, index: &dyn IndexRefresh, options: MigrationOptions) -> io::Result<MigrationReport> {
    let mut scanned = 0;
    let mut up_to_date = 0;
    let mut migrated = Vec::new();
    let mut newer = Vec::new();
    let mut state = Migration {
        directory,
        options,
        renamed: Vec::new(),
        unreadable: Vec::new(),
        changed: Vec::new(),
    };

    if directory.root().is_dir() {
        for (full, relative) in layout_files(directory) {
            scanned += 1;

            let text = match read_all_text(&full) {
                Ok(text) => text,
                Err(e) => {
                    state.problem(&relative, e.to_string());
                    continue;
                }
            };

            if format::merge_conflict_line(&text).is_some() {
                state.problem(&relative, CONFLICT_REASON);
                continue;
            }

            let version = match format::version_of(&text, &full) {
                Ok(version) => version,
                Err(e) => {
                    state.problem(&relative, e.message());
                    continue;
                }
            };

            if version > format::CURRENT {
                newer.push(MigrationFile { path: relative, version });
                continue;
            }

            // Файл в папке сущностей проекта должен быть настоящей сущностью: проверяем до любых изменений, чтобы чужой или битый
            // файл не тронуть вовсе.
            let layout = classify(&relative).expect("layout files only");
            let folder = folder_of(layout.kind);
            let mut head = None;
            if let Some(folder) = folder {
                match inspect(folder, &layout, &full) {
                    Ok(inspected) => head = Some(inspected),
                    Err(problem) => {
                        state.problem(&relative, problem);
                        continue;
                    }
                }
            }

            if version == format::CURRENT {
                up_to_date += 1;
            } else {
                migrated.push(MigrationFile {
                    path: relative.clone(),
                    version,
                });
                if !options.dry_run {
                    write::upgrade_on_disk(&full).map_err(|e| match e {
                        write::UpgradeError::Io(e) => e,
                        write::UpgradeError::Format(e) => io::Error::other(e.message()),
                    })?;
                    state.changed.push(full.clone());
                }
            }

            if let (Some(folder), Some(head)) = (folder, head) {
                state.rename(folder, &head, &full, &relative)?;
            }
        }
    }

    if !state.changed.is_empty() {
        index.refresh(&state.changed)?;
    }

    Ok(MigrationReport {
        current_format: format::CURRENT,
        scanned,
        up_to_date,
        migrated,
        renamed: state.renamed,
        newer,
        unreadable: state.unreadable,
    })
}

impl Migration<'_> {
    fn problem(&mut self, relative: &str, reason: impl Into<String>) {
        self.unreadable.push(MigrationProblem {
            path: relative.to_string(),
            reason: reason.into(),
        });
    }

    /// Файл сущности проекта должен называться по её названию: старые имена (Guid) и имена, от которых название «ушло» (правка
    /// мимо Tasker, слияние), приводятся к нужному. Чужой файл под новым именем не затираем.
    fn rename(&mut self, folder: &EntityFolder, head: &EntityHead, full: &Path, relative: &str) -> io::Result<()> {
        let current_name = format::file_name(full);
        if folder.is_current(&current_name, head.name.as_deref(), &head.id) {
            return Ok(());
        }

        let target_name = folder.file_name(head.name.as_deref(), &head.id);
        let target = full
            .parent()
            .map(|dir| dir.join(&target_name))
            .unwrap_or_else(|| PathBuf::from(&target_name));
        let target_relative = relative_path(self.directory.root(), &target);
        if target.exists() {
            self.problem(relative, format!("Cannot rename to '{target_name}': a file with that name exists"));
            return Ok(());
        }

        self.renamed.push(MigrationRename {
            from: relative.to_string(),
            to: target_relative,
        });
        if self.options.dry_run {
            return Ok(());
        }

        if write::rename(full, &target)? {
            self.changed.push(full.to_path_buf());
            self.changed.push(target);
        }
        Ok(())
    }
}

/// Читает файл сущности (старый формат — в памяти) и проверяет, что это настоящая сущность: id внутри согласуется с именем, как в
/// индексе. Иначе (пустой или чужой файл, неудачное слияние) файл не трогаем совсем: назвать его по названию значило бы его потерять.
fn inspect(folder: &EntityFolder, layout: &LayoutEntry, full: &Path) -> Result<EntityHead, String> {
    let project_id = layout.project_id.expect("project entity");
    let bytes = std::fs::read(full).map_err(|e| e.to_string())?;
    let head = folder.peek(project_id, &bytes, full).map_err(|e| e.message())?;
    let consistent = head.as_ref().is_some_and(|head| {
        !head.id.is_nil()
            && match (&layout.id, &layout.id_prefix) {
                (Some(named), _) => head.id == *named,
                (None, Some(prefix)) => names::id_prefix(&head.id) == *prefix,
                (None, None) => true,
            }
    });
    match (consistent, head) {
        (true, Some(head)) => Ok(head),
        _ => Err(MISMATCH_REASON.to_string()),
    }
}

/// `Path.GetRelativePath(root, path)` через `/`.
fn relative_path(root: &Path, path: &Path) -> String {
    path.strip_prefix(root)
        .map(|p| p.to_string_lossy().replace('\\', "/"))
        .unwrap_or_else(|_| path.to_string_lossy().replace('\\', "/"))
}

/// Все `*.yaml` под `.tasker`, которые относятся к раскладке (`.cache`, временные и чужие файлы — не сущности), по относительному
/// пути в порядке Ordinal (по единицам UTF-16, как `StringComparer.Ordinal`). Недоступные папки пропускаются
/// (`IgnoreInaccessible`).
fn layout_files(directory: &TaskerDirectory) -> Vec<(PathBuf, String)> {
    let mut files = Vec::new();
    collect_yaml(directory.root(), &mut files);
    let mut entries: Vec<(PathBuf, String)> = files
        .into_iter()
        .map(|full| {
            let relative = relative_path(directory.root(), &full);
            (full, relative)
        })
        .filter(|(_, relative)| classify(relative).is_some())
        .collect();
    entries.sort_by(|a, b| {
        let x: Vec<u16> = a.1.encode_utf16().collect();
        let y: Vec<u16> = b.1.encode_utf16().collect();
        x.cmp(&y)
    });
    entries
}

fn collect_yaml(dir: &Path, out: &mut Vec<PathBuf>) {
    let Ok(entries) = std::fs::read_dir(dir) else {
        return;
    };
    for entry in entries.flatten() {
        let path = entry.path();
        let Ok(file_type) = std::fs::metadata(&path).map(|m| m.file_type()) else {
            continue;
        };
        if file_type.is_dir() {
            collect_yaml(&path, out);
        } else if path.extension().is_some_and(|e| e == "yaml") {
            out.push(path);
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::layout::TASKS;
    use crate::test_support::temp_dir;
    use crate::write::NoIndex;
    use uuid::Uuid;

    const TASK: &str = "id: 0000007d-0000-4000-8000-00000000007d\ntitle: Legacy one\ntypeId: 0000007b-0000-4000-8000-00000000007b\nstatusId: 00000073-0000-4000-8000-000000000073\ncreatedAt: 2026-01-01T00:00:00.0000000+00:00\nupdatedAt: 2026-01-01T00:00:00.0000000+00:00\n";

    #[test]
    fn an_empty_workspace_or_a_missing_folder_scans_nothing() {
        let ws = temp_dir();
        let dir = TaskerDirectory::new(&ws);
        let report = run(&dir, &NoIndex, MigrationOptions::default()).unwrap();
        assert_eq!(report.scanned, 0);
        assert!(!report.needs_attention());
        assert_eq!(report.current_format, format::CURRENT);
        std::fs::remove_dir_all(&ws).unwrap();
    }

    #[test]
    fn mismatched_ids_conflicts_and_occupied_names_are_reported_and_not_touched() {
        let ws = temp_dir();
        let dir = TaskerDirectory::new(&ws);
        let project = Uuid::parse_str("22222222-2222-4222-8222-222222222222").unwrap();
        let tasks = dir.project(&project).tasks();
        std::fs::create_dir_all(&tasks).unwrap();
        let id = Uuid::parse_str("0000007d-0000-4000-8000-00000000007d").unwrap();
        // Имя по чужому id: не трогаем.
        std::fs::write(tasks.join("legacy-one-ffffffff.yaml"), TASK).unwrap();
        // Старое имя по полному Guid, но внутри другой id.
        std::fs::write(tasks.join("0000007e-0000-4000-8000-00000000007e.yaml"), TASK).unwrap();
        // Пустой файл: YamlDotNet отдаёт null → тот же отказ.
        std::fs::write(tasks.join("empty-0000007d.yaml"), b"").unwrap();
        // Конфликт слияния.
        std::fs::write(
            tasks.join("conflicted-0000007d.yaml"),
            format!("<<<<<<< ours\n{TASK}=======\n>>>>>>> theirs\n"),
        )
        .unwrap();
        // Из будущего.
        std::fs::write(tasks.join("future-0000007d.yaml"), format!("formatVersion: 99\n{TASK}")).unwrap();
        // Версия не число.
        std::fs::write(tasks.join("bad-0000007d.yaml"), format!("formatVersion: x\n{TASK}")).unwrap();
        // Старое имя и занятое новое: переименовать нельзя.
        std::fs::write(dir.project(&project).legacy_file(&TASKS, &id), TASK).unwrap();
        std::fs::write(tasks.join("legacy-one-0000007d.yaml"), format!("formatVersion: 9\n{TASK}")).unwrap();
        // Не сущность: не считается.
        std::fs::write(tasks.join("notes.yaml"), b"x").unwrap();
        std::fs::write(tasks.join("legacy-one-0000007d.yaml.tmp"), b"x").unwrap();

        let report = run(&dir, &NoIndex, MigrationOptions::default()).unwrap();
        let p = "projects/22222222-2222-4222-8222-222222222222/tasks";
        assert_eq!(report.scanned, 8);
        assert_eq!(report.up_to_date, 1);
        assert_eq!(
            report.migrated,
            vec![MigrationFile {
                path: format!("{p}/0000007d-0000-4000-8000-00000000007d.yaml"),
                version: 0
            }]
        );
        assert!(report.renamed.is_empty());
        assert_eq!(
            report.newer,
            vec![MigrationFile {
                path: format!("{p}/future-0000007d.yaml"),
                version: 99
            }]
        );
        let problems: Vec<(String, String)> = report.unreadable.iter().map(|x| (x.path.clone(), x.reason.clone())).collect();
        assert_eq!(
            problems,
            vec![
                (
                    format!("{p}/0000007d-0000-4000-8000-00000000007d.yaml"),
                    "Cannot rename to 'legacy-one-0000007d.yaml': a file with that name exists".to_string()
                ),
                (
                    format!("{p}/0000007e-0000-4000-8000-00000000007e.yaml"),
                    MISMATCH_REASON.to_string()
                ),
                (
                    format!("{p}/bad-0000007d.yaml"),
                    "bad-0000007d.yaml: formatVersion 'x' is not a number".to_string()
                ),
                (format!("{p}/conflicted-0000007d.yaml"), CONFLICT_REASON.to_string()),
                (format!("{p}/empty-0000007d.yaml"), MISMATCH_REASON.to_string()),
                (format!("{p}/legacy-one-ffffffff.yaml"), MISMATCH_REASON.to_string()),
            ]
        );
        // Мигрирован только файл со старым именем; остальные байты не тронуты.
        assert_eq!(
            std::fs::read_to_string(dir.project(&project).legacy_file(&TASKS, &id)).unwrap(),
            format!("formatVersion: 9\n{TASK}")
        );
        assert_eq!(std::fs::read_to_string(tasks.join("legacy-one-ffffffff.yaml")).unwrap(), TASK);
        assert_eq!(
            std::fs::read_to_string(tasks.join("future-0000007d.yaml")).unwrap(),
            format!("formatVersion: 99\n{TASK}")
        );
        std::fs::remove_dir_all(&ws).unwrap();
    }

    #[test]
    fn dry_run_writes_nothing_and_a_rename_follows_the_title() {
        let ws = temp_dir();
        let dir = TaskerDirectory::new(&ws);
        let project = Uuid::parse_str("22222222-2222-4222-8222-222222222222").unwrap();
        let tasks = dir.project(&project).tasks();
        std::fs::create_dir_all(&tasks).unwrap();
        let old = tasks.join("renamed-by-hand-0000007d.yaml");
        std::fs::write(&old, format!("formatVersion: 2\n{TASK}")).unwrap();

        let dry = run(&dir, &NoIndex, MigrationOptions { dry_run: true }).unwrap();
        assert_eq!(dry.migrated.len(), 1);
        assert_eq!(
            dry.renamed,
            vec![MigrationRename {
                from: "projects/22222222-2222-4222-8222-222222222222/tasks/renamed-by-hand-0000007d.yaml".into(),
                to: "projects/22222222-2222-4222-8222-222222222222/tasks/legacy-one-0000007d.yaml".into(),
            }]
        );
        assert!(old.exists());
        assert_eq!(std::fs::read_to_string(&old).unwrap(), format!("formatVersion: 2\n{TASK}"));

        struct Recorder(std::sync::Mutex<Vec<PathBuf>>);
        impl IndexRefresh for Recorder {
            fn refresh(&self, paths: &[PathBuf]) -> io::Result<()> {
                self.0.lock().unwrap().extend_from_slice(paths);
                Ok(())
            }
        }
        let recorder = Recorder(std::sync::Mutex::new(Vec::new()));
        let real = run(&dir, &recorder, MigrationOptions::default()).unwrap();
        assert_eq!(real.renamed, dry.renamed);
        assert!(!old.exists());
        let new = tasks.join("legacy-one-0000007d.yaml");
        assert_eq!(std::fs::read_to_string(&new).unwrap(), format!("formatVersion: 9\n{TASK}"));
        assert_eq!(*recorder.0.lock().unwrap(), vec![old.clone(), old.clone(), new.clone()]);

        let again = run(&dir, &NoIndex, MigrationOptions::default()).unwrap();
        assert!(!again.needs_attention());
        assert_eq!((again.scanned, again.up_to_date), (1, 1));
        std::fs::remove_dir_all(&ws).unwrap();
    }
}
