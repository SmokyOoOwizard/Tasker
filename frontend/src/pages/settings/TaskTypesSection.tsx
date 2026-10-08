import { useState, type FormEvent } from 'react'
import { api } from '../../api'
import { IconEdit, IconTrash } from '../../components/Icons'
import { LockBadge } from '../../components/LockBadge'
import { DescriptionInput, IconButton, ItemDescription } from '../../components/ui'
import { useAction } from '../../hooks/useAction'
import { useEditLock } from '../../hooks/useEditLock'
import type { SettingsSectionProps, StatusSet, TaskType } from '../../types'

/** Типы задач: название и набор статусов, из которого задачи типа берут статус. */
function TaskTypesSection({ base, settings, reload, locks }: SettingsSectionProps) {
  const { busy, error, setError, run } = useAction(reload)
  const editLock = useEditLock()
  const [editingId, setEditingId] = useState<string | null>(null)
  const setById = new Map(settings.statusSets.map((s) => [s.id, s]))
  const sets = settings.statusSets

  const create = (name: string, statusSetId: string, description: string) =>
    run(() => api(`${base}/task-types`, { method: 'POST', body: { name, statusSetId, description } }))

  // Правка типа открывается, только если его не правит другой: на время формы он блокируется.
  const startEdit = async (type: TaskType) => {
    const problem = await editLock.lock(`${base}/task-types/${type.id}`)
    if (problem) setError(problem)
    else setEditingId(type.id)
  }

  const stopEdit = () => {
    editLock.unlock()
    setEditingId(null)
  }

  const update = async (type: TaskType, name: string, statusSetId: string, description: string) => {
    const ok = await run(() =>
      api(`${base}/task-types/${type.id}`, { method: 'PATCH', body: { name, statusSetId, description, version: type.version } }),
    )
    if (ok) stopEdit()
  }

  const remove = (type: TaskType) => {
    if (!confirm(`Удалить тип «${type.name}»?`)) return
    run(() => api(`${base}/task-types/${type.id}?version=${encodeURIComponent(type.version)}`, { method: 'DELETE' }))
  }

  return (
    <section className="card">
      <h2 className="card-title">Типы задач</h2>

      {sets.length === 0 ? (
        <p className="muted">Сначала создайте набор статусов на вкладке «Статусы»: тип задачи берёт статусы из набора.</p>
      ) : (
        <>
          {settings.taskTypes.length === 0 && <p className="muted">Типов пока нет. Например: «Задача», «Баг», «Фича».</p>}
          <ul className="item-list">
            {settings.taskTypes.map((type) => {
              const other = locks.byOther('taskType', type.id)
              return (
              <li key={type.id} className="item-row">
                {editingId === type.id ? (
                  <TaskTypeForm
                    sets={sets}
                    initialName={type.name}
                    initialSetId={type.statusSetId}
                    initialDescription={type.description}
                    busy={busy}
                    submitLabel="Сохранить"
                    onSubmit={(name, setId, description) => update(type, name, setId, description)}
                    onCancel={stopEdit}
                  />
                ) : (
                  <>
                    <span className="item-text">
                      <span className="item-name">{type.name}</span>
                      <ItemDescription text={type.description} />
                    </span>
                    <span className="item-meta">{setById.get(type.statusSetId)?.name ?? '—'}</span>
                    {other && <LockBadge lock={other} />}
                    <span className="item-actions">
                      <IconButton title="Изменить" disabled={!!other} onClick={() => startEdit(type)}>
                        <IconEdit />
                      </IconButton>
                      <IconButton title="Удалить" danger disabled={busy || !!other} onClick={() => remove(type)}>
                        <IconTrash />
                      </IconButton>
                    </span>
                  </>
                )}
              </li>
              )
            })}
          </ul>

          {error && <div className="error-message">{error}</div>}

          <TaskTypeForm key={settings.taskTypes.length} sets={sets} initialName="" initialSetId={sets[0].id} initialDescription="" busy={busy} submitLabel="Добавить" onSubmit={create} />
        </>
      )}
    </section>
  )
}

function TaskTypeForm({
  sets,
  initialName,
  initialSetId,
  initialDescription,
  busy,
  submitLabel,
  onSubmit,
  onCancel,
}: {
  sets: StatusSet[]
  initialName: string
  initialSetId: string
  initialDescription: string
  busy: boolean
  submitLabel: string
  onSubmit: (name: string, statusSetId: string, description: string) => void
  onCancel?: () => void
}) {
  const [name, setName] = useState(initialName)
  const [setId, setSetId] = useState(initialSetId)
  const [description, setDescription] = useState(initialDescription)

  const submit = (e: FormEvent) => {
    e.preventDefault()
    onSubmit(name, setId, description)
  }

  return (
    <form className="inline-form inline-form-row" onSubmit={submit} onKeyDown={(e) => e.key === 'Escape' && onCancel?.()}>
      <input className="input" placeholder="Новый тип задачи" value={name} onChange={(e) => setName(e.target.value)} autoFocus={!!onCancel} />
      <select className="input select" value={setId} onChange={(e) => setSetId(e.target.value)} title="Набор статусов">
        {sets.map((s) => (
          <option key={s.id} value={s.id}>
            {s.name}
          </option>
        ))}
      </select>
      <DescriptionInput value={description} onChange={setDescription} placeholder="Описание: когда использовать этот тип (необязательно)" />
      <button className="button" type="submit" disabled={busy || !name.trim()}>
        {submitLabel}
      </button>
      {onCancel && (
        <button className="button button-secondary" type="button" onClick={onCancel}>
          Отмена
        </button>
      )}
    </form>
  )
}

export default TaskTypesSection
