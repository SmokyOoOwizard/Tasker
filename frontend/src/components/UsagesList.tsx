import { hasKey, useT } from '../i18n'
import type { Usage } from '../errors'

function UsagesList({ usages }: { usages: Usage[] }) {
  const t = useT()
  if (usages.length === 0) return null
  return (
    <ul className="usage-list">
      {usages.map((usage, index) => {
        const key = `error.usage.${usage.kind}`
        const label = hasKey(key) ? t(key, usage.kind === 'boardColumn' ? (usage.board ?? '') : (usage.count ?? 0)) : usage.kind
        const value = usage.tasks?.length ? usage.tasks.join(', ') : (usage.column ?? usage.name ?? '')
        return (
          <li key={`${usage.kind}-${index}`}>
            <span className="usage-label">{label}</span>
            {value && <span className="usage-value">{value}</span>}
          </li>
        )
      })}
    </ul>
  )
}

export default UsagesList
