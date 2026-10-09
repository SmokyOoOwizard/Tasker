//! Сценарии `TaskHierarchyTests.cs` (уровень сервисов): список деревом — эпик и дочерние задачи, несколько родителей, фильтры,
//! порядок, страницы верхнего уровня, предел глубины; плоский список с `parent_ids`/`child_count`; ядро иерархии.
mod common;

use common::*;
use std::collections::HashMap;
use std::time::Duration;
use tasker_core::ids::default_link_type_id;
use tasker_core::model::TaskItem;
use tasker_core::tasks::{Page, TaskFilter};
use tasker_files::index::LinkEdge;
use tasker_services::hierarchy::{MAX_DEPTH, TaskHierarchy};
use tasker_services::preview::TaskTreeList;
use tasker_services::task::CreateTask;
use uuid::Uuid;

struct H {
    env: Env,
    series: Uuid,
    parent: Uuid,
}

impl H {
    fn new() -> H {
        let env = Env::new();
        let series = env.series("TSK").id;
        let parent = default_link_type_id(&env.project, "parent");
        H { env, series, parent }
    }

    /// Задача в серии TSK; `parents` — ссылки `TSK-N` родителей (связь «includes» от родителя к ней).
    fn create(&self, title: &str, status: Option<Uuid>, parents: &[&str]) -> TaskItem {
        self.env.clock.advance(Duration::from_secs(1));
        let task = self
            .env
            .ws
            .tasks()
            .create(
                &self.env.project,
                &CreateTask {
                    status_id: status,
                    series_ids: Some(vec![self.series]),
                    ..CreateTask::new(title, self.env.task_type.id)
                },
            )
            .unwrap();
        for parent in parents {
            let parent = self.env.ws.tasks().resolve(&self.env.project, parent, false).unwrap().remove(0);
            self.env
                .ws
                .links()
                .add(&self.env.project, &parent.id, &self.parent, &task.id, None)
                .unwrap();
        }
        task
    }

    fn tree(&self, filter: Option<&TaskFilter>, page: Page, sort: Option<&str>) -> TaskTreeList {
        self.env
            .ws
            .tasks()
            .list_tree(&self.env.project, filter, None, page, -1, sort)
            .unwrap()
    }

    /// Форма дерева: `TSK-1 >TSK-2 >>TSK-3 TSK-4`, повтор помечен `+`.
    fn shape(&self, list: &TaskTreeList) -> String {
        list.data
            .iter()
            .map(|row| {
                format!(
                    "{}TSK-{}{}",
                    ">".repeat(row.depth),
                    row.item.task.series_numbers[0].number,
                    if row.repeated { "+" } else { "" }
                )
            })
            .collect::<Vec<_>>()
            .join(" ")
    }
}

fn status_filter(status: Uuid) -> TaskFilter {
    TaskFilter {
        status_ids: Some(vec![status]),
        ..TaskFilter::default()
    }
}

#[test]
fn an_epic_is_followed_by_its_children_and_nests_three_levels_deep() {
    let h = H::new();
    h.create("Epic", None, &[]);
    h.create("First", None, &["TSK-1"]);
    h.create("Second", None, &["TSK-1"]);
    h.create("Solo", None, &[]);
    let list = h.tree(None, Page::first(50), None);
    assert_eq!((list.total_count, list.top_level_count), (4, 2));
    assert_eq!(h.shape(&list), "TSK-1 >TSK-2 >TSK-3 TSK-4");

    let h = H::new();
    h.create("Epic", None, &[]);
    h.create("Story", None, &["TSK-1"]);
    h.create("Sub", None, &["TSK-2"]);
    h.create("Subsub", None, &["TSK-3"]);
    assert_eq!(h.shape(&h.tree(None, Page::first(50), None)), "TSK-1 >TSK-2 >>TSK-3 >>>TSK-4");
}

#[test]
fn a_task_with_two_parents_is_shown_under_each_with_its_subtree_and_counted_once() {
    let h = H::new();
    h.create("Epic one", None, &[]);
    h.create("Epic two", None, &[]);
    h.create("Shared", None, &["TSK-1", "TSK-2"]);
    h.create("Part", None, &["TSK-3"]);
    let list = h.tree(None, Page::first(50), None);
    assert_eq!(list.total_count, 4);
    assert_eq!(h.shape(&list), "TSK-1 >TSK-3 >>TSK-4 TSK-2 >TSK-3+ >>TSK-4+");
    assert!(!list.data[1].repeated && list.data[4].repeated);

    let flat = h
        .env
        .ws
        .tasks()
        .list(&h.env.project, None, None, Page::first(50), -1, None)
        .unwrap();
    let shared = flat.data.iter().find(|x| x.task.title == "Shared").unwrap();
    assert_eq!((shared.parent_ids.len(), shared.child_count), (2, 1));
}

#[test]
fn filters_act_on_every_task_separately_and_a_child_without_a_matching_parent_goes_to_the_top_level() {
    let h = H::new();
    let done = h.env.done.id;
    h.create("Epic todo", None, &[]);
    h.create("Child done", Some(done), &["TSK-1"]);
    h.create("Child todo", None, &["TSK-1"]);
    h.create("Epic done", Some(done), &[]);
    h.create("Child of done", None, &["TSK-4"]);
    h.create("Two parents", None, &["TSK-1", "TSK-4"]);

    let todo = h.tree(Some(&status_filter(h.env.backlog.id)), Page::first(50), None);
    assert_eq!(h.shape(&todo), "TSK-1 >TSK-3 >TSK-6 TSK-5");
    assert_eq!(todo.total_count, 4);
    let done_list = h.tree(Some(&status_filter(done)), Page::first(50), None);
    assert_eq!(h.shape(&done_list), "TSK-2 TSK-4");
    let page = h.tree(Some(&status_filter(done)), Page::new(0, 1), None);
    assert_eq!((page.total_count, page.top_level_count, page.data.len()), (2, 2, 1));
    let sorted = h.tree(Some(&status_filter(done)), Page::new(0, 1), Some("-created"));
    assert_eq!(h.shape(&sorted), "TSK-4");

    let h = H::new();
    let done = h.env.done.id;
    h.create("Epic A", Some(done), &[]);
    h.create("Epic B", Some(done), &[]);
    h.create("Shared", None, &["TSK-1", "TSK-2"]);
    assert_eq!(
        h.shape(&h.tree(Some(&status_filter(h.env.backlog.id)), Page::first(50), None)),
        "TSK-3"
    );
}

#[test]
fn sort_orders_the_top_level_and_the_children_of_each_parent() {
    let h = H::new();
    h.create("Alpha", None, &[]);
    h.create("Beta", None, &[]);
    h.create("Zed child", None, &["TSK-1"]);
    h.create("Abc child", None, &["TSK-1"]);
    h.create("Mid child", None, &["TSK-2"]);
    assert_eq!(h.shape(&h.tree(None, Page::first(50), None)), "TSK-1 >TSK-3 >TSK-4 TSK-2 >TSK-5");
    assert_eq!(
        h.shape(&h.tree(None, Page::first(50), Some("title"))),
        "TSK-1 >TSK-4 >TSK-3 TSK-2 >TSK-5"
    );
    assert_eq!(
        h.shape(&h.tree(None, Page::first(50), Some("-title"))),
        "TSK-2 >TSK-5 TSK-1 >TSK-3 >TSK-4"
    );
}

#[test]
fn pages_count_top_level_tasks_and_never_split_a_subtree() {
    let h = H::new();
    for i in 1..=3 {
        h.create(&format!("Epic {i}"), None, &[]);
    }
    h.create("Child of 1", None, &["TSK-1"]);
    h.create("Child of 1 too", None, &["TSK-1"]);
    h.create("Child of 3", None, &["TSK-3"]);

    let first = h.tree(None, Page::new(0, 1), None);
    assert_eq!((first.total_count, first.top_level_count, first.offset, first.limit), (6, 3, 0, 1));
    assert_eq!(h.shape(&first), "TSK-1 >TSK-4 >TSK-5");
    assert_eq!(h.shape(&h.tree(None, Page::new(1, 1), None)), "TSK-2");
    assert_eq!(h.shape(&h.tree(None, Page::new(2, 5), None)), "TSK-3 >TSK-6");
    assert!(h.tree(None, Page::new(9, 50), None).data.is_empty());
    assert_eq!(
        h.shape(&h.tree(None, Page::first(200), None)),
        "TSK-1 >TSK-4 >TSK-5 TSK-2 TSK-3 >TSK-6"
    );

    // Плоский список страниц верхнего уровня не знает.
    let flat = h
        .env
        .ws
        .tasks()
        .list(&h.env.project, None, None, Page::new(0, 2), -1, None)
        .unwrap();
    assert_eq!((flat.total_count, flat.data.len()), (6, 2));
    let epic = flat.data.iter().find(|x| x.task.title == "Epic 1").unwrap();
    assert_eq!((epic.child_count, epic.parent_ids.len()), (2, 0));
    let child = h
        .env
        .ws
        .tasks()
        .list(&h.env.project, None, None, Page::first(50), -1, None)
        .unwrap()
        .data
        .into_iter()
        .find(|x| x.task.title == "Child of 1")
        .unwrap();
    assert_eq!((child.child_count, child.parent_ids.len()), (0, 1));

    // Без иерархических связей дерево — обычная страница.
    let plain = Env::new();
    plain.task("A");
    plain.task("B");
    let tree = plain
        .ws
        .tasks()
        .list_tree(&plain.project, None, None, Page::first(50), -1, None)
        .unwrap();
    assert_eq!((tree.total_count, tree.top_level_count, tree.data.len()), (2, 2, 2));
    assert!(tree.data.iter().all(|x| x.depth == 0 && !x.repeated));
}

#[test]
fn a_deep_chain_stops_at_the_depth_limit_and_every_task_is_still_found() {
    let h = H::new();
    h.create("Level 0", None, &[]);
    for i in 1..MAX_DEPTH + 2 {
        h.create(&format!("Level {i}"), None, &[&format!("TSK-{i}")]);
    }
    let list = h.tree(None, Page::first(50), None);
    assert_eq!(list.total_count, MAX_DEPTH + 2);
    assert_eq!(list.data.len(), MAX_DEPTH);
    assert_eq!(list.data.last().unwrap().depth, MAX_DEPTH - 1);
}

#[test]
fn dozens_of_tasks_are_listed_by_pages_of_top_level_tasks() {
    let h = H::new();
    for i in 1..=12 {
        h.create(&format!("Epic {i:02}"), None, &[]);
    }
    for i in 1..=12 {
        h.create(&format!("Child {i:02}"), None, &[&format!("TSK-{i}")]);
    }
    let page = h.tree(None, Page::new(4, 4), None);
    assert_eq!((page.total_count, page.top_level_count, page.data.len()), (24, 12, 8));
    assert_eq!(page.data.iter().filter(|x| x.depth == 0).count(), 4);
    assert_eq!(page.data[0].item.task.title, "Epic 05");
    assert_eq!(page.data[1].item.task.title, "Child 05");
}

#[test]
fn parent_links_across_two_hierarchical_types_form_one_graph_and_cycles_are_rejected() {
    let h = H::new();
    let env = &h.env;
    let epic_link = env
        .ws
        .link_types()
        .create(
            &env.project,
            &tasker_services::link_type::CreateLinkType {
                hierarchical: Some(true),
                ..tasker_services::link_type::CreateLinkType::new("Epic link", "has story", Some("belongs to epic"))
            },
        )
        .unwrap();
    let a = h.create("A", None, &[]);
    let b = h.create("B", None, &["TSK-1"]);
    let cycle = message(env.ws.links().add(&env.project, &b.id, &epic_link.id, &a.id, None));
    assert!(cycle.starts_with("Cycle: TSK-2 → TSK-1 → TSK-2"));
    assert!(cycle.contains("'Epic link'"));
    assert!(message(env.ws.links().add(&env.project, &b.id, &h.parent, &a.id, None)).starts_with("Cycle:"));
    assert_eq!(
        message(env.ws.links().add(&env.project, &a.id, &h.parent, &a.id, None)),
        "TargetId: a task cannot be linked to itself"
    );

    env.ws.links().remove(&env.project, &a.id, &h.parent, &b.id, None).unwrap();
    env.ws.links().add(&env.project, &a.id, &epic_link.id, &b.id, None).unwrap();
    assert_eq!(h.shape(&h.tree(None, Page::first(50), None)), "TSK-1 >TSK-2");
    let views = env.ws.links().get_links(&env.project, &b.id).unwrap().unwrap();
    assert_eq!(views[0].name, "belongs to epic");
}

// ---- ядро: порядок верхнего уровня и защита от циклов ----

fn edge(a: Uuid, b: Uuid) -> LinkEdge {
    LinkEdge {
        source_id: a,
        target_id: b,
    }
}

#[test]
fn a_cycle_of_parents_cannot_hide_tasks_and_does_not_hang_the_walk() {
    let (a, b, c) = (Uuid::new_v4(), Uuid::new_v4(), Uuid::new_v4());
    let hierarchy = TaskHierarchy::new(&[edge(a, b), edge(b, c), edge(c, a)]);
    let ordered = [a, b, c];
    let top = hierarchy.top_level(&ordered);
    assert_eq!(top, vec![a]);
    let position: HashMap<Uuid, usize> = ordered.iter().enumerate().map(|(i, id)| (*id, i)).collect();
    let rows = hierarchy.rows(&top, &position);
    assert_eq!(rows.iter().map(|x| x.id).collect::<Vec<_>>(), vec![a, b, c]);
    assert!(rows.iter().all(|x| !x.repeated));
}

#[test]
fn a_diamond_repeats_the_shared_subtree_and_marks_the_repeats() {
    let (root, left, right, shared, leaf) = (Uuid::new_v4(), Uuid::new_v4(), Uuid::new_v4(), Uuid::new_v4(), Uuid::new_v4());
    let hierarchy = TaskHierarchy::new(&[
        edge(root, left),
        edge(root, right),
        edge(left, shared),
        edge(right, shared),
        edge(shared, leaf),
    ]);
    let ordered = [root, left, right, shared, leaf];
    let position: HashMap<Uuid, usize> = ordered.iter().enumerate().map(|(i, id)| (*id, i)).collect();
    let rows = hierarchy.rows(&hierarchy.top_level(&ordered), &position);
    assert_eq!(
        rows.iter().map(|x| (x.id, x.depth, x.repeated)).collect::<Vec<_>>(),
        vec![
            (root, 0, false),
            (left, 1, false),
            (shared, 2, false),
            (leaf, 3, false),
            (right, 1, false),
            (shared, 2, true),
            (leaf, 3, true)
        ]
    );
    let mut parents = hierarchy.parents_of(&shared);
    parents.sort();
    let mut expected = vec![left, right];
    expected.sort();
    assert_eq!(parents, expected);
    assert_eq!(hierarchy.child_count(&shared), 1);
}
