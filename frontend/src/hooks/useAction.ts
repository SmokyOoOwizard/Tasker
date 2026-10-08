import { useState } from 'react'
import { errorText, isConflict } from '../errors'

/**
 * Изменение на сервере из экрана: занятость, текст ошибки и перезагрузка данных после.
 * run() возвращает true, если всё прошло; при конфликте версий данные тоже перезагружаются.
 */
export function useAction(reload: () => void) {
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const run = async (action: () => Promise<unknown>): Promise<boolean> => {
    setBusy(true)
    setError(null)
    try {
      await action()
      reload()
      return true
    } catch (err) {
      setError(errorText(err))
      if (isConflict(err)) reload()
      return false
    } finally {
      setBusy(false)
    }
  }

  return { busy, error, setError, run }
}
