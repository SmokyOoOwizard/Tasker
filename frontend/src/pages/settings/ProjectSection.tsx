import { useEffect, useState, type FormEvent } from 'react'
import { useNavigate } from 'react-router'
import { api } from '../../api'
import { LockBadge } from '../../components/LockBadge'
import UsagesList from '../../components/UsagesList'
import { errorText, errorUsages, isConflict } from '../../errors'
import { useT, type Translate } from '../../i18n'
import { useEditLock } from '../../hooks/useEditLock'
import { useLocks } from '../../hooks/useLocks'
import { useProjects } from '../../projects'
import { loadProjectStats } from '../../reads'
import type { EditLock, Project, ProjectStats } from '../../types'

function ProjectSection({ projectId }: { projectId: string }) {
  const { data: projects } = useProjects()
  const locks = useLocks(projectId)
  const project = projects?.find((p) => p.id === projectId)
  if (!project) return null

  return (
    <>
      <RenameProject key={project.version} project={project} lock={locks.byOther('project', project.id)} />
      <DeleteProject project={project} />
    </>
  )
}

function RenameProject({ project, lock }: { project: Project; lock: EditLock | undefined }) {
  const t = useT()
  const { mutate, reload } = useProjects()
  const editLock = useEditLock()
  const [name, setName] = useState(project.name)
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

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
        {t('web.project.nameLabel')} {lock && <LockBadge lock={lock} />}
      </h2>
      <div className="form-group">
        <input value={name} onChange={(e) => change(e.target.value)} required aria-label={t('web.project.nameLabel')} disabled={!!lock} />
      </div>
      {error && <div className="error-message">{error}</div>}
      <button className="button" type="submit" disabled={busy || !!lock || !name.trim() || name === project.name}>
        {t('web.common.save')}
      </button>
    </form>
  )
}

function lossText(s: ProjectStats, t: Translate) {
  return [
    s.tasks > 0 && t('web.project.lossTasks', s.tasks),
    s.boards > 0 && t('web.project.lossBoards', s.boards),
    s.statuses > 0 && t('web.project.lossStatuses', s.statuses),
    s.statusSets > 0 && t('web.project.lossStatusSets', s.statusSets),
    s.taskTypes > 0 && t('web.project.lossTaskTypes', s.taskTypes),
    s.series > 0 && t('web.project.lossSeries', s.series),
    s.linkTypes > 0 && t('web.project.lossLinkTypes', s.linkTypes),
  ]
    .filter(Boolean)
    .join(', ')
}

function DeleteProject({ project }: { project: Project }) {
  const t = useT()
  const { mutate, reload } = useProjects()
  const navigate = useNavigate()
  const [confirmation, setConfirmation] = useState('')
  const [failure, setFailure] = useState<unknown>(null)
  const [busy, setBusy] = useState(false)
  const [stats, setStats] = useState<ProjectStats | null>(null)

  useEffect(() => {
    let current = true
    loadProjectStats(project.id)
      .then((s) => current && setStats(s))
      .catch(() => current && setStats(null))
    return () => {
      current = false
    }
  }, [project.id])

  const remove = async (e: FormEvent) => {
    e.preventDefault()
    setBusy(true)
    setFailure(null)
    try {
      await api(`/projects/${project.id}?version=${encodeURIComponent(project.version)}`, { method: 'DELETE' })
      mutate((list) => list.filter((p) => p.id !== project.id))
      navigate('/', { replace: true })
    } catch (err) {
      setFailure(err)
      if (isConflict(err)) reload()
      setBusy(false)
    }
  }

  return (
    <form className="card form danger-zone" onSubmit={remove}>
      <h2 className="card-title">{t('web.project.deleteTitle')}</h2>
      <p>
        {t('web.project.deleteLead', stats ? lossText(stats, t) || t('web.project.empty') : t('web.project.fallbackLoss'))}{' '}
        {t('web.project.deleteConfirm')} <strong>{project.name}</strong>
      </p>
      <div className="form-group">
        <input value={confirmation} onChange={(e) => setConfirmation(e.target.value)} aria-label={t('web.project.confirmLabel')} />
      </div>
      {failure !== null && <div className="error-message">{errorText(failure)}</div>}
      <UsagesList usages={errorUsages(failure)} />
      <button className="button button-danger" type="submit" disabled={busy || confirmation !== project.name}>
        {t('web.project.delete')}
      </button>
    </form>
  )
}

export default ProjectSection
