import { useEffect, useRef, useState, type FormEvent } from 'react'
import { api } from '../../api'
import { IconEdit, IconTrash } from '../../components/Icons'
import { LockBadge } from '../../components/LockBadge'
import { DescriptionInput, IconButton, ItemDescription } from '../../components/ui'
import { useAction } from '../../hooks/useAction'
import { useEditLock } from '../../hooks/useEditLock'
import type { SettingsSectionProps, Status } from '../../types'

// Цвет нового статуса — следующий из палитры, чтобы статусы сразу различались.
const palette = ['#64748b', '#6366f1', '#f59e0b', '#14b8a6', '#e11d48', '#8b5cf6', '#0ea5e9', '#22c55e']
const nextColor = (count: number) => palette[count % palette.length]

/** Статусы проекта: название и цвет. Порядок у статусов свой в каждом наборе (см. StatusSetsSection). */
function StatusesSection({ base, settings, reload, locks }: SettingsSectionProps) {
  const { busy, error, setError, run } = useAction(reload)
  const editLock = useEditLock()
  const [name, setName] = useState('')
  const [description, setDescription] = useState('')
  const [color, setColor] = useState(nextColor(settings.statuses.length))
  const [editingId, setEditingId] = useState<string | null>(null)

  const add = async (e: FormEvent) => {
    e.preventDefault()
    if (await run(() => api(`${base}/statuses`, { method: 'POST', body: { name, color, description } }))) {
      setName('')
      setDescription('')
      setColor(nextColor(settings.statuses.length + 1))
    }
  }

  // Переименование открывается, только если запись не правит другой: на время формы она блокируется.
  const startEdit = async (status: Status) => {
    const problem = await editLock.lock(`${base}/statuses/${status.id}`)
    if (problem) setError(problem)
    else setEditingId(status.id)
  }

  const stopEdit = () => {
    editLock.unlock()
    setEditingId(null)
  }

  const update = (status: Status, changes: { name?: string; color?: string; description?: string }) =>
    run(() => api(`${base}/statuses/${status.id}`, { method: 'PATCH', body: { ...changes, version: status.version } }))

  const remove = (status: Status) => {
    if (!confirm(`Удалить статус «${status.name}»?`)) return
    run(() => api(`${base}/statuses/${status.id}?version=${encodeURIComponent(status.version)}`, { method: 'DELETE' }))
  }

  return (
    <section className="card">
      <h2 className="card-title">Статусы</h2>

      {settings.statuses.length === 0 ? (
        <p className="muted">Статусов пока нет. Добавьте, например, «Открыта», «В работе» и «Готово».</p>
      ) : (
        <ul className="item-list">
          {settings.statuses.map((status) => {
            const other = locks.byOther('status', status.id)
            return (
            <li key={status.id} className="item-row">
              <ColorInput key={status.version} value={status.color} onCommit={(next) => update(status, { color: next })} />
              {editingId === status.id ? (
                <StatusEditForm
                  status={status}
                  busy={busy}
                  onCancel={stopEdit}
                  onSave={async (changes) => (await update(status, changes)) && stopEdit()}
                />
              ) : (
                <>
                  <span className="item-text">
                    <span className="item-name">{status.name}</span>
                    <ItemDescription text={status.description} />
                  </span>
                  {other && <LockBadge lock={other} />}
                  <span className="item-actions">
                    <IconButton title="Изменить" disabled={!!other} onClick={() => startEdit(status)}>
                      <IconEdit />
                    </IconButton>
                    <IconButton title="Удалить" danger disabled={busy || !!other} onClick={() => remove(status)}>
                      <IconTrash />
                    </IconButton>
                  </span>
                </>
              )}
            </li>
            )
          })}
        </ul>
      )}

      {error && <div className="error-message">{error}</div>}

      <form className="inline-form" onSubmit={add}>
        <input type="color" className="color-input" title="Цвет" value={color} onChange={(e) => setColor(e.target.value)} />
        <input className="input" placeholder="Новый статус" value={name} onChange={(e) => setName(e.target.value)} />
        <DescriptionInput value={description} onChange={setDescription} placeholder="Описание: что значит статус и когда его ставить (необязательно)" />
        <button className="button" type="submit" disabled={busy || !name.trim()}>
          Добавить
        </button>
      </form>
    </section>
  )
}

/** Правка названия и описания статуса в строке списка. */
function StatusEditForm({
  status,
  busy,
  onSave,
  onCancel,
}: {
  status: Status
  busy: boolean
  onSave: (changes: { name: string; description: string }) => void
  onCancel: () => void
}) {
  const [name, setName] = useState(status.name)
  const [description, setDescription] = useState(status.description)
  return (
    <form
      className="inline-form inline-form-row"
      onSubmit={(e) => {
        e.preventDefault()
        onSave({ name, description })
      }}
      onKeyDown={(e) => e.key === 'Escape' && onCancel()}
    >
      <input className="input" value={name} onChange={(e) => setName(e.target.value)} autoFocus />
      <DescriptionInput value={description} onChange={setDescription} placeholder="Описание (необязательно)" />
      <button className="button" type="submit" disabled={busy || !name.trim()}>
        Сохранить
      </button>
      <button className="button button-secondary" type="button" onClick={onCancel}>
        Отмена
      </button>
    </form>
  )
}

/**
 * Цвет статуса. Сохраняется, когда выбор в палитре затих: палитра шлёт изменения непрерывно,
 * а PATCH на каждое из них менял бы версию и конфликтовал сам с собой.
 */
function ColorInput({ value, onCommit }: { value: string; onCommit: (color: string) => void }) {
  const [color, setColor] = useState(value)
  const timer = useRef<number | undefined>(undefined)
  useEffect(() => () => window.clearTimeout(timer.current), [])

  return (
    <input
      type="color"
      className="color-input"
      title="Цвет"
      value={color}
      onChange={(e) => {
        const next = e.target.value
        setColor(next)
        window.clearTimeout(timer.current)
        timer.current = window.setTimeout(() => next !== value && onCommit(next), 600)
      }}
    />
  )
}

export default StatusesSection
