import { useCallback, useEffect, useRef } from 'react'
import { api } from '../api'
import { errorText } from '../errors'

// Блокировка действует 2 минуты (Tasker.Core.Locks.EditLockService.Duration): продлеваем втрое чаще.
const heartbeat = 40_000

/**
 * Блокировка записи на время правки: «правит Иван». Берётся при открытии формы правки, продлевается, пока форма
 * открыта, снимается при закрытии и при уходе с экрана. Занято другим — lock() вернёт текст ошибки и форму открывать не нужно.
 * Одна блокировка за раз: новая снимает прежнюю. Забытую (закрыли вкладку) сервер снимает сам через 2 минуты.
 *
 *   const editLock = useEditLock()
 *   const problem = await editLock.lock(`${base}/statuses/${id}`)
 *   if (problem) setError(problem); else setEditing(id)
 */
export function useEditLock() {
  const held = useRef<{ path: string; timer: number } | null>(null)

  const unlock = useCallback(() => {
    const current = held.current
    if (!current) return
    held.current = null
    window.clearInterval(current.timer)
    // Не удалось снять (сеть) — не страшно: блокировка истечёт сама.
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

      // Не продлилась (сеть, блокировку успели занять) — молчим: об этом скажет отказ при сохранении.
      const timer = window.setInterval(() => api(`${path}/lock`, { method: 'POST' }).catch(() => {}), heartbeat)
      held.current = { path, timer }
      return null
    },
    [unlock],
  )

  // Ушли с экрана с открытой формой — блокировку снимаем.
  useEffect(() => unlock, [unlock])

  return { lock, unlock }
}
