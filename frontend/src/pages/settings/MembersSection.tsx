import { useState, type FormEvent } from 'react'
import { useNavigate } from 'react-router'
import { api, getAll } from '../../api'
import { useSession, type User } from '../../auth'
import Avatar from '../../components/Avatar'
import { IconTrash } from '../../components/Icons'
import { IconButton } from '../../components/ui'
import { useAction } from '../../hooks/useAction'
import { useLoad } from '../../hooks/useLoad'
import { useProjects } from '../../projects'

/**
 * Участники проекта (только сервер): люди и агенты. Проект видят только участники и админы.
 * Добавить можно любого человека и своего агента (админ — любого агента); последнего человека убрать нельзя.
 */
function MembersSection({ base }: { base: string }) {
  const me = useSession()?.user
  const projects = useProjects()
  const navigate = useNavigate()
  const { data, error: loadError, reload } = useLoad(async () => {
    const [members, users] = await Promise.all([getAll<User>(`${base}/members`), getAll<User>('/users')])
    return { members, users }
  }, [base])
  const { busy, error, run } = useAction(reload)
  const [userId, setUserId] = useState('')

  if (!data || !me) return loadError ? <div className="error-message">{loadError}</div> : <p className="muted">Загрузка…</p>

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
      ? 'Выйти из проекта? Вы перестанете его видеть, вернуть вас сможет другой участник или админ.'
      : `Убрать ${member.username} из проекта?`
    if (!confirm(question)) return
    const ok = await run(() => api(`${base}/members/${member.id}`, { method: 'DELETE' }))
    // Админ видит все проекты и без участия; остальные теряют доступ.
    if (ok && self && !me.isAdmin) {
      projects.reload()
      navigate('/', { replace: true })
    }
  }

  return (
    <section className="card">
      <h2 className="card-title">Участники</h2>
      <p className="card-hint">Проект видят только участники и администраторы. Агенты работают в проекте через MCP.</p>

      <ul className="item-list">
        {data.members.map((member) => (
          <li key={member.id} className="item-row">
            <Avatar name={member.username} className={member.kind === 'agent' ? 'member-avatar member-avatar-agent' : 'member-avatar'} />
            <span className="member-info">
              <span className="item-name">
                {member.username}
                {member.id === me.id && <span className="badge">Вы</span>}
                {member.kind === 'agent' && <span className="badge badge-agent">Агент</span>}
                {member.isAdmin && <span className="badge">Админ</span>}
              </span>
              {member.email && <span className="item-meta">{member.email}</span>}
            </span>
            <span className="item-actions">
              <IconButton title={member.id === me.id ? 'Выйти из проекта' : 'Убрать из проекта'} danger disabled={busy} onClick={() => remove(member)}>
                <IconTrash />
              </IconButton>
            </span>
          </li>
        ))}
      </ul>

      {error && <div className="error-message">{error}</div>}

      {candidates.length === 0 ? (
        <p className="muted">Добавить некого: все пользователи уже в проекте. Новых пользователей создаёт администратор.</p>
      ) : (
        <form className="inline-form" onSubmit={add}>
          <select className="input" value={selected} onChange={(e) => setUserId(e.target.value)} aria-label="Пользователь">
            {candidates.map((u) => (
              <option key={u.id} value={u.id}>
                {u.username}
                {u.kind === 'agent' ? ' (агент)' : u.email ? ` — ${u.email}` : ''}
              </option>
            ))}
          </select>
          <button className="button" type="submit" disabled={busy || !selected}>
            Добавить
          </button>
        </form>
      )}
    </section>
  )
}

export default MembersSection
