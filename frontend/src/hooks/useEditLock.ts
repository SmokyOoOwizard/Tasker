import { useCallback, useEffect, useRef } from 'react'
import { api } from '../api'
import { errorText } from '../errors'

const heartbeat = 40_000

export function useEditLock() {
  const held = useRef<{ path: string; timer: number } | null>(null)

  const unlock = useCallback(() => {
    const current = held.current
    if (!current) return
    held.current = null
    window.clearInterval(current.timer)
    api(`${current.path}/lock`, { method: 'DELETE' }).catch(() => {})
  }, [])

  const lock = useCallback(
    async (path: string): Promise<string | null> => {
      unlock()
      try {
        await api(`${path}/lock`, { method: 'POST' })
      } catch (err) {
        return errorText(err)
      }

      const timer = window.setInterval(() => api(`${path}/lock`, { method: 'POST' }).catch(() => {}), heartbeat)
      held.current = { path, timer }
      return null
    },
    [unlock],
  )

  useEffect(() => unlock, [unlock])

  return { lock, unlock }
}
