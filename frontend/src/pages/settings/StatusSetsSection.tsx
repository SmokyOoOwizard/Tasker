import { useState, type FormEvent } from 'react'
import { api } from '../../api'
import { IconArrowDown, IconArrowUp, IconEdit, IconTrash } from '../../components/Icons'
import { LockBadge } from '../../components/LockBadge'
import { IconButton, StatusChip } from '../../components/ui'
import { useAction } from '../../hooks/useAction'
import { useEditLock } from '../../hooks/useEditLock'
import type { SettingsSectionProps, Status, StatusSet } from '../../types'

/**
 * Наборы статусов: какие статусы и в каком порядке доступны задачам. Набор выбирается у типа задачи,
 * поэтому без набора тип не создать.
 */
function StatusSetsSection({ base, settings, reload, locks }: SettingsSectionProps) {
  const { busy, error, setError, run } = useAction(reload)
  const editLock = useEditLock()
  // null — ничего не редактируется, 'new' — создание, иначе id набора.
  const [editing, setEditing] = useState<string | null>(null)
  const statusById = new Map(settings.statuses.map((s) => [s.id, s]))

  // Правка набора открывается, только если его не правит другой: на время формы он блокируется.
  const startEdit = async (set: StatusSet) => {
    const problem = await editLock.lock(`${base}/status-sets/${set.id}`)
    if (problem) setError(problem)
    else setEditing(set.id)
  }

  const stopEdit = () => {
    editLock.unlock()
    setEditing(null)
  }

  const save = async (set: StatusSet | null, name: string, statusIds: string[]) => {
    const ok = await run(() =>
      set
        ? api(`${base}/status-sets/${set.id}`, { method: 'PATCH', body: { name, statusIds, version: set.version } })
        : api(`${base}/status-sets`, { method: 'POST', body: { name, statusIds } }),
    )
    if (ok) stopEdit()
  }

  const remove = (set: StatusSet) => {
    if (!confirm(`Удалить набор «${set.name}»?`)) return
    run(() => api(`${base}/status-sets/${set.id}?version=${encodeURIComponent(set.version)}`, { method: 'DELETE' }))
  }

  return (
    <section className="card">
      <h2 className="card-title">Наборы статусов</h2>
      <p className="card-hint">Набор — статусы, по которым проходит задача, в порядке отображения. Тип задачи берёт статусы из своего набора.</p>

      {settings.statusSets.length === 0 && editing !== 'new' && <p className="muted">Наборов пока нет.</p>}

      <ul className="item-list">
        {settings.statusSets.map((set) => {
          const other = locks.byOther('statusSet', set.id)
          return editing === set.id ? (
            <li key={set.id} className="item-row item-row-editor">
              <StatusSetEditor
                statuses={settings.statuses}
                initialName={set.name}
                initialStatusIds={set.statusIds}
                busy={busy}
                onSave={(name, ids) => save(set, name, ids)}
                onCancel={stopEdit}
              />
            </li>
          ) : (
            <li key={set.id} className="item-row">
              <span className="item-name">{set.name}</span>
              {other && <LockBadge lock={other} />}
              <span className="chips">
                {set.statusIds.map((id) => (
                  <StatusChip key={id} status={statusById.get(id)} />
                ))}
              </span>
              <span className="item-actions">
                <IconButton title="Изменить" disabled={!!other} onClick={() => startEdit(set)}>
                  <IconEdit />
                </IconButton>
                <IconButton title="Удалить" danger disabled={busy || !!other} onClick={() => remove(set)}>
                  <IconTrash />
                </IconButton>
              </span>
            </li>
          )
        })}
      </ul>

      {error && <div className="error-message">{error}</div>}

      {editing === 'new' ? (
        <div className="item-row item-row-editor">
          <StatusSetEditor
            statuses={settings.statuses}
            initialName=""
            initialStatusIds={[]}
            busy={busy}
            onSave={(name, ids) => save(null, name, ids)}
            onCancel={() => setEditing(null)}
          />
        </div>
      ) : (
        <button
          type="button"
          className="button button-secondary"
          disabled={settings.statuses.length === 0}
          title={settings.statuses.length === 0 ? 'Сначала добавьте статусы' : undefined}
          onClick={() => setEditing('new')}
        >
          Новый набор
        </button>
      )}
    </section>
  )
}

function StatusSetEditor({
  statuses,
  initialName,
  initialStatusIds,
  busy,
  onSave,
  onCancel,
}: {
  statuses: Status[]
  initialName: string
  initialStatusIds: string[]
  busy: boolean
  onSave: (name: string, statusIds: string[]) => void
  onCancel: () => void
}) {
  const [name, setName] = useState(initialName)
  const [ids, setIds] = useState(initialStatusIds)
  const statusById = new Map(statuses.map((s) => [s.id, s]))
  const available = statuses.filter((s) => !ids.includes(s.id))

  const move = (index: number, delta: number) => {
    const next = [...ids]
    ;[next[index], next[index + delta]] = [next[index + delta], next[index]]
    setIds(next)
  }

  const submit = (e: FormEvent) => {
    e.preventDefault()
    onSave(name, ids)
  }

  return (
    <form className="set-editor" onSubmit={submit} onKeyDown={(e) => e.key === 'Escape' && onCancel()}>
      <input className="input" placeholder="Название набора" value={name} onChange={(e) => setName(e.target.value)} autoFocus />

      <ol className="set-editor-list">
        {ids.map((id, index) => (
          <li key={id} className="set-editor-item">
            <span className="set-editor-index">{index + 1}</span>
            <StatusChip status={statusById.get(id)} />
            <span className="item-actions">
              <IconButton title="Выше" disabled={index === 0} onClick={() => move(index, -1)}>
                <IconArrowUp />
              </IconButton>
              <IconButton title="Ниже" disabled={index === ids.length - 1} onClick={() => move(index, 1)}>
                <IconArrowDown />
              </IconButton>
              <IconButton title="Убрать из набора" danger onClick={() => setIds(ids.filter((x) => x !== id))}>
                <IconTrash />
              </IconButton>
            </span>
          </li>
        ))}
      </ol>

      {available.length > 0 && (
        <div className="set-editor-add">
          <span className="muted">Добавить:</span>
          {available.map((status) => (
            <button key={status.id} type="button" className="chip chip-button" onClick={() => setIds([...ids, status.id])}>
              <span className="color-dot" style={{ background: status.color }} />
              {status.name}
            </button>
          ))}
        </div>
      )}

      <div className="inline-form-row">
        <button className="button" type="submit" disabled={busy || !name.trim() || ids.length === 0}>
          Сохранить
        </button>
        <button className="button button-secondary" type="button" onClick={onCancel}>
          Отмена
        </button>
      </div>
    </form>
  )
}

export default StatusSetsSection
