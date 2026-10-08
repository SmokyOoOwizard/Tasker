import { ApiError } from './auth'

/** Текст ошибки для экрана. Конфликт версий и занятую запись объясняем по-русски: остальные тексты сервера и так понятны. */
export function errorText(err: unknown): string {
  if (err instanceof ApiError && err.code === 'modified')
    return 'Запись уже изменили в другом месте. Данные обновлены — повторите изменение.'
  if (err instanceof ApiError && err.code === 'locked')
    return `Запись сейчас правит ${err.heldBy?.name ?? 'другой пользователь'}. Подождите, пока закончит, и повторите.`
  return err instanceof Error ? err.message : String(err)
}

/** Запись изменили или занял другой: данные на экране устарели, их нужно перечитать. */
export function isConflict(err: unknown): boolean {
  return err instanceof ApiError && (err.code === 'modified' || err.code === 'locked')
}
