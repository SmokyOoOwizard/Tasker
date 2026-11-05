import { useCallback, useEffect, useRef, useState } from 'react'
import { onWorkspaceChange } from '../api'
import { errorText } from '../errors'
import { useLanguage } from '../i18n'

export type Loaded<T> = {
  data: T | undefined
  error: string | null
  reload: () => void
  mutate: (update: (value: T) => T) => void
}

export function useLoad<T>(load: () => Promise<T>, deps: unknown[]): Loaded<T> {
  const key = JSON.stringify(deps)
  const [loaded, setLoaded] = useState<{ key: string; value: T }>()
  useLanguage()
  const [failure, setError] = useState<unknown>(null)
  const [generation, setGeneration] = useState(0)
  const reload = useCallback(() => setGeneration((g) => g + 1), [])

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
        if (!cancelled) setError(err)
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

  return { data: loaded?.key === key ? loaded.value : undefined, error: failure === null ? null : errorText(failure), reload, mutate }
}
