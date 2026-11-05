import { useLocale, useT } from '../i18n'
import type { EditLock } from '../types'
import { IconLock } from './Icons'
import '../styles/locks.css'

export function LockBadge({ lock }: { lock: EditLock }) {
  const t = useT()
  const locale = useLocale()
  const until = new Date(lock.expiresAt).toLocaleTimeString(locale, { timeStyle: 'short' })
  return (
    <span className="lock-badge" title={t('web.lock.title', lock.holder, until)}>
      <IconLock />
      {t('web.lock.badge', lock.holder)}
    </span>
  )
}
