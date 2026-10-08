import { useEffect, useState, type FormEvent } from 'react'
import { useNavigate } from 'react-router'
import { api } from '../../api'
import { LockBadge } from '../../components/LockBadge'
import { errorText, isConflict } from '../../errors'
import { useEditLock } from '../../hooks/useEditLock'
import { useLocks } from '../../hooks/useLocks'
import { useProjects } from '../../projects'
import type { EditLock, Project, ProjectStats } from '../../types'

/** Переименование и удаление проекта. */
function ProjectSection({ projectId }: { projectId: string }) {
  const { data: projects } = useProjects()
  const locks = useLocks(`/projects/${projectId}`)
  const project = projects?.find((p) => p.id === projectId)
  if (!project) return null

  return (
    <>
      {/* key: после переименования (или правки в другом месте) форма берёт новое название. */}
      <RenameProject key={project.version} project={project} lock={locks.byOther('project', project.id)} />
      <DeleteProject project={project} />
    </>
  )
}

function RenameProject({ project, lock }: { project: Project; lock: EditLock | undefined }) {
  const { mutate, reload } = useProjects()
  const editLock = useEditLock()
  const [name, setName] = useState(project.name)
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  // Блокируем проект, как только начали менять название; снимется при сохранении (форма пересоздаётся) и при уходе с экрана.
  const change = async (next: string) => {
    setName(next)
    if (next === project.name) return
    const problem = await editLock.lock(`/projects/${project.id}`)
    if (problem) setError(problem)
  }

  const save = async (e: FormEvent) => {
    e.preventDefault()
    setBusy(true)
    setError(null)
    try {
      const updated = await api<Project>(`/projects/${project.id}`, {
        method: 'PATCH',
        body: { name, version: project.version },
      })
      mutate((list) => list.map((p) => (p.id === updated.id ? updated : p)))
    } catch (err) {
      setError(errorText(err))
      if (isConflict(err)) reload()
      setBusy(false)
    }
  }

  return (
    <form className="card form" onSubmit={save}>
      <h2 className="card-title">
        Название {lock && <LockBadge lock={lock} />}
      </h2>
      <div className="form-group">
        <input value={name} onChange={(e) => change(e.target.value)} required aria-label="Название проекта" disabled={!!lock} />
      </div>
      {error && <div className="error-message">{error}</div>}
      <button className="button" type="submit" disabled={busy || !!lock || !name.trim() || name === project.name}>
        Сохранить
      </button>
    </form>
  )
}

/** Русское число с существительным: 1 задача, 2 задачи, 5 задач. */
function plural(n: number, one: string, few: string, many: string) {
  const last = n % 10
  const word = n % 100 >= 11 && n % 100 <= 14 ? many : last === 1 ? one : last >= 2 && last <= 4 ? few : many
  return `${n} ${word}`
}

/** Что пропадёт вместе с проектом; нулевые количества пропускаются. */
function lossText(s: ProjectStats) {
  return [
    s.tasks > 0 && plural(s.tasks, 'задача', 'задачи', 'задач'),
    s.boards > 0 && plural(s.boards, 'доска', 'доски', 'досок'),
    s.statuses > 0 && plural(s.statuses, 'статус', 'статуса', 'статусов'),
    s.statusSets > 0 && plural(s.statusSets, 'набор статусов', 'набора статусов', 'наборов статусов'),
    s.taskTypes > 0 && plural(s.taskTypes, 'тип задач', 'типа задач', 'типов задач'),
    s.series > 0 && plural(s.series, 'серия', 'серии', 'серий'),
    s.linkTypes > 0 && plural(s.linkTypes, 'тип связи', 'типа связи', 'типов связи'),
  ]
    .filter(Boolean)
    .join(', ')
}

function DeleteProject({ project }: { project: Project }) {
  const { mutate, reload } = useProjects()
  const navigate = useNavigate()
  const [confirmation, setConfirmation] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  // Не загрузилась — блок работает без цифр.
  const [stats, setStats] = useState<ProjectStats | null>(null)

  useEffect(() => {
    let current = true
    api<ProjectStats>(`/projects/${project.id}/stats`)
      .then((s) => current && setStats(s))
      .catch(() => current && setStats(null))
    return () => {
      current = false
    }
  }, [project.id])

  const remove = async (e: FormEvent) => {
    e.preventDefault()
    setBusy(true)
    setError(null)
    try {
      await api(`/projects/${project.id}?version=${encodeURIComponent(project.version)}`, { method: 'DELETE' })
      mutate((list) => list.filter((p) => p.id !== project.id))
      navigate('/', { replace: true })
    } catch (err) {
      setError(errorText(err))
      if (isConflict(err)) reload()
      setBusy(false)
    }
  }

  return (
    <form className="card form danger-zone" onSubmit={remove}>
      <h2 className="card-title">Удаление проекта</h2>
      <p>
        Удаляется всё, что в проекте: {stats ? lossText(stats) || 'проект пуст' : 'задачи, доски, статусы и типы задач'}. Отменить нельзя.
        Чтобы подтвердить, введите название проекта: <strong>{project.name}</strong>
      </p>
      <div className="form-group">
        <input value={confirmation} onChange={(e) => setConfirmation(e.target.value)} aria-label="Название проекта для подтверждения" />
      </div>
      {error && <div className="error-message">{error}</div>}
      <button className="button button-danger" type="submit" disabled={busy || confirmation !== project.name}>
        Удалить проект
      </button>
    </form>
  )
}

export default ProjectSection
