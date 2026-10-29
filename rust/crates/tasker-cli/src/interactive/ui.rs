use super::app::{App, ResultKind};
use super::step::{Inner, ListStep, StepState, TextStep};
use ratatui::Frame;
use ratatui::layout::{Constraint, Layout, Rect};
use ratatui::style::{Color, Modifier, Style};
use ratatui::text::{Line, Span};
use ratatui::widgets::{Block, Borders, Clear, List, ListItem, Paragraph, Wrap};

const DETAIL: Color = Color::Gray;
const HINT: Color = Color::DarkGray;

pub fn draw(frame: &mut Frame<'_>, app: &mut App) {
    let [header, crumbs, body, footer] = Layout::vertical([
        Constraint::Length(1),
        Constraint::Length(1),
        Constraint::Min(3),
        Constraint::Length(1),
    ])
    .areas(frame.area());
    let project = app.project.as_ref().map(|p| p.name.clone()).unwrap_or_else(|| "none".to_string());
    frame.render_widget(
        Paragraph::new(Line::from(vec![
            Span::styled(" tasker -i ", Style::default().add_modifier(Modifier::BOLD)),
            Span::raw(format!(" project: {project}   workspace: {}", app.workspace_label())),
        ]))
        .style(Style::default().bg(Color::DarkGray).fg(Color::White)),
        header,
    );
    frame.render_widget(
        Paragraph::new(format!(" {}", app.breadcrumb())).style(Style::default().fg(Color::Cyan)),
        crumbs,
    );
    app.page = body.height.saturating_sub(4).max(1) as usize;

    if app.result.is_some() {
        draw_result(frame, app, body, footer);
        return;
    }
    let title = app.current_spec().map(|s| s.title().to_string()).unwrap_or_default();
    let filters = app.has_filters();
    let refreshes = app.list_refreshes().then(|| app.refresh.as_secs_f64().ceil() as u64);
    let Some(state) = app.state_mut() else { return };
    let block = Block::default().borders(Borders::ALL).title(format!(" {title} "));
    let inner = block.inner(body);
    frame.render_widget(block, body);
    match state {
        StepState::List(list) => {
            let mut hint = draw_list(frame, list, inner);
            if filters {
                hint.push_str("   Ctrl+F filters");
            }
            if let Some(seconds) = refreshes {
                hint.push_str(&format!("   refreshes every {seconds} s"));
            }
            frame.render_widget(Paragraph::new(hint).style(Style::default().fg(HINT)), footer);
        }
        StepState::Text(text) => {
            draw_text(frame, text, inner);
            frame.render_widget(Paragraph::new(" Enter accept   Esc back").style(Style::default().fg(HINT)), footer);
        }
        StepState::Form(form) => {
            let rows: Vec<ListItem> = form
                .sections
                .iter()
                .map(|section| {
                    ListItem::new(Line::from(vec![
                        Span::raw(format!("{:<18}", section.label)),
                        Span::styled(section.summary(), Style::default().fg(DETAIL)),
                    ]))
                })
                .chain(std::iter::once(ListItem::new(Line::from(Span::styled(
                    format!("▶ {}", form.submit),
                    Style::default().add_modifier(Modifier::BOLD),
                )))))
                .collect();
            let mut scroll = ratatui::widgets::ListState::default();
            let widget = if form.popup.is_some() {
                List::new(rows)
            } else {
                scroll.select(Some(form.cursor));
                List::new(rows)
                    .highlight_style(Style::default().add_modifier(Modifier::REVERSED))
                    .highlight_symbol("› ")
            };
            frame.render_stateful_widget(widget, inner, &mut scroll);
            let hint = match &mut form.popup {
                None => " ↑↓ move   Enter open a setting or show   Backspace clear   Esc back".to_string(),
                Some(popup) => {
                    let layer = popup.layer_mut();
                    let height = match &layer.inner {
                        Inner::List(list) => list.items.len() as u16 + 3,
                        Inner::Text(_) => 5,
                    };
                    let width = (inner.width * 4 / 5).max(30.min(inner.width));
                    let height = height.min(inner.height);
                    let area = Rect::new(
                        inner.x + (inner.width - width) / 2,
                        inner.y + (inner.height - height) / 2,
                        width,
                        height,
                    );
                    let block = Block::default()
                        .borders(Borders::ALL)
                        .title(format!(" {} ", layer.title))
                        .border_style(Style::default().fg(Color::Cyan));
                    let popup_inner = block.inner(area);
                    frame.render_widget(Clear, area);
                    frame.render_widget(block, area);
                    match &mut layer.inner {
                        Inner::List(list) => {
                            let hint = draw_list(frame, list, popup_inner);
                            if list.multi {
                                hint.replace("Enter done", "Enter apply").replace("Esc back", "Esc cancel")
                            } else {
                                hint.replace("Esc back", "Esc close")
                            }
                        }
                        Inner::Text(text) => {
                            draw_text(frame, text, popup_inner);
                            " Enter accept   Esc close".to_string()
                        }
                    }
                }
            };
            frame.render_widget(Paragraph::new(hint).style(Style::default().fg(HINT)), footer);
        }
        StepState::Editor(editor) => {
            let [hint_area, text_area] = Layout::vertical([Constraint::Length(1), Constraint::Min(1)]).areas(inner);
            let message = match &editor.message {
                Some(message) => format!(" {message}"),
                None => format!(" {}", editor.hint),
            };
            frame.render_widget(Paragraph::new(message).style(Style::default().fg(HINT)), hint_area);
            let shown = if editor.text.is_empty() {
                "(empty)".to_string()
            } else {
                editor.text.clone()
            };
            frame.render_widget(Paragraph::new(shown).wrap(Wrap { trim: false }), text_area);
            frame.render_widget(
                Paragraph::new(" e edit in $EDITOR   Enter accept as shown   Esc back").style(Style::default().fg(HINT)),
                footer,
            );
        }
        StepState::Confirm(confirm) => {
            frame.render_widget(Paragraph::new(confirm.text.clone()).wrap(Wrap { trim: false }), inner);
            frame.render_widget(
                Paragraph::new(" y / Enter confirm   n / Esc back").style(Style::default().fg(HINT)),
                footer,
            );
        }
    }
}

fn draw_list(frame: &mut Frame<'_>, list: &mut ListStep, inner: Rect) -> String {
    let [filter_area, list_area] = Layout::vertical([Constraint::Length(1), Constraint::Min(1)]).areas(inner);
    let filter_text = if list.filterable {
        if list.filter.is_empty() {
            " Type to filter".to_string()
        } else {
            format!(" Filter: {}", list.filter)
        }
    } else {
        String::new()
    };
    let message = list.message.clone().map(|m| format!("   {m}")).unwrap_or_default();
    frame.render_widget(
        Paragraph::new(Line::from(vec![
            Span::styled(filter_text, Style::default().fg(Color::Yellow)),
            Span::styled(message, Style::default().fg(Color::Red)),
        ])),
        filter_area,
    );
    let visible = list.visible();
    let items: Vec<ListItem> = visible
        .iter()
        .map(|&index| {
            let item = &list.items[index];
            let mark = if list.multi {
                match list.pick_number(&item.key) {
                    Some(n) => format!("[{n}] "),
                    None => "[ ] ".to_string(),
                }
            } else {
                String::new()
            };
            let mut spans = vec![Span::raw(mark), Span::raw(item.label.clone())];
            if !item.detail.is_empty() {
                spans.push(Span::styled(format!("  {}", item.detail), Style::default().fg(DETAIL)));
            }
            ListItem::new(Line::from(spans))
        })
        .collect();
    let empty = items.is_empty();
    let widget = List::new(items)
        .highlight_style(Style::default().add_modifier(Modifier::REVERSED))
        .highlight_symbol("› ");
    list.scroll.select(if empty { None } else { Some(list.cursor) });
    frame.render_stateful_widget(widget, list_area, &mut list.scroll);
    if empty {
        frame.render_widget(
            Paragraph::new("  Nothing matches the filter").style(Style::default().fg(DETAIL)),
            list_area,
        );
    }
    if list.multi {
        " ↑↓ move   Space mark   Enter done   type to filter   Esc back"
    } else if list.filterable {
        " ↑↓ move   Enter choose   type to filter   Esc back"
    } else {
        " ↑↓ move   Enter choose   Esc back   q quit"
    }
    .to_string()
}

fn draw_text(frame: &mut Frame<'_>, text: &TextStep, inner: Rect) {
    let [hint_area, input_area, error_area] =
        Layout::vertical([Constraint::Length(1), Constraint::Length(1), Constraint::Length(1)]).areas(inner);
    frame.render_widget(
        Paragraph::new(format!(" {}", text.hint)).style(Style::default().fg(HINT)),
        hint_area,
    );
    frame.render_widget(Paragraph::new(format!(" > {}", text.value)), input_area);
    frame.set_cursor_position((input_area.x + 3 + text.cursor_column() as u16, input_area.y));
    if let Some(error) = &text.error {
        frame.render_widget(
            Paragraph::new(format!(" {error}")).style(Style::default().fg(Color::Red)),
            error_area,
        );
    }
}

fn draw_result(frame: &mut Frame<'_>, app: &mut App, body: Rect, footer: Rect) {
    let Some(result) = &app.result else { return };
    let (title, color) = match result.kind {
        ResultKind::Done => (" Done ".to_string(), Color::Green),
        ResultKind::View => (
            format!(
                " View  updated {} s ago, refreshes every {} s ",
                result.at.elapsed().as_secs(),
                app.refresh.as_secs_f64().ceil() as u64
            ),
            Color::Cyan,
        ),
        ResultKind::Failed => (" Error ".to_string(), Color::Red),
        ResultKind::Retryable => (" Conflict ".to_string(), Color::Yellow),
    };
    let block = Block::default()
        .borders(Borders::ALL)
        .title(title)
        .border_style(Style::default().fg(color));
    frame.render_widget(
        Paragraph::new(result.text.clone())
            .wrap(Wrap { trim: false })
            .scroll((result.scroll, 0))
            .block(block),
        body,
    );
    let hint = match result.kind {
        ResultKind::Done => " Enter back to the menu   ↑↓ scroll",
        ResultKind::View => " Esc previous step   f change a setting   r refresh now   q menu   ↑↓ scroll",
        ResultKind::Failed => " Enter back to the steps (Esc there returns to the previous step)   ↑↓ scroll",
        ResultKind::Retryable => " r retry with the current data   Enter back to the steps   ↑↓ scroll",
    };
    frame.render_widget(Paragraph::new(hint).style(Style::default().fg(HINT)), footer);
}
