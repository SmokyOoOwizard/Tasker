import { useEffect, useRef, useState, type FormEvent } from 'react'
import { api } from '../../api'
import { IconEdit, IconTrash } from '../../components/Icons'
import UsagesList from '../../components/UsagesList'
import { LockBadge } from '../../components/LockBadge'
import { DescriptionInput, IconButton, ItemDescription } from '../../components/ui'
import { useAction } from '../../hooks/useAction'
import { useT } from '../../i18n'
import { useEditLock } from '../../hooks/useEditLock'
import type { SettingsSectionProps, Status } from '../../types'

const palette = ['#64748b', '#6366f1', '#f59e0b', '#14b8a6', '#e11d48', '#8b5cf6', '#0ea5e9', '#22c55e']
const nextColor = (count: number) => palette[count % palette.length]

function StatusesSection({ base, settings, reload, locks }: SettingsSectionProps) {
  const t = useT()
  const { busy, error, usages, setError, run } = useAction(reload)
  const editLock = useEditLock()
  const [name, setName] = useState('')
  const [description, setDescription] = useState('')
  const [color, setColor] = useState(nextColor(settings.statuses.length))
  const [editingId, setEditingId] = useState<string | null>(null)

  const add = async (e: FormEvent) => {
    e.preventDefault()
    if (await run(() => api(`${base}/statuses`, { method: 'POST', body: { name, color, description } }))) {
      setName('')
      setDescription('')
      setColor(nextColor(settings.statuses.length + 1))
    }
  }

  const startEdit = async (status: Status) => {
    const problem = await editLock.lock(`${base}/statuses/${status.id}`)
    if (problem) setError(problem)
    else setEditingId(status.id)
  }

  const stopEdit = () => {
    editLock.unlock()
    setEditingId(null)
  }

  const update = (status: Status, changes: { name?: string; color?: string; description?: string }) =>
    run(() => api(`${base}/statuses/${status.id}`, { method: 'PATCH', body: { ...changes, version: status.version } }))

  const remove = (status: Status) => {
    if (!confirm(t('web.statuses.confirmDelete', status.name))) return
    run(() => api(`${base}/statuses/${status.id}?version=${encodeURIComponent(status.version)}`, { method: 'DELETE' }))
  }

  return (
    <section className="card">
      <h2 className="card-title">{t('web.settings.statuses')}</h2>

      {settings.statuses.length === 0 ? (
        <p className="muted">{t('web.statuses.empty')}</p>
      ) : (
        <ul className="item-list">
          {settings.statuses.map((status) => {
            const other = locks.byOther('status', status.id)
            return (
            <li key={status.id} className="item-row">
              <ColorInput key={status.version} value={status.color} onCommit={(next) => update(status, { color: next })} />
              {editingId === status.id ? (
                <StatusEditForm
                  status={status}
                  busy={busy}
                  onCancel={stopEdit}
                  onSave={async (changes) => (await update(status, changes)) && stopEdit()}
                />
              ) : (
                <>
                  <span className="item-text">
                    <span className="item-name">{status.name}</span>
                    <ItemDescription text={status.description} />
                  </span>
                  {other && <LockBadge lock={other} />}
                  <span className="item-actions">
                    <IconButton title={t('web.common.edit')} disabled={!!other} onClick={() => startEdit(status)}>
                      <IconEdit />
                    </IconButton>
                    <IconButton title={t('web.common.delete')} danger disabled={busy || !!other} onClick={() => remove(status)}>
                      <IconTrash />
                    </IconButton>
                  </span>
                </>
              )}
            </li>
            )
          })}
        </ul>
      )}

      {error && <div className="error-message">{error}</div>}
      <UsagesList usages={usages} />

      <form className="inline-form" onSubmit={add}>
        <input type="color" className="color-input" title={t('web.statuses.color')} value={color} onChange={(e) => setColor(e.target.value)} />
        <input className="input" placeholder={t('web.statuses.newPlaceholder')} value={name} onChange={(e) => setName(e.target.value)} />
        <DescriptionInput value={description} onChange={setDescription} placeholder={t('web.statuses.descriptionPlaceholder')} />
        <button className="button" type="submit" disabled={busy || !name.trim()}>
          {t('web.common.add')}
        </button>
      </form>
    </section>
  )
}

function StatusEditForm({
  status,
  busy,
  onSave,
  onCancel,
}: {
  status: Status
  busy: boolean
  onSave: (changes: { name: string; description: string }) => void
  onCancel: () => void
}) {
  const t = useT()
  const [name, setName] = useState(status.name)
  const [description, setDescription] = useState(status.description)
  return (
    <form
      className="inline-form inline-form-row"
      onSubmit={(e) => {
        e.preventDefault()
        onSave({ name, description })
      }}
      onKeyDown={(e) => e.key === 'Escape' && onCancel()}
    >
      <input className="input" value={name} onChange={(e) => setName(e.target.value)} autoFocus />
      <DescriptionInput value={description} onChange={setDescription} placeholder={t('web.statuses.descriptionOptional')} />
      <button className="button" type="submit" disabled={busy || !name.trim()}>
        {t('web.common.save')}
      </button>
      <button className="button button-secondary" type="button" onClick={onCancel}>
        {t('web.common.cancel')}
      </button>
    </form>
  )
}

function ColorInput({ value, onCommit }: { value: string; onCommit: (color: string) => void }) {
  const t = useT()
  const [color, setColor] = useState(value)
  const timer = useRef<number | undefined>(undefined)
  useEffect(() => () => window.clearTimeout(timer.current), [])

  return (
    <input
      type="color"
      className="color-input"
      title={t('web.statuses.color')}
      value={color}
      onChange={(e) => {
        const next = e.target.value
        setColor(next)
        window.clearTimeout(timer.current)
        timer.current = window.setTimeout(() => next !== value && onCommit(next), 600)
      }}
    />
  )
}

export default StatusesSection
