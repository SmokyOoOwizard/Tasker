import type { EditLock } from '../types'
import { IconLock } from './Icons'
import '../styles/locks.css'

/** «Правит Иван» у записи, которую сейчас редактирует другой: изменить и удалить её, пока он не закончит, нельзя. */
export function LockBadge({ lock }: { lock: EditLock }) {
  const until = new Date(lock.expiresAt).toLocaleTimeString('ru-RU', { timeStyle: 'short' })
  return (
    <span className="lock-badge" title={`Правит ${lock.holder}. Блокировка снимется, когда он закончит, но не позже ${until}.`}>
      <IconLock />
      Правит {lock.holder}
    </span>
  )
}
