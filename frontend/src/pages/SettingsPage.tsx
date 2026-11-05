import { NavLink, Navigate, useParams } from 'react-router'
import { useT } from '../i18n'
import { useLoad } from '../hooks/useLoad'
import { useLocks } from '../hooks/useLocks'
import { loadProjectSettings } from '../reads'
import type { StringKey } from '../i18n'
import type { ProjectSettings } from '../types'
import { isDesktop } from '../workspace'
import MembersSection from './settings/MembersSection'
import ProjectSection from './settings/ProjectSection'
import StatusesSection from './settings/StatusesSection'
import StatusSetsSection from './settings/StatusSetsSection'
import TaskTypesSection from './settings/TaskTypesSection'
import '../styles/forms.css'
import '../styles/settings.css'

const tabs: { key: string; label: StringKey }[] = [
  { key: 'statuses', label: 'web.settings.statuses' },
  { key: 'task-types', label: 'web.settings.taskTypes' },
  ...(isDesktop ? [] : [{ key: 'members', label: 'web.settings.members' as const }]),
  { key: 'project', label: 'web.settings.project' },
]

function SettingsPage() {
  const t = useT()
  const { projectId, tab } = useParams()
  const base = `/projects/${projectId}`

  if (!tabs.some((item) => item.key === tab)) return <Navigate to={`${base}/settings/statuses`} replace />

  return (
    <div className="page page-wide">
      <h1 className="page-title">{t('web.settings.title')}</h1>
      <div className="tabs">
        {tabs.map((item) => (
          <NavLink key={item.key} to={`${base}/settings/${item.key}`} className={({ isActive }) => (isActive ? 'tab-button active' : 'tab-button')}>
            {t(item.label)}
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

function WorkflowTab({ projectId, tab }: { projectId: string; tab: string }) {
  const t = useT()
  const base = `/projects/${projectId}`
  const locks = useLocks(projectId)
  const { data, error, reload } = useLoad((): Promise<ProjectSettings> => loadProjectSettings(projectId), [projectId])

  if (!data) return error ? <div className="error-message">{error}</div> : <p className="muted">{t('web.common.loading')}</p>

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
