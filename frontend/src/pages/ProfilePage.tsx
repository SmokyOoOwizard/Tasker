import { useState, type FormEvent } from 'react'
import { NavLink, Navigate, useParams } from 'react-router'
import { api, ApiError } from '../api'
import { signIn, updateSessionUser, useSession, type User } from '../auth'
import Avatar from '../components/Avatar'
import { isDesktop, workspaceBase } from '../workspace'
import AgentsSection from './profile/AgentsSection'
import '../styles/forms.css'
import '../styles/settings.css'

/**
 * Настройки пользователя. Вкладки: /profile — имя, почта и пароль (сервер; на десктопе входа и учётной записи нет),
 * /profile/agents — агенты для MCP.
 */
function ProfilePage() {
  const user = useSession()?.user
  const { tab } = useParams()

  if (tab && tab !== 'agents') return <Navigate to="/profile" replace />

  return (
    <div className="page">
      <h1 className="page-title">Настройки пользователя</h1>
      <div className="tabs">
        <NavLink to="/profile" end className={({ isActive }) => (isActive ? 'tab-button active' : 'tab-button')}>
          Профиль
        </NavLink>
        <NavLink to="/profile/agents" className={({ isActive }) => (isActive ? 'tab-button active' : 'tab-button')}>
          Агенты
        </NavLink>
      </div>
      {tab === 'agents' ? <AgentsSection /> : isDesktop || !user ? <LocalProfile /> : <ServerProfile user={user} />}
    </div>
  )
}

function LocalProfile() {
  const folder = decodeURIComponent(workspaceBase.replace(/^\/w\//, ''))
  return (
    <>
      <div className="card profile-header">
        <Avatar className="profile-avatar" />
        <div>
          <div className="profile-name">Локальный режим</div>
          <div className="profile-meta">Папка: {folder}</div>
        </div>
      </div>
      <div className="card">
        <p>
          В десктопном приложении нет входа и учётных записей: всё, что вы делаете в интерфейсе, делается
          на этом компьютере, а агенты через MCP работают от имени пользователя «agent» этой папки.
        </p>
      </div>
    </>
  )
}

function ServerProfile({ user }: { user: User }) {
  return (
    <>
      <div className="card profile-header">
        <Avatar name={user.username} className="profile-avatar" />
        <div>
          <div className="profile-name">
            {user.username}
            {user.isAdmin && <span className="badge">Администратор</span>}
          </div>
          <div className="profile-meta">
            {user.email} · в Tasker с {new Date(user.createdAt).toLocaleDateString('ru-RU', { dateStyle: 'long' })}
          </div>
        </div>
      </div>
      {/* key: после сохранения (или изменения в другой вкладке) форма берёт свежие значения. */}
      <AccountForm key={user.version} user={user} />
      <PasswordForm user={user} />
    </>
  )
}

type Status = { kind: 'error' | 'success'; text: string } | null

function StatusMessage({ status }: { status: Status }) {
  if (!status) return null
  return <div className={status.kind === 'error' ? 'error-message' : 'success-message'}>{status.text}</div>
}

function AccountForm({ user }: { user: User }) {
  const [username, setUsername] = useState(user.username)
  const [email, setEmail] = useState(user.email ?? '')
  const [status, setStatus] = useState<Status>(null)
  const [busy, setBusy] = useState(false)

  const changed = username !== user.username || email !== (user.email ?? '')

  const save = async (e: FormEvent) => {
    e.preventDefault()
    setBusy(true)
    setStatus(null)
    try {
      const updated = await api<User>(`/users/${user.id}`, {
        method: 'PATCH',
        body: { username, email, version: user.version },
      })
      updateSessionUser(updated)
    } catch (err) {
      if (err instanceof ApiError && err.code === 'modified') {
        // Профиль уже изменили в другом месте: показываем актуальные данные, правку нужно повторить.
        updateSessionUser(await api<User>('/auth/me'))
        return
      }
      setStatus({ kind: 'error', text: err instanceof Error ? err.message : String(err) })
    } finally {
      setBusy(false)
    }
  }

  return (
    <form className="card form" onSubmit={save}>
      <h2 className="card-title">Профиль</h2>
      <div className="form-group">
        <label htmlFor="profile-username">Имя пользователя</label>
        <input id="profile-username" value={username} onChange={(e) => setUsername(e.target.value)} required />
      </div>
      <div className="form-group">
        <label htmlFor="profile-email">Почта</label>
        <input id="profile-email" type="email" value={email} onChange={(e) => setEmail(e.target.value)} required />
      </div>
      <StatusMessage status={status} />
      <button className="button" type="submit" disabled={busy || !changed}>
        Сохранить
      </button>
    </form>
  )
}

function PasswordForm({ user }: { user: User }) {
  const [status, setStatus] = useState<Status>(null)
  const [busy, setBusy] = useState(false)

  const save = async (e: FormEvent<HTMLFormElement>) => {
    e.preventDefault()
    const form = e.currentTarget
    const data = new FormData(form)
    const currentPassword = String(data.get('currentPassword'))
    const newPassword = String(data.get('newPassword'))
    if (newPassword !== data.get('repeatPassword')) {
      setStatus({ kind: 'error', text: 'Новые пароли не совпадают' })
      return
    }

    setBusy(true)
    setStatus(null)
    try {
      await api(`/users/${user.id}/password`, { method: 'POST', body: { currentPassword, newPassword } })
      // Смена пароля завершает все сессии, и эту тоже, — сразу входим с новым паролем.
      await signIn(user.username, newPassword)
      form.reset()
      setStatus({ kind: 'success', text: 'Пароль изменён. Остальные сессии завершены.' })
    } catch (err) {
      setStatus({ kind: 'error', text: err instanceof Error ? err.message : String(err) })
    } finally {
      setBusy(false)
    }
  }

  return (
    <form className="card form" onSubmit={save}>
      <h2 className="card-title">Пароль</h2>
      <input type="text" name="username" autoComplete="username" value={user.username} readOnly hidden />
      <div className="form-group">
        <label htmlFor="current-password">Текущий пароль</label>
        <input id="current-password" name="currentPassword" type="password" autoComplete="current-password" required />
      </div>
      <div className="form-group">
        <label htmlFor="new-password">Новый пароль</label>
        <input id="new-password" name="newPassword" type="password" autoComplete="new-password" minLength={8} required />
        <span className="form-hint">Не короче 8 символов. После смены пароля другие устройства выйдут из Tasker.</span>
      </div>
      <div className="form-group">
        <label htmlFor="repeat-password">Повторите новый пароль</label>
        <input id="repeat-password" name="repeatPassword" type="password" autoComplete="new-password" minLength={8} required />
      </div>
      <StatusMessage status={status} />
      <button className="button" type="submit" disabled={busy}>
        Сменить пароль
      </button>
    </form>
  )
}

export default ProfilePage
