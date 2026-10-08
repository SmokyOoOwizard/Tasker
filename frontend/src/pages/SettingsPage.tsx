import { NavLink, Navigate, useParams } from 'react-router'
import { getAll } from '../api'
import { useLoad } from '../hooks/useLoad'
import { useLocks } from '../hooks/useLocks'
import type { ProjectSettings, Status, StatusSet, TaskType } from '../types'
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
        <MembersSection base={base} />
      ) : tab === 'project' ? (
        <ProjectSection projectId={projectId!} />
      ) : (
        <WorkflowTab base={base} tab={tab!} />
      )}
    </div>
  )
}

/** Статусы, наборы и типы задач. */
function WorkflowTab({ base, tab }: { base: string; tab: string }) {
  // Всё сразу: наборы показывают названия статусов, типы — названия наборов, и после любой правки обновляется всё.
  const locks = useLocks(base)
  const { data, error, reload } = useLoad(
    async (): Promise<ProjectSettings> => {
      const [statuses, statusSets, taskTypes] = await Promise.all([
        getAll<Status>(`${base}/statuses`),
        getAll<StatusSet>(`${base}/status-sets`),
        getAll<TaskType>(`${base}/task-types`),
      ])
      return { statuses, statusSets, taskTypes }
    },
    [base],
  )

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
