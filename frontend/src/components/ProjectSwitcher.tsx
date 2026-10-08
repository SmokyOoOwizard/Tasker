import { useEffect, useRef, useState } from 'react'
import { useLocation, useNavigate } from 'react-router'
import { useCurrentProjectId, useProjects } from '../projects'
import { IconCheck, IconChevronDown, IconFolder, IconPlus } from './Icons'
import '../styles/project-switcher.css'

const closeDelayMs = 150

/** Выбор проекта в сайдбаре: список всплывает справа при наведении. */
function ProjectSwitcher() {
  const { data: projects } = useProjects()
  const projectId = useCurrentProjectId()
  const location = useLocation()
  const navigate = useNavigate()
  const [open, setOpen] = useState(false)
  const closeTimer = useRef<number | null>(null)

  const cancelClose = () => {
    if (closeTimer.current !== null) {
      window.clearTimeout(closeTimer.current)
      closeTimer.current = null
    }
  }

  const openNow = () => {
    cancelClose()
    setOpen(true)
  }

  const scheduleClose = () => {
    cancelClose()
    closeTimer.current = window.setTimeout(() => {
      setOpen(false)
      closeTimer.current = null
    }, closeDelayMs)
  }

  useEffect(() => cancelClose, [])

  if (!projects) return null

  const current = projects.find((p) => p.id === projectId)

  const select = (id: string) => {
    setOpen(false)
    // Тот же раздел (задачи, доски, вкладка настроек), но в другом проекте.
    const [, , , section, tab] = location.pathname.split('/')
    const path = section === 'settings' && tab ? `settings/${tab}` : (section ?? 'tasks')
    navigate(`/projects/${id}/${current ? path : 'tasks'}`)
  }

  const create = () => {
    setOpen(false)
    navigate('/new-project')
  }

  return (
    <div
      className="project-switcher"
      onMouseEnter={openNow}
      onMouseLeave={scheduleClose}
      onKeyDown={(e) => {
        if (e.key === 'Escape') setOpen(false)
      }}
    >
      <button
        type="button"
        className="project-switcher-trigger"
        aria-expanded={open}
        title={current?.name ?? 'Проект не выбран'}
        onFocus={openNow}
        onBlur={scheduleClose}
        onClick={projects.length === 0 ? create : openNow}
      >
        <span className="project-switcher-icon">
          <IconFolder />
        </span>
        <span className="project-switcher-name">{current?.name ?? (projects.length ? 'Проект' : 'Нет проекта')}</span>
        <span className="project-switcher-chevron">
          <IconChevronDown />
        </span>
      </button>

      {open && (
        <div className="project-switcher-flyout">
          <div className="project-switcher-flyout-title">Проекты</div>
          <ul className="project-switcher-list">
            {projects.map((project) => (
              <li key={project.id}>
                <button
                  type="button"
                  className={project.id === projectId ? 'project-switcher-option active' : 'project-switcher-option'}
                  onClick={() => select(project.id)}
                  onFocus={openNow}
                  onBlur={scheduleClose}
                >
                  <span className="project-switcher-option-name">{project.name}</span>
                  {project.id === projectId && (
                    <span className="project-switcher-option-check">
                      <IconCheck />
                    </span>
                  )}
                </button>
              </li>
            ))}
          </ul>
          <button type="button" className="project-switcher-create" onClick={create} onFocus={openNow} onBlur={scheduleClose}>
            <IconPlus />
            Новый проект
          </button>
        </div>
      )}
    </div>
  )
}

export default ProjectSwitcher
