import { useState, type ReactNode } from 'react'
import type { Status } from '../types'

/** Статус задачи: цветная точка и название. */
export function StatusChip({ status }: { status: Status | undefined }) {
  if (!status) return <span className="chip muted">—</span>
  return (
    <span className="chip" title={status.description || undefined}>
      <span className="color-dot" style={{ background: status.color }} />
      {status.name}
    </span>
  )
}

/** Описание статуса или типа задачи: многострочное, необязательное; пустое при сохранении очищает его. */
export function DescriptionInput({
  value,
  onChange,
  placeholder,
}: {
  value: string
  onChange: (value: string) => void
  placeholder: string
}) {
  return (
    <textarea
      className="input textarea"
      rows={2}
      placeholder={placeholder}
      aria-label="Описание"
      value={value}
      onChange={(e) => onChange(e.target.value)}
    />
  )
}

/** Описание под названием в строке списка: полный текст — в подсказке. */
export function ItemDescription({ text }: { text: string }) {
  if (!text) return null
  return (
    <span className="item-description" title={text}>
      {text}
    </span>
  )
}

/** Кнопка-иконка в строке списка. */
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

/** Переименование в строке списка. */
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
        Сохранить
      </button>
      <button className="button button-secondary" type="button" onClick={onCancel}>
        Отмена
      </button>
    </form>
  )
}
