use super::app::{App, ResultKind};
use super::step::StepState;
use ratatui::Frame;
use ratatui::layout::{Constraint, Layout, Rect};
use ratatui::style::{Color, Modifier, Style};
use ratatui::text::{Line, Span};
use ratatui::widgets::{Block, Borders, List, ListItem, Paragraph, Wrap};

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
    let Some(state) = app.state_mut() else { return };
    let block = Block::default().borders(Borders::ALL).title(format!(" {title} "));
    let inner = block.inner(body);
    frame.render_widget(block, body);
    match state {
        StepState::List(list) => {
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
                        spans.push(Span::styled(format!("  {}", item.detail), Style::default().fg(Color::DarkGray)));
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
                    Paragraph::new("  Nothing matches the filter").style(Style::default().fg(Color::DarkGray)),
                    list_area,
                );
            }
            let hint = if list.multi {
                " ↑↓ move   Space mark   Enter done   type to filter   Esc back"
            } else if list.filterable {
                " ↑↓ move   Enter choose   type to filter   Esc back"
            } else {
                " ↑↓ move   Enter choose   Esc back   q quit"
            };
            frame.render_widget(Paragraph::new(hint).style(Style::default().fg(Color::DarkGray)), footer);
        }
        StepState::Text(text) => {
            let [hint_area, input_area, error_area] =
                Layout::vertical([Constraint::Length(1), Constraint::Length(1), Constraint::Length(1)]).areas(inner);
            frame.render_widget(
                Paragraph::new(format!(" {}", text.hint)).style(Style::default().fg(Color::DarkGray)),
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
            frame.render_widget(
                Paragraph::new(" Enter accept   Esc back").style(Style::default().fg(Color::DarkGray)),
                footer,
            );
        }
        StepState::Editor(editor) => {
            let [hint_area, text_area] = Layout::vertical([Constraint::Length(1), Constraint::Min(1)]).areas(inner);
            let message = match &editor.message {
                Some(message) => format!(" {message}"),
                None => format!(" {}", editor.hint),
            };
            frame.render_widget(Paragraph::new(message).style(Style::default().fg(Color::DarkGray)), hint_area);
            let shown = if editor.text.is_empty() {
                "(empty)".to_string()
            } else {
                editor.text.clone()
            };
            frame.render_widget(Paragraph::new(shown).wrap(Wrap { trim: false }), text_area);
            frame.render_widget(
                Paragraph::new(" e edit in $EDITOR   Enter accept as shown   Esc back").style(Style::default().fg(Color::DarkGray)),
                footer,
            );
        }
        StepState::Confirm(confirm) => {
            frame.render_widget(Paragraph::new(confirm.text.clone()).wrap(Wrap { trim: false }), inner);
            frame.render_widget(
                Paragraph::new(" y / Enter confirm   n / Esc back").style(Style::default().fg(Color::DarkGray)),
                footer,
            );
        }
    }
}

fn draw_result(frame: &mut Frame<'_>, app: &mut App, body: Rect, footer: Rect) {
    let Some(result) = &app.result else { return };
    let (title, color) = match result.kind {
        ResultKind::Done => (" Done ", Color::Green),
        ResultKind::Failed => (" Error ", Color::Red),
        ResultKind::Retryable => (" Conflict ", Color::Yellow),
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
        ResultKind::Failed => " Enter back to the steps (Esc there returns to the previous step)   ↑↓ scroll",
        ResultKind::Retryable => " r retry with the current data   Enter back to the steps   ↑↓ scroll",
    };
    frame.render_widget(Paragraph::new(hint).style(Style::default().fg(Color::DarkGray)), footer);
}
