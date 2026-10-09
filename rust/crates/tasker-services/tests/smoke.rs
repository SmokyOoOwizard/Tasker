mod common;

use common::*;
use tasker_core::ids::default_link_type_id;
use tasker_core::tasks::Page;
use tasker_services::series::CreateSeries;

#[test]
fn entities_are_created_listed_linked_and_deleted() {
    let env = Env::new();
    let ws = &env.ws;
    let p = &env.project;

    let series = ws.series().create(p, &CreateSeries::new("Tasks", "TSK")).unwrap();
    let a = env.task("Alpha");
    let b = env.task("Beta");
    let a = ws.tasks().add_to_series(p, &a.id, &series.id, Some(&a.version)).unwrap().unwrap();
    let b = ws.tasks().add_to_series(p, &b.id, &series.id, Some(&b.version)).unwrap().unwrap();
    assert_eq!(a.series_numbers[0].number, 1);
    assert_eq!(b.series_numbers[0].number, 2);

    let found = ws.tasks().resolve(p, "TSK-2", false).unwrap();
    assert_eq!(found.len(), 1);
    assert_eq!(found[0].id, b.id);

    let blocks = default_link_type_id(p, "blocks");
    let linked = ws.links().add(p, &a.id, &blocks, &b.id, None).unwrap().unwrap();
    assert_eq!(linked.links.len(), 1);
    // Типы по умолчанию записаны при первой записи связи.
    assert_eq!(ws.link_types().get_all(p).unwrap().len(), 6);
    let cycle = message(ws.links().add(p, &b.id, &blocks, &a.id, None));
    assert_eq!(
        cycle,
        "Cycle: TSK-2 → TSK-1 → TSK-2 (link type 'Blocks' does not allow cycles; remove the opposite link or allow cycles for the type)"
    );

    let views = ws.links().get_links(p, &b.id).unwrap().unwrap();
    assert_eq!(views.len(), 1);
    assert_eq!(views[0].name, "is blocked by");

    let list = ws.tasks().list(p, None, None, Page::first(50), -1, None).unwrap();
    assert_eq!(list.total_count, 2);
    assert_eq!(list.data[0].links_count, 1);

    let fresh = ws.tasks().get_by_id(p, &b.id).unwrap().unwrap();
    assert!(ws.tasks().delete(p, &b.id, Some(&fresh.version)).unwrap());
    let a = ws.tasks().get_by_id(p, &a.id).unwrap().unwrap();
    assert!(a.links.is_empty());

    let stats = ws.projects().get_stats(p).unwrap().unwrap();
    assert_eq!(
        (
            stats.tasks,
            stats.statuses,
            stats.status_sets,
            stats.task_types,
            stats.series,
            stats.link_types
        ),
        (1, 2, 1, 1, 1, 6)
    );
}
