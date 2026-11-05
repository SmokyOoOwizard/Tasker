import { useState } from 'react'
import { errorText, errorUsages, isConflict, type Usage } from '../errors'
import { useLanguage } from '../i18n'

type Failure = { kind: 'error'; err: unknown } | { kind: 'text'; text: string }

export function useAction(reload: () => void) {
  useLanguage()
  const [busy, setBusy] = useState(false)
  const [failure, setFailure] = useState<Failure | null>(null)

  const run = async (action: () => Promise<unknown>): Promise<boolean> => {
    setBusy(true)
    setFailure(null)
    try {
      await action()
      reload()
      return true
    } catch (err) {
      setFailure({ kind: 'error', err })
      if (isConflict(err)) reload()
      return false
    } finally {
      setBusy(false)
    }
  }

  const setError = (text: string | null) => setFailure(text === null ? null : { kind: 'text', text })

  const error = failure === null ? null : failure.kind === 'text' ? failure.text : errorText(failure.err)
  const usages: Usage[] = failure?.kind === 'error' ? errorUsages(failure.err) : []

  return { busy, error, usages, setError, run }
}
