import { useState, type ReactNode } from 'react'
import { useT } from '../i18n'
import type { Status } from '../types'

export function StatusChip({ status }: { status: Status | undefined }) {
  if (!status) return <span className="chip muted">—</span>
  return (
    <span className="chip" title={status.description || undefined}>
      <span className="color-dot" style={{ background: status.color }} />
      {status.name}
    </span>
  )
}

export function DescriptionInput({
  value,
  onChange,
  placeholder,
}: {
  value: string
  onChange: (value: string) => void
  placeholder: string
}) {
  const t = useT()
  return (
    <textarea
      className="input textarea"
      rows={2}
      placeholder={placeholder}
      aria-label={t('web.common.description')}
      value={value}
      onChange={(e) => onChange(e.target.value)}
    />
  )
}

export function ItemDescription({ text }: { text: string }) {
  if (!text) return null
  return (
    <span className="item-description" title={text}>
      {text}
    </span>
  )
}

export function IconButton({
  title,
  danger,
  disabled,
  onClick,
  children,
}: {
  title: string
  danger?: boolean
  disabled?: boolean
  onClick: () => void
  children: ReactNode
}) {
  return (
    <button
      type="button"
      className={danger ? 'icon-btn icon-btn-danger' : 'icon-btn'}
      title={title}
      aria-label={title}
      disabled={disabled}
      onClick={onClick}
    >
      {children}
    </button>
  )
}

export function RenameForm({
  initial,
  busy,
  onSave,
  onCancel,
}: {
  initial: string
  busy: boolean
  onSave: (name: string) => void
  onCancel: () => void
}) {
  const t = useT()
  const [name, setName] = useState(initial)
  return (
    <form
      className="inline-form inline-form-row"
      onSubmit={(e) => {
        e.preventDefault()
        onSave(name)
      }}
      onKeyDown={(e) => e.key === 'Escape' && onCancel()}
    >
      <input className="input" value={name} onChange={(e) => setName(e.target.value)} autoFocus />
      <button className="button" type="submit" disabled={busy || !name.trim()}>
        {t('web.common.save')}
      </button>
      <button className="button button-secondary" type="button" onClick={onCancel}>
        {t('web.common.cancel')}
      </button>
    </form>
  )
}
