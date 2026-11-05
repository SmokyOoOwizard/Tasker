import { useEffect, useMemo } from 'react'
import { loadLocks } from '../reads'
import type { LockIndex } from '../types'
import { isDesktop } from '../workspace'
import { useLoad } from './useLoad'

const serverPollInterval = 15_000

export function useLocks(projectId: string): LockIndex {
  const { data, reload } = useLoad(() => loadLocks(projectId), [projectId])

  useEffect(() => {
    if (isDesktop) return
    const timer = window.setInterval(reload, serverPollInterval)
    return () => window.clearInterval(timer)
  }, [reload])

  return useMemo(() => {
    const others = new Map(data?.filter((x) => !x.mine).map((x) => [`${x.entity}:${x.id}`, x]))
    return { byOther: (entity, id) => others.get(`${entity}:${id}`) }
  }, [data])
}
