import { useCallback, useEffect, useRef, useState } from 'react'
import { onWorkspaceChange } from '../api'

export type Loaded<T> = {
  /** undefined — ещё грузится. При reload() остаются прежние данные, пока не придут новые. */
  data: T | undefined
  error: string | null
  reload: () => void
  /** Заменить данные сразу, не дожидаясь перезагрузки (например, добавить только что созданное). */
  mutate: (update: (value: T) => T) => void
}

/**
 * Данные с сервера для экрана. Перезагружаются при смене deps, по reload() и — на десктопе —
 * при любом изменении в рабочей области (соседняя вкладка, агент, git).
 */
export function useLoad<T>(load: () => Promise<T>, deps: unknown[]): Loaded<T> {
  // Данные помечены ключом deps: после смены проекта не показываем данные прежнего, пока грузятся новые.
  const key = JSON.stringify(deps)
  const [loaded, setLoaded] = useState<{ key: string; value: T }>()
  const [error, setError] = useState<string | null>(null)
  const [generation, setGeneration] = useState(0)
  const reload = useCallback(() => setGeneration((g) => g + 1), [])

  // Последний вызов load, чтобы не тянуть её в deps: она создаётся заново на каждом рендере.
  // Эффект объявлен раньше загрузки, поэтому к её запуску ссылка уже свежая.
  const loadRef = useRef(load)
  useEffect(() => {
    loadRef.current = load
  })

  useEffect(() => {
    let cancelled = false
    loadRef.current().then(
      (result) => {
        if (cancelled) return
        setLoaded({ key, value: result })
        setError(null)
      },
      (err: unknown) => {
        if (!cancelled) setError(err instanceof Error ? err.message : String(err))
      },
    )
    return () => {
      cancelled = true
    }
  }, [key, generation])

  useEffect(() => onWorkspaceChange(reload), [reload])

  const mutate = useCallback(
    (update: (value: T) => T) => setLoaded((prev) => (prev?.key === key ? { key, value: update(prev.value) } : prev)),
    [key],
  )

  return { data: loaded?.key === key ? loaded.value : undefined, error, reload, mutate }
}
