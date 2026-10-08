import { useState, type FormEvent } from 'react'
import { api, getAll } from '../../api'
import { useSession, type User } from '../../auth'
import Avatar from '../../components/Avatar'
import { IconEdit, IconTrash } from '../../components/Icons'
import { IconButton, RenameForm } from '../../components/ui'
import { useAction } from '../../hooks/useAction'
import { useLoad } from '../../hooks/useLoad'
import type { AgentToken } from '../../types'
import { isDesktop } from '../../workspace'

/**
 * Десктоп: общий адрес MCP (null — порт MCP был занят), имя этой папки для агента (аргумент workspace инструментов)
 * и заголовок, которым выбирается агент.
 */
type LocalMcp = { url: string | null; workspace: string | null; agentHeader: string }

/**
 * Агенты — пользователи для MCP (например, Claude Code). Сервер: свои агенты (админу — все) и их токены.
 * Десктоп: агенты папки общие, MCP работает без токена.
 */
function AgentsSection() {
  const me = useSession()?.user
  const { data, error: loadError, reload } = useLoad(async () => {
    const [agents, users, mcp] = await Promise.all([
      getAll<User>('/agents'),
      // Админ видит чужих агентов — показываем, чей агент.
      me?.isAdmin ? getAll<User>('/users') : [],
      isDesktop ? api<LocalMcp>('/workspace/mcp') : null,
    ])
    return { agents, users, mcp }
  }, [me?.isAdmin])
  const { busy, error, run } = useAction(reload)
  const [username, setUsername] = useState('')
  // Десктоп: у только что созданного агента сразу открыта команда подключения.
  const [createdId, setCreatedId] = useState<string | null>(null)

  if (!data) return loadError ? <div className="error-message">{loadError}</div> : <p className="muted">Загрузка…</p>

  const userById = new Map(data.users.map((u) => [u.id, u]))

  const create = async (e: FormEvent) => {
    e.preventDefault()
    let created: User | undefined
    if (await run(async () => (created = await api<User>('/agents', { method: 'POST', body: { username } })))) {
      setUsername('')
      setCreatedId(created?.id ?? null)
    }
  }

  return (
    <>
      <div className="card">
        <p>
          {isDesktop
            ? 'Агенты работают с задачами через MCP. На десктопе токены не нужны: MCP доступен только с этого компьютера, а агент выбирается в команде подключения. Без него MCP работает от имени агента «agent» этой папки.'
            : 'Агент работает с задачами через MCP по своему токену. Чтобы агент видел проект, добавьте его в участники проекта: «Настройки» проекта → «Участники».'}
        </p>
      </div>

      {data.mcp && !data.mcp.url && (
        <div className="error-message">MCP недоступен: его порт был занят при запуске Tasker (другой порт — параметр --mcpport).</div>
      )}

      {data.agents.map((agent) => (
        <AgentCard
          key={agent.id}
          agent={agent}
          owner={agent.ownerId && agent.ownerId !== me?.id ? userById.get(agent.ownerId) : undefined}
          mcp={data.mcp}
          connectOpen={agent.id === createdId}
          reload={reload}
        />
      ))}

      <form className="card inline-form" onSubmit={create}>
        <input className="input" placeholder="Имя нового агента, например claude" value={username} onChange={(e) => setUsername(e.target.value)} />
        <button className="button" type="submit" disabled={busy || !username.trim()}>
          Создать агента
        </button>
        {error && <div className="error-message form-row-error">{error}</div>}
      </form>
    </>
  )
}

function AgentCard({
  agent,
  owner,
  mcp,
  connectOpen,
  reload,
}: {
  agent: User
  owner: User | undefined
  mcp: LocalMcp | null
  connectOpen: boolean
  reload: () => void
}) {
  const { busy, error, run } = useAction(reload)
  const [renaming, setRenaming] = useState(false)
  const [connecting, setConnecting] = useState(connectOpen)

  const rename = async (username: string) => {
    if (await run(() => api(`/agents/${agent.id}`, { method: 'PATCH', body: { username, version: agent.version } }))) setRenaming(false)
  }

  const remove = () => {
    if (!confirm(`Удалить агента «${agent.username}»? Его токены перестанут работать, а сам он пропадёт из участников проектов.`)) return
    run(() => api(`/agents/${agent.id}?version=${encodeURIComponent(agent.version)}`, { method: 'DELETE' }))
  }

  return (
    <section className="card agent-card">
      <div className="agent-header">
        <Avatar name={agent.username} className="member-avatar member-avatar-agent" />
        {renaming ? (
          <RenameForm initial={agent.username} busy={busy} onSave={rename} onCancel={() => setRenaming(false)} />
        ) : (
          <>
            <div className="member-info">
              <span className="item-name">{agent.username}</span>
              <span className="item-meta">
                {owner ? `Агент пользователя ${owner.username} · ` : ''}
                создан {formatDate(agent.createdAt)}
              </span>
            </div>
            <span className="item-actions">
              {mcp?.url && (
                <button type="button" className="button button-secondary button-small" onClick={() => setConnecting(!connecting)}>
                  Подключить
                </button>
              )}
              <IconButton title="Переименовать" onClick={() => setRenaming(true)}>
                <IconEdit />
              </IconButton>
              <IconButton title="Удалить агента" danger disabled={busy} onClick={remove}>
                <IconTrash />
              </IconButton>
            </span>
          </>
        )}
      </div>
      {error && <div className="error-message">{error}</div>}
      {mcp?.url && connecting && <LocalConnect agent={agent} mcp={mcp as LocalMcp & { url: string }} onClose={() => setConnecting(false)} />}
      {!isDesktop && <Tokens agent={agent} />}
    </section>
  )
}

const lifetimes = [
  { label: 'Без срока', days: null },
  { label: '30 дней', days: 30 },
  { label: '90 дней', days: 90 },
  { label: '1 год', days: 365 },
]

/** Токены агента: у каждого места использования — свой, чтобы отзывать по отдельности. */
function Tokens({ agent }: { agent: User }) {
  // Время загрузки — чтобы определить истёкшие токены, не вызывая Date.now() при рендере.
  const { data, error: loadError, reload } = useLoad(
    async () => ({ tokens: await getAll<AgentToken>(`/agents/${agent.id}/tokens`), loadedAt: Date.now() }),
    [agent.id],
  )
  const tokens = data?.tokens
  const { busy, error, run } = useAction(reload)
  const [name, setName] = useState('')
  const [lifetime, setLifetime] = useState(0)
  // Значение нового токена: сервер отдаёт его один раз и хранит только хэш.
  const [issued, setIssued] = useState<{ name: string; value: string } | null>(null)

  const create = async (e: FormEvent) => {
    e.preventDefault()
    const days = lifetimes[lifetime].days
    const expiresAt = days ? new Date(Date.now() + days * 86_400_000).toISOString() : null
    let value = ''
    const ok = await run(async () => {
      const result = await api<{ token: string }>(`/agents/${agent.id}/tokens`, { method: 'POST', body: { name, expiresAt } })
      value = result.token
    })
    if (ok) {
      setIssued({ name, value })
      setName('')
    }
  }

  const revoke = (token: AgentToken) => {
    if (!confirm(`Отозвать токен «${token.name}»? Агент с этим токеном сразу потеряет доступ.`)) return
    run(() => api(`/agents/${agent.id}/tokens/${token.id}`, { method: 'DELETE' }))
  }

  const state = (t: AgentToken) =>
    t.revokedAt ? 'отозван' : t.expiresAt && data && Date.parse(t.expiresAt) <= data.loadedAt ? 'истёк' : null

  return (
    <div className="tokens">
      <h3 className="tokens-title">Токены</h3>

      {issued && <IssuedToken name={issued.name} value={issued.value} onClose={() => setIssued(null)} />}

      {loadError && <div className="error-message">{loadError}</div>}
      {tokens?.length === 0 && <p className="muted">Токенов нет — выпустите токен, чтобы подключить агента.</p>}
      {tokens && tokens.length > 0 && (
        <ul className="item-list">
          {tokens.map((token) => (
            <li key={token.id} className={state(token) ? 'item-row token-inactive' : 'item-row'}>
              <span className="member-info">
                <span className="item-name">
                  {token.name}
                  {state(token) && <span className="badge badge-muted">{state(token)}</span>}
                </span>
                <span className="item-meta">
                  <code>{token.prefix}…</code> · выпущен {formatDate(token.createdAt)}
                  {token.expiresAt && ` · действует до ${formatDate(token.expiresAt)}`}
                  {' · '}
                  {token.lastUsedAt ? `использован ${formatDateTime(token.lastUsedAt)}` : 'ещё не использовался'}
                </span>
              </span>
              {!state(token) && (
                <span className="item-actions">
                  <IconButton title="Отозвать токен" danger disabled={busy} onClick={() => revoke(token)}>
                    <IconTrash />
                  </IconButton>
                </span>
              )}
            </li>
          ))}
        </ul>
      )}

      {error && <div className="error-message">{error}</div>}

      <form className="inline-form" onSubmit={create}>
        <input className="input" placeholder="Где используется, например «Claude Code на ноутбуке»" value={name} onChange={(e) => setName(e.target.value)} />
        <select className="input select" value={lifetime} onChange={(e) => setLifetime(Number(e.target.value))} aria-label="Срок действия">
          {lifetimes.map((l, i) => (
            <option key={l.label} value={i}>
              {l.label}
            </option>
          ))}
        </select>
        <button className="button" type="submit" disabled={busy || !name.trim()}>
          Выпустить токен
        </button>
      </form>
    </div>
  )
}

/** Только что выпущенный токен: показывается один раз, с готовой командой подключения. */
function IssuedToken({ name, value, onClose }: { name: string; value: string; onClose: () => void }) {
  const command = `claude mcp add --transport http tasker ${window.location.origin}/mcp --header "Authorization: Bearer ${value}"`

  return (
    <div className="issued-token">
      <p>
        <strong>Токен «{name}» выпущен.</strong> Скопируйте его сейчас: больше он не будет показан.
      </p>
      <CopyField value={value} />
      <p className="muted">Подключение в Claude Code:</p>
      <CopyField value={command} />
      <button type="button" className="button button-secondary" onClick={onClose}>
        Готово, скопировал
      </button>
    </div>
  )
}

/**
 * Десктоп: команда подключения агента к общему MCP. Адрес один на все папки, папку агент указывает аргументом workspace.
 * Секрета в команде нет — её можно показывать сколько угодно.
 */
function LocalConnect({ agent, mcp, onClose }: { agent: User; mcp: { url: string; workspace: string | null; agentHeader: string }; onClose: () => void }) {
  const command = `claude mcp add --transport http tasker ${mcp.url} --header "${mcp.agentHeader}: ${agent.id}"`
  const commonCommand = `claude mcp add --transport http tasker ${mcp.url}`

  return (
    <div className="issued-token">
      <p>
        <strong>Подключение агента «{agent.username}» в Claude Code.</strong> Выполните команду: агент будет работать с задачами
        этой папки от своего имени.
      </p>
      <CopyField value={command} />
      <p className="muted">
        Адрес MCP <code>{mcp.url}</code> один на все папки, открытые в Tasker. Папку агент указывает аргументом{' '}
        <code>workspace</code> инструментов{mcp.workspace && (
          <>
            : для этой — <code>{mcp.workspace}</code>
          </>
        )}
        ; если открыта одна папка, аргумент можно опустить. Список папок — инструмент <code>list_workspaces</code>. Агент в заголовке{' '}
        <code>{mcp.agentHeader}</code> действует только в этой папке; чтобы работать с несколькими папками, подключите агента без заголовка
        (в каждой папке он работает от имени её агента «agent»):
      </p>
      <CopyField value={commonCommand} />
      <p className="muted">MCP работает, пока открыт Tasker.</p>
      <button type="button" className="button button-secondary" onClick={onClose}>
        Готово
      </button>
    </div>
  )
}

function CopyField({ value }: { value: string }) {
  const [copied, setCopied] = useState(false)

  const copy = async () => {
    try {
      await navigator.clipboard.writeText(value)
      setCopied(true)
    } catch {
      setCopied(false)
    }
  }

  return (
    <div className="issued-token-row">
      <code className="issued-token-value">{value}</code>
      <button type="button" className="button button-secondary" onClick={copy}>
        {copied ? 'Скопировано' : 'Копировать'}
      </button>
    </div>
  )
}

function formatDate(value: string) {
  return new Date(value).toLocaleDateString('ru-RU', { dateStyle: 'medium' })
}

function formatDateTime(value: string) {
  return new Date(value).toLocaleString('ru-RU', { dateStyle: 'short', timeStyle: 'short' })
}

export default AgentsSection
