import { useState, type FormEvent } from 'react'
import { api } from '../../api'
import { useSession, type User } from '../../auth'
import Avatar from '../../components/Avatar'
import { IconEdit, IconTrash } from '../../components/Icons'
import RichText from '../../components/RichText'
import { IconButton, RenameForm } from '../../components/ui'
import { useAction } from '../../hooks/useAction'
import { useLocale, useT, type StringKey, type Translate } from '../../i18n'
import { useLoad } from '../../hooks/useLoad'
import { loadAgents, loadAgentTokens, loadUsers, loadWorkspaceMcp, type LocalMcp } from '../../reads'
import type { AgentToken } from '../../types'
import { isDesktop } from '../../workspace'

function daemonState(mcp: LocalMcp, t: Translate): { text: string; error: boolean } {
  const reason = mcp.error ?? t('web.agents.reasonInLog')
  switch (mcp.daemon) {
    case 'starting':
      return { text: t('web.agents.daemonStarting'), error: false }
    case 'failed':
      return { text: t('web.agents.daemonFailed', reason), error: true }
    case 'stopped':
      return { text: t('web.agents.daemonStopped'), error: true }
  }
  if (mcp.workspaceState === 'open') return { text: t('web.agents.daemonServing', mcp.port ?? ''), error: false }
  if (mcp.workspaceState === 'opening') return { text: t('web.agents.workspaceOpening'), error: false }
  if (mcp.workspaceState === 'failed') return { text: t('web.agents.workspaceFailed', reason), error: true }
  return { text: t('web.agents.workspaceMissing'), error: true }
}

function AgentsSection() {
  const t = useT()
  const me = useSession()?.user
  const { data, error: loadError, reload } = useLoad(async () => {
    const [agents, users, mcp] = await Promise.all([
      loadAgents(),
      me?.isAdmin ? loadUsers() : [],
      isDesktop ? loadWorkspaceMcp() : null,
    ])
    return { agents, users, mcp }
  }, [me?.isAdmin])
  const { busy, error, run } = useAction(reload)
  const [username, setUsername] = useState('')
  const [createdId, setCreatedId] = useState<string | null>(null)

  if (!data) return loadError ? <div className="error-message">{loadError}</div> : <p className="muted">{t('web.common.loading')}</p>

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
            ? t('web.agents.introDesktop')
            : t('web.agents.introServer')}
        </p>
      </div>

      {data.mcp && (
        <div className={daemonState(data.mcp, t).error ? 'error-message' : 'card muted'}>{daemonState(data.mcp, t).text}</div>
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
        <input className="input" placeholder={t('web.agents.newPlaceholder')} value={username} onChange={(e) => setUsername(e.target.value)} />
        <button className="button" type="submit" disabled={busy || !username.trim()}>
          {t('web.agents.create')}
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
  const t = useT()
  const locale = useLocale()
  const { busy, error, run } = useAction(reload)
  const [renaming, setRenaming] = useState(false)
  const [connecting, setConnecting] = useState(connectOpen)

  const rename = async (username: string) => {
    if (await run(() => api(`/agents/${agent.id}`, { method: 'PATCH', body: { username, version: agent.version } }))) setRenaming(false)
  }

  const remove = () => {
    if (!confirm(t('web.agents.confirmDelete', agent.username))) return
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
                {owner ? t('web.agents.ownerPrefix', owner.username) : ''}
                {t('web.agents.created', formatDate(locale, agent.createdAt))}
              </span>
            </div>
            <span className="item-actions">
              {mcp?.url && (
                <button type="button" className="button button-secondary button-small" onClick={() => setConnecting(!connecting)}>
                  {t('web.agents.connect')}
                </button>
              )}
              <IconButton title={t('web.agents.rename')} onClick={() => setRenaming(true)}>
                <IconEdit />
              </IconButton>
              <IconButton title={t('web.agents.deleteAgent')} danger disabled={busy} onClick={remove}>
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

const lifetimes: { label: StringKey; days: number | null }[] = [
  { label: 'web.agents.lifetimeNone', days: null },
  { label: 'web.agents.lifetime30', days: 30 },
  { label: 'web.agents.lifetime90', days: 90 },
  { label: 'web.agents.lifetimeYear', days: 365 },
]

function Tokens({ agent }: { agent: User }) {
  const t = useT()
  const locale = useLocale()
  const { data, error: loadError, reload } = useLoad(
    async () => ({ tokens: await loadAgentTokens(agent.id), loadedAt: Date.now() }),
    [agent.id],
  )
  const tokens = data?.tokens
  const { busy, error, run } = useAction(reload)
  const [name, setName] = useState('')
  const [lifetime, setLifetime] = useState(0)
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
    if (!confirm(t('web.agents.confirmRevoke', token.name))) return
    run(() => api(`/agents/${agent.id}/tokens/${token.id}`, { method: 'DELETE' }))
  }

  const state = (token: AgentToken) =>
    token.revokedAt
      ? t('web.agents.tokenRevoked')
      : token.expiresAt && data && Date.parse(token.expiresAt) <= data.loadedAt
        ? t('web.agents.tokenExpired')
        : null

  return (
    <div className="tokens">
      <h3 className="tokens-title">{t('web.agents.tokens')}</h3>

      {issued && <IssuedToken name={issued.name} value={issued.value} onClose={() => setIssued(null)} />}

      {loadError && <div className="error-message">{loadError}</div>}
      {tokens?.length === 0 && <p className="muted">{t('web.agents.tokensEmpty')}</p>}
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
                  <code>{token.prefix}…</code> · {t('web.agents.tokenIssued', formatDate(locale, token.createdAt))}
                  {token.expiresAt && ` · ${t('web.agents.tokenValidUntil', formatDate(locale, token.expiresAt))}`}
                  {' · '}
                  {token.lastUsedAt ? t('web.agents.tokenUsed', formatDateTime(locale, token.lastUsedAt)) : t('web.agents.tokenNeverUsed')}
                </span>
              </span>
              {!state(token) && (
                <span className="item-actions">
                  <IconButton title={t('web.agents.revokeToken')} danger disabled={busy} onClick={() => revoke(token)}>
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
        <input className="input" placeholder={t('web.agents.tokenNamePlaceholder')} value={name} onChange={(e) => setName(e.target.value)} />
        <select className="input select" value={lifetime} onChange={(e) => setLifetime(Number(e.target.value))} aria-label={t('web.agents.lifetimeLabel')}>
          {lifetimes.map((l, i) => (
            <option key={l.label} value={i}>
              {t(l.label)}
            </option>
          ))}
        </select>
        <button className="button" type="submit" disabled={busy || !name.trim()}>
          {t('web.agents.issueToken')}
        </button>
      </form>
    </div>
  )
}

function IssuedToken({ name, value, onClose }: { name: string; value: string; onClose: () => void }) {
  const t = useT()
  const command = `claude mcp add --transport http tasker ${window.location.origin}/mcp --header "Authorization: Bearer ${value}"`

  return (
    <div className="issued-token">
      <p>
        <strong>{t('web.agents.tokenIssuedTitle', name)}</strong> {t('web.agents.tokenCopyNow')}
      </p>
      <CopyField value={value} />
      <p className="muted">{t('web.agents.connectInClaude')}</p>
      <CopyField value={command} />
      <button type="button" className="button button-secondary" onClick={onClose}>
        {t('web.agents.doneCopied')}
      </button>
    </div>
  )
}

function LocalConnect({ agent, mcp, onClose }: { agent: User; mcp: { url: string; workspace: string | null; agentHeader: string }; onClose: () => void }) {
  const t = useT()
  const command = `claude mcp add --transport http tasker ${mcp.url} --header "${mcp.agentHeader}: ${agent.id}"`
  const commonCommand = `claude mcp add --transport http tasker ${mcp.url}`

  return (
    <div className="issued-token">
      <p>
        <strong>{t('web.agents.connectTitle', agent.username)}</strong> {t('web.agents.connectRun')}
      </p>
      <CopyField value={command} />
      <p className="muted">
        <RichText
          text={t('web.agents.connectDetails', mcp.url, mcp.workspace ? t('web.agents.connectWorkspace', mcp.workspace) : '', mcp.agentHeader)}
        />
      </p>
      <CopyField value={commonCommand} />
      <p className="muted">{t('web.agents.daemonKeepsRunning')}</p>
      <button type="button" className="button button-secondary" onClick={onClose}>
        {t('web.agents.done')}
      </button>
    </div>
  )
}

function CopyField({ value }: { value: string }) {
  const t = useT()
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
        {copied ? t('web.agents.copied') : t('web.agents.copy')}
      </button>
    </div>
  )
}

function formatDate(locale: string, value: string) {
  return new Date(value).toLocaleDateString(locale, { dateStyle: 'medium' })
}

function formatDateTime(locale: string, value: string) {
  return new Date(value).toLocaleString(locale, { dateStyle: 'short', timeStyle: 'short' })
}

export default AgentsSection
