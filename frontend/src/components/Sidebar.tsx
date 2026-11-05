import { useEffect, useRef, useState, type ReactElement } from 'react'
import { NavLink, useNavigate } from 'react-router'
import { signOut, useSession } from '../auth'
import { useT, type Translate } from '../i18n'
import { useCurrentProjectId } from '../projects'
import { isDesktop } from '../workspace'
import Avatar from './Avatar'
import { IconBoards, IconLogout, IconSettings, IconTasks } from './Icons'
import ProjectSwitcher from './ProjectSwitcher'
import '../styles/sidebar.css'

const closeDelayMs = 150

function projectItems(projectId: string, t: Translate): { icon: ReactElement; label: string; path: string }[] {
  return [
    { icon: <IconTasks />, label: t('web.nav.tasks'), path: `/projects/${projectId}/tasks` },
    { icon: <IconBoards />, label: t('web.nav.boards'), path: `/projects/${projectId}/boards` },
    { icon: <IconSettings />, label: t('web.nav.settings'), path: `/projects/${projectId}/settings` },
  ]
}

function Sidebar() {
  const t = useT()
  const user = useSession()?.user
  const navigate = useNavigate()
  const projectId = useCurrentProjectId()
  const items = projectId ? projectItems(projectId, t) : []

  const [logoutOpen, setLogoutOpen] = useState(false)
  const closeTimer = useRef<number | null>(null)

  const cancelClose = () => {
    if (closeTimer.current !== null) {
      window.clearTimeout(closeTimer.current)
      closeTimer.current = null
    }
  }

  const openNow = () => {
    cancelClose()
    setLogoutOpen(true)
  }

  const scheduleClose = () => {
    cancelClose()
    closeTimer.current = window.setTimeout(() => {
      setLogoutOpen(false)
      closeTimer.current = null
    }, closeDelayMs)
  }

  useEffect(() => cancelClose, [])

  const handleLogout = async () => {
    navigate('/', { replace: true })
    await signOut()
  }

  const canLogout = !isDesktop && !!user

  return (
    <aside className="sidebar">
      <div className="sidebar-header">
        <div className="sidebar-logo-mark">T</div>
      </div>
      <ProjectSwitcher />
      <nav className="sidebar-nav">
        {items.map((item) => (
          <NavLink key={item.path} to={item.path} className={({ isActive }) => (isActive ? 'sidebar-item active' : 'sidebar-item')}>
            <span className="sidebar-icon">{item.icon}</span>
            <span className="sidebar-label">{item.label}</span>
          </NavLink>
        ))}
      </nav>
      <div
        className="sidebar-user"
        onMouseEnter={canLogout ? openNow : undefined}
        onMouseLeave={canLogout ? scheduleClose : undefined}
      >
        <NavLink
          to="/profile"
          className={({ isActive }) => (isActive ? 'sidebar-user-link active' : 'sidebar-user-link')}
          onFocus={canLogout ? openNow : undefined}
          onBlur={canLogout ? scheduleClose : undefined}
        >
          <Avatar name={user?.username} className="sidebar-user-avatar" />
          <div className="sidebar-user-name" title={user?.username}>
            {user?.username ?? t('web.nav.profile')}
          </div>
        </NavLink>

        {canLogout && (
          <div className={logoutOpen ? 'sidebar-logout-flyout open' : 'sidebar-logout-flyout'}>
            <button type="button" className="sidebar-logout-btn" onClick={handleLogout} onFocus={openNow} onBlur={scheduleClose}>
              <IconLogout />
              <span>{t('web.nav.logout')}</span>
            </button>
          </div>
        )}
      </div>
    </aside>
  )
}

export default Sidebar
