import { useState, type FormEvent } from 'react'
import { Link, useParams } from 'react-router'
import { api, getAll } from '../api'
import { LockBadge } from '../components/LockBadge'
import { StatusChip } from '../components/ui'
import { useAction } from '../hooks/useAction'
import { useLoad } from '../hooks/useLoad'
import { useLocks } from '../hooks/useLocks'
import type { Page, Status, StatusSet, Task, TaskType } from '../types'
import '../styles/forms.css'
import '../styles/settings.css'

const pageSize = 50

/** Все задачи проекта: список страницами, создание и смена статуса. */
function TasksPage() {
  const { projectId } = useParams()
  const base = `/projects/${projectId}`
  const [shown, setShown] = useState(pageSize)
  const locks = useLocks(base)

  const settings = useLoad(async () => {
    const [statuses, statusSets, taskTypes] = await Promise.all([
      getAll<Status>(`${base}/statuses`),
      getAll<StatusSet>(`${base}/status-sets`),
      getAll<TaskType>(`${base}/task-types`),
    ])
    return { statuses, statusSets, taskTypes }
  }, [base])

  // Первые `shown` задач: при «Показать ещё» и перезагрузке список перечитывается целиком, чтобы не было дыр и повторов.
  const tasks = useLoad(async () => {
    const items: Task[] = []
    let total = 0
    do {
      const page = await api<Page<Task>>(`${base}/tasks?offset=${items.length}&limit=${Math.min(200, shown - items.length)}`)
      items.push(...page.data)
      total = page.totalCount
      if (page.data.length === 0) break
    } while (items.length < Math.min(shown, total))
    return { items, total }
  }, [base, shown])

  const reload = () => {
    tasks.reload()
    settings.reload()
  }
  const { busy, error, run } = useAction(reload)

  const data = settings.data
  const loadError = settings.error ?? tasks.error

  if (!data || !tasks.data) {
    return (
      <div className="page page-wide">
        <h1 className="page-title">Задачи</h1>
        {loadError ? <div className="error-message">{loadError}</div> : <p className="muted">Загрузка…</p>}
      </div>
    )
  }

  const typeById = new Map(data.taskTypes.map((t) => [t.id, t]))
  const setById = new Map(data.statusSets.map((s) => [s.id, s]))
  const statusById = new Map(data.statuses.map((s) => [s.id, s]))
  const statusesOf = (typeId: string) =>
    (setById.get(typeById.get(typeId)?.statusSetId ?? '')?.statusIds ?? []).flatMap((id) => statusById.get(id) ?? [])

  const create = (title: string, typeId: string) => run(() => api(`${base}/tasks`, { method: 'POST', body: { title, typeId } }))

  const changeStatus = (task: Task, statusId: string) =>
    run(() => api(`${base}/tasks/${task.id}`, { method: 'PATCH', body: { statusId, version: task.version } }))

  return (
    <div className="page page-wide">
      <div className="page-header">
        <h1 className="page-title">Задачи</h1>
        <span className="muted">{tasks.data.total}</span>
      </div>

      {data.taskTypes.length === 0 ? (
        <div className="card">
          <p>
            Чтобы создавать задачи, настройте проект: статусы, наборы статусов и типы задач —{' '}
            <Link to={`${base}/settings`}>в настройках</Link>.
          </p>
        </div>
      ) : (
        <NewTaskForm key={tasks.data.total} types={data.taskTypes} busy={busy} onCreate={create} />
      )}

      {error && <div className="error-message">{error}</div>}

      {tasks.data.items.length > 0 && (
        <div className="card card-flush">
          <table className="table">
            <thead>
              <tr>
                <th>Задача</th>
                <th>Тип</th>
                <th>Статус</th>
                <th>Изменена</th>
              </tr>
            </thead>
            <tbody>
              {tasks.data.items.map((task) => {
                const other = locks.byOther('task', task.id)
                return (
                <tr key={task.id}>
                  <td className="table-title">
                    {task.title} {other && <LockBadge lock={other} />}
                  </td>
                  <td>{typeById.get(task.typeId)?.name ?? '—'}</td>
                  <td>
                    <StatusSelect
                      value={task.statusId}
                      statuses={statusesOf(task.typeId)}
                      current={statusById.get(task.statusId)}
                      disabled={busy || !!other}
                      onChange={(statusId) => changeStatus(task, statusId)}
                    />
                  </td>
                  <td className="muted">{new Date(task.updatedAt).toLocaleString('ru-RU', { dateStyle: 'short', timeStyle: 'short' })}</td>
                </tr>
                )
              })}
            </tbody>
          </table>
        </div>
      )}

      {tasks.data.items.length < tasks.data.total && (
        <button type="button" className="button button-secondary" onClick={() => setShown(shown + pageSize)}>
          Показать ещё
        </button>
      )}
    </div>
  )
}

function NewTaskForm({ types, busy, onCreate }: { types: TaskType[]; busy: boolean; onCreate: (title: string, typeId: string) => void }) {
  const [title, setTitle] = useState('')
  const [typeId, setTypeId] = useState(types[0].id)

  const submit = (e: FormEvent) => {
    e.preventDefault()
    onCreate(title, typeId)
  }

  return (
    <form className="card inline-form inline-form-row" onSubmit={submit}>
      <input className="input" placeholder="Новая задача" value={title} onChange={(e) => setTitle(e.target.value)} />
      <select className="input select" value={typeId} onChange={(e) => setTypeId(e.target.value)} title="Тип задачи">
        {types.map((t) => (
          <option key={t.id} value={t.id}>
            {t.name}
          </option>
        ))}
      </select>
      <button className="button" type="submit" disabled={busy || !title.trim()}>
        Создать
      </button>
    </form>
  )
}

/** Статус задачи с выбором из набора её типа. */
function StatusSelect({
  value,
  statuses,
  current,
  disabled,
  onChange,
}: {
  value: string
  statuses: Status[]
  current: Status | undefined
  disabled: boolean
  onChange: (statusId: string) => void
}) {
  return (
    <label className="status-select">
      <StatusChip status={current} />
      <select value={value} disabled={disabled} onChange={(e) => onChange(e.target.value)} aria-label="Статус">
        {statuses.map((s) => (
          <option key={s.id} value={s.id}>
            {s.name}
          </option>
        ))}
      </select>
    </label>
  )
}

export default TasksPage
