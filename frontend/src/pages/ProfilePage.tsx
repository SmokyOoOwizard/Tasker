import { useState, type FormEvent } from 'react'
import { NavLink, Navigate, useParams } from 'react-router'
import { api } from '../api'
import { signIn, updateSessionUser, useSession, type User } from '../auth'
import Avatar from '../components/Avatar'
import LanguageSelect from '../components/LanguageSelect'
import { ApiError, errorText } from '../errors'
import { useLocale, useT } from '../i18n'
import { loadMe } from '../reads'
import { isDesktop, workspaceBase } from '../workspace'
import AgentsSection from './profile/AgentsSection'
import '../styles/forms.css'
import '../styles/settings.css'

function ProfilePage() {
  const t = useT()
  const user = useSession()?.user
  const { tab } = useParams()

  if (tab && tab !== 'agents') return <Navigate to="/profile" replace />

  return (
    <div className="page">
      <h1 className="page-title">{t('web.profile.title')}</h1>
      <div className="tabs">
        <NavLink to="/profile" end className={({ isActive }) => (isActive ? 'tab-button active' : 'tab-button')}>
          {t('web.profile.tabProfile')}
        </NavLink>
        <NavLink to="/profile/agents" className={({ isActive }) => (isActive ? 'tab-button active' : 'tab-button')}>
          {t('web.profile.tabAgents')}
        </NavLink>
      </div>
      {tab === 'agents' ? (
        <AgentsSection />
      ) : (
        <>
          {isDesktop || !user ? <LocalProfile /> : <ServerProfile user={user} />}
          <LanguageCard />
        </>
      )}
    </div>
  )
}

function LanguageCard() {
  const t = useT()
  return (
    <div className="card form">
      <h2 className="card-title">{t('web.language.title')}</h2>
      <LanguageSelect id="profile-language" />
    </div>
  )
}

function LocalProfile() {
  const t = useT()
  const folder = decodeURIComponent(workspaceBase.replace(/^\/w\//, ''))
  return (
    <>
      <div className="card profile-header">
        <Avatar className="profile-avatar" />
        <div>
          <div className="profile-name">{t('web.profile.localMode')}</div>
          <div className="profile-meta">{t('web.profile.folder', folder)}</div>
        </div>
      </div>
      <div className="card">
        <p>
          {t('web.profile.localInfo')}
        </p>
      </div>
    </>
  )
}

function ServerProfile({ user }: { user: User }) {
  const t = useT()
  const locale = useLocale()
  return (
    <>
      <div className="card profile-header">
        <Avatar name={user.username} className="profile-avatar" />
        <div>
          <div className="profile-name">
            {user.username}
            {user.isAdmin && <span className="badge">{t('web.profile.admin')}</span>}
          </div>
          <div className="profile-meta">
            {t('web.profile.since', user.email ?? '', new Date(user.createdAt).toLocaleDateString(locale, { dateStyle: 'long' }))}
          </div>
        </div>
      </div>
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
  const t = useT()
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
      if (err instanceof ApiError && err.code === 'entity.modified') {
        updateSessionUser(await loadMe())
        return
      }
      setStatus({ kind: 'error', text: errorText(err) })
    } finally {
      setBusy(false)
    }
  }

  return (
    <form className="card form" onSubmit={save}>
      <h2 className="card-title">{t('web.profile.tabProfile')}</h2>
      <div className="form-group">
        <label htmlFor="profile-username">{t('web.common.username')}</label>
        <input id="profile-username" value={username} onChange={(e) => setUsername(e.target.value)} required />
      </div>
      <div className="form-group">
        <label htmlFor="profile-email">{t('web.common.email')}</label>
        <input id="profile-email" type="email" value={email} onChange={(e) => setEmail(e.target.value)} required />
      </div>
      <StatusMessage status={status} />
      <button className="button" type="submit" disabled={busy || !changed}>
        {t('web.common.save')}
      </button>
    </form>
  )
}

function PasswordForm({ user }: { user: User }) {
  const t = useT()
  const [status, setStatus] = useState<Status>(null)
  const [busy, setBusy] = useState(false)

  const save = async (e: FormEvent<HTMLFormElement>) => {
    e.preventDefault()
    const form = e.currentTarget
    const data = new FormData(form)
    const currentPassword = String(data.get('currentPassword'))
    const newPassword = String(data.get('newPassword'))
    if (newPassword !== data.get('repeatPassword')) {
      setStatus({ kind: 'error', text: t('web.profile.passwordMismatch') })
      return
    }

    setBusy(true)
    setStatus(null)
    try {
      await api(`/users/${user.id}/password`, { method: 'POST', body: { currentPassword, newPassword } })
      await signIn(user.username, newPassword)
      form.reset()
      setStatus({ kind: 'success', text: t('web.profile.passwordChanged') })
    } catch (err) {
      setStatus({ kind: 'error', text: errorText(err) })
    } finally {
      setBusy(false)
    }
  }

  return (
    <form className="card form" onSubmit={save}>
      <h2 className="card-title">{t('web.common.password')}</h2>
      <input type="text" name="username" autoComplete="username" value={user.username} readOnly hidden />
      <div className="form-group">
        <label htmlFor="current-password">{t('web.profile.currentPassword')}</label>
        <input id="current-password" name="currentPassword" type="password" autoComplete="current-password" required />
      </div>
      <div className="form-group">
        <label htmlFor="new-password">{t('web.profile.newPassword')}</label>
        <input id="new-password" name="newPassword" type="password" autoComplete="new-password" minLength={8} required />
        <span className="form-hint">{t('web.profile.passwordHint')}</span>
      </div>
      <div className="form-group">
        <label htmlFor="repeat-password">{t('web.profile.repeatPassword')}</label>
        <input id="repeat-password" name="repeatPassword" type="password" autoComplete="new-password" minLength={8} required />
      </div>
      <StatusMessage status={status} />
      <button className="button" type="submit" disabled={busy}>
        {t('web.profile.changePassword')}
      </button>
    </form>
  )
}

export default ProfilePage
