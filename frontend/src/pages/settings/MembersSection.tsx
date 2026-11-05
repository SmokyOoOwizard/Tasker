import { useState, type FormEvent } from 'react'
import { useNavigate } from 'react-router'
import { api } from '../../api'
import { useSession, type User } from '../../auth'
import Avatar from '../../components/Avatar'
import { IconTrash } from '../../components/Icons'
import { IconButton } from '../../components/ui'
import { useT } from '../../i18n'
import { useAction } from '../../hooks/useAction'
import { useLoad } from '../../hooks/useLoad'
import { useProjects } from '../../projects'
import { loadMembers, loadUsers } from '../../reads'

function MembersSection({ projectId }: { projectId: string }) {
  const t = useT()
  const base = `/projects/${projectId}`
  const me = useSession()?.user
  const projects = useProjects()
  const navigate = useNavigate()
  const { data, error: loadError, reload } = useLoad(async () => {
    const [members, users] = await Promise.all([loadMembers(projectId), loadUsers()])
    return { members, users }
  }, [projectId])
  const { busy, error, run } = useAction(reload)
  const [userId, setUserId] = useState('')

  if (!data || !me) return loadError ? <div className="error-message">{loadError}</div> : <p className="muted">{t('web.common.loading')}</p>

  const memberIds = new Set(data.members.map((m) => m.id))
  const candidates = data.users.filter(
    (u) => !memberIds.has(u.id) && (u.kind === 'human' || me.isAdmin || u.ownerId === me.id),
  )
  const selected = candidates.some((u) => u.id === userId) ? userId : (candidates[0]?.id ?? '')

  const add = async (e: FormEvent) => {
    e.preventDefault()
    await run(() => api(`${base}/members`, { method: 'POST', body: { userId: selected } }))
  }

  const remove = async (member: User) => {
    const self = member.id === me.id
    const question = self
      ? t('web.members.confirmLeave')
      : t('web.members.confirmRemove', member.username)
    if (!confirm(question)) return
    const ok = await run(() => api(`${base}/members/${member.id}`, { method: 'DELETE' }))
    if (ok && self && !me.isAdmin) {
      projects.reload()
      navigate('/', { replace: true })
    }
  }

  return (
    <section className="card">
      <h2 className="card-title">{t('web.settings.members')}</h2>
      <p className="card-hint">{t('web.members.hint')}</p>

      <ul className="item-list">
        {data.members.map((member) => (
          <li key={member.id} className="item-row">
            <Avatar name={member.username} className={member.kind === 'agent' ? 'member-avatar member-avatar-agent' : 'member-avatar'} />
            <span className="member-info">
              <span className="item-name">
                {member.username}
                {member.id === me.id && <span className="badge">{t('web.members.you')}</span>}
                {member.kind === 'agent' && <span className="badge badge-agent">{t('web.members.agent')}</span>}
                {member.isAdmin && <span className="badge">{t('web.members.admin')}</span>}
              </span>
              {member.email && <span className="item-meta">{member.email}</span>}
            </span>
            <span className="item-actions">
              <IconButton title={member.id === me.id ? t('web.members.leave') : t('web.members.remove')} danger disabled={busy} onClick={() => remove(member)}>
                <IconTrash />
              </IconButton>
            </span>
          </li>
        ))}
      </ul>

      {error && <div className="error-message">{error}</div>}

      {candidates.length === 0 ? (
        <p className="muted">{t('web.members.nobody')}</p>
      ) : (
        <form className="inline-form" onSubmit={add}>
          <select className="input" value={selected} onChange={(e) => setUserId(e.target.value)} aria-label={t('web.members.user')}>
            {candidates.map((u) => (
              <option key={u.id} value={u.id}>
                {u.username}
                {u.kind === 'agent' ? ` ${t('web.members.agentSuffix')}` : u.email ? ` — ${u.email}` : ''}
              </option>
            ))}
          </select>
          <button className="button" type="submit" disabled={busy || !selected}>
            {t('web.common.add')}
          </button>
        </form>
      )}
    </section>
  )
}

export default MembersSection
