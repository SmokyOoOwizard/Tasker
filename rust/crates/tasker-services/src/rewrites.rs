//! Массовая правка задач (`TaskRewrites` в .NET): перечитать, поправить, записать с версией, при конфликте повторить. Единое правило
//! блокировок каскадов: вызывающий ждёт чужие блокировки до секции записи ([`wait_for_locks`]), внутри секции они проверяются
//! без ожидания до первой записи и перед каждой задачей ([`modify_all`]).
use crate::Workspace;
use crate::error::Result;
use crate::locks::LockTarget;
use std::time::Duration;
use tasker_core::model::{TaskItem, TaskSeriesNumber};
use tasker_core::versioning;

/// Перечитать, поправить (`change` возвращает изменённую задачу или None, если менять нечего), записать с версией; `attempts` раз.
/// Возвращает актуальную задачу; None — задачу тем временем удалили. `Modified` — задачу постоянно меняет кто-то другой.
pub fn modify(
    ws: &Workspace,
    task: &TaskItem,
    change: &dyn Fn(&TaskItem) -> Option<TaskItem>,
    attempts: usize,
) -> Result<Option<TaskItem>> {
    let mut current = task.clone();
    for attempt in 0..attempts {
        let Some(changed) = change(&current) else {
            return Ok(Some(current));
        };
        let mut updated = TaskItem {
            updated_at: ws.now(),
            ..changed
        };
        if let Some(version) = ws.update(&updated, &current.version)? {
            updated.version = version;
            return Ok(Some(updated));
        }
        let Some(fresh) = ws.get_by_id::<TaskItem>(&task.project_id, &task.id)? else {
            return Ok(None);
        };
        current = fresh;

        // Параллельные правки одной задачи без паузы сталкиваются снова и снова: случайная пауза, растущая с попыткой (до четверти
        // секунды), разводит их по времени.
        if attempt + 1 < attempts {
            let bound = 6u64 << attempt.min(5);
            let random = u64::from(uuid::Uuid::new_v4().as_bytes()[0]) % (bound - 1) + 1;
            ws.clock().sleep(Duration::from_millis(random));
        }
    }
    Err(versioning::modified(&format!("Task '{}'", task.title)).into())
}

/// Задачи, которые `change` действительно меняет, не должны быть заняты другим: проверка без ожидания до первой записи, затем по
/// одной с проверкой перед каждой. Возвращает, сколько задач переписано (удалённую тем временем не считаем).
pub fn modify_all<'t>(
    ws: &Workspace,
    candidates: impl IntoIterator<Item = &'t TaskItem>,
    change: &dyn Fn(&TaskItem) -> Option<TaskItem>,
    attempts: usize,
) -> Result<usize> {
    let affected: Vec<&TaskItem> = candidates.into_iter().filter(|x| change(x).is_some()).collect();
    wait_for_locks(ws, affected.iter().copied(), Some(Duration::ZERO))?;

    let mut rewritten = 0;
    for task in affected {
        // Другой мог взять блокировку, пока переписывались предыдущие задачи.
        wait_for_locks(ws, [task], Some(Duration::ZERO))?;
        if modify(ws, task, change, attempts)?.is_some() {
            rewritten += 1;
        }
    }
    Ok(rewritten)
}

/// Ждёт снятия чужих блокировок задач (без записи). `timeout` None — [`Workspace::cascade_timeout`].
pub fn wait_for_locks<'t>(ws: &Workspace, affected: impl IntoIterator<Item = &'t TaskItem>, timeout: Option<Duration>) -> Result<()> {
    let targets: Vec<LockTarget> = affected.into_iter().map(LockTarget::task).collect();
    ws.locks().wait_until_writable(&targets, timeout)
}

pub fn without(task: &TaskItem, remove: impl Fn(&TaskSeriesNumber) -> bool) -> Vec<TaskSeriesNumber> {
    task.series_numbers.iter().filter(|x| !remove(x)).copied().collect()
}
