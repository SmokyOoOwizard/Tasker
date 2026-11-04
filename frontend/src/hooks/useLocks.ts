import { useEffect, useMemo } from 'react'
import { loadLocks } from '../reads'
import type { LockIndex } from '../types'
import { isDesktop } from '../workspace'
import { useLoad } from './useLoad'

// На сервере событий об изменениях нет (они только на десктопе), поэтому блокировки перечитываем по таймеру.
const serverPollInterval = 15_000

/**
 * Кто сейчас правит сущности проекта. На десктопе обновляется по событиям рабочей области
 * (useLoad), на сервере — раз в 15 секунд. Пока блокировки не загрузились (или не загрузились совсем), считаем, что их нет:
 * плашка — подсказка, а запрет всё равно даёт сервер при сохранении.
 */
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
