import { NavLink, Navigate, useParams } from 'react-router'
import { useLoad } from '../hooks/useLoad'
import { useLocks } from '../hooks/useLocks'
import { loadProjectSettings } from '../reads'
import type { ProjectSettings } from '../types'
import { isDesktop } from '../workspace'
import MembersSection from './settings/MembersSection'
import ProjectSection from './settings/ProjectSection'
import StatusesSection from './settings/StatusesSection'
import StatusSetsSection from './settings/StatusSetsSection'
import TaskTypesSection from './settings/TaskTypesSection'
import '../styles/forms.css'
import '../styles/settings.css'

// Участников нет на десктопе: там все видят все проекты папки.
const tabs = [
  { key: 'statuses', label: 'Статусы' },
  { key: 'task-types', label: 'Типы задач' },
  ...(isDesktop ? [] : [{ key: 'members', label: 'Участники' }]),
  { key: 'project', label: 'Проект' },
]

/** Настройки проекта. Вкладка — в адресе: /settings/statuses, /settings/members и т.д. */
function SettingsPage() {
  const { projectId, tab } = useParams()
  const base = `/projects/${projectId}`

  if (!tabs.some((t) => t.key === tab)) return <Navigate to={`${base}/settings/statuses`} replace />

  return (
    <div className="page page-wide">
      <h1 className="page-title">Настройки проекта</h1>
      <div className="tabs">
        {tabs.map((t) => (
          <NavLink key={t.key} to={`${base}/settings/${t.key}`} className={({ isActive }) => (isActive ? 'tab-button active' : 'tab-button')}>
            {t.label}
          </NavLink>
        ))}
      </div>

      {tab === 'members' ? (
        <MembersSection projectId={projectId!} />
      ) : tab === 'project' ? (
        <ProjectSection projectId={projectId!} />
      ) : (
        <WorkflowTab projectId={projectId!} tab={tab!} />
      )}
    </div>
  )
}

/** Статусы, наборы и типы задач. */
function WorkflowTab({ projectId, tab }: { projectId: string; tab: string }) {
  const base = `/projects/${projectId}`
  // Всё сразу: наборы показывают названия статусов, типы — названия наборов, и после любой правки обновляется всё.
  const locks = useLocks(projectId)
  const { data, error, reload } = useLoad((): Promise<ProjectSettings> => loadProjectSettings(projectId), [projectId])

  if (!data) return error ? <div className="error-message">{error}</div> : <p className="muted">Загрузка…</p>

  return tab === 'task-types' ? (
    <TaskTypesSection base={base} settings={data} reload={reload} locks={locks} />
  ) : (
    <>
      <StatusesSection base={base} settings={data} reload={reload} locks={locks} />
      <StatusSetsSection base={base} settings={data} reload={reload} locks={locks} />
    </>
  )
}

export default SettingsPage
