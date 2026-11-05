import { ApiError, type Usage } from './auth'
import { errorParams, strings } from './generated/strings'
import { currentLanguage, format, hasKey, translateIn, type Arg, type Language } from './i18n'

export { ApiError } from './auth'
export type { Usage } from './auth'

const conflictCodes = new Set(['entity.modified', 'entity.locked', 'series.planChanged'])

function paramText(language: Language, name: string, value: unknown): Arg {
  if (Array.isArray(value)) return value.map((x) => paramText(language, name, x)).join(', ')
  if (value === null || value === undefined) return ''
  if (name === 'field' && typeof value === 'string') {
    const key = `error.label.${value}`
    return hasKey(key) ? translateIn(language, key) : value
  }
  if (typeof value === 'number') return value
  return String(value)
}

export function errorTextIn(language: Language, err: unknown): string {
  if (!(err instanceof ApiError)) return err instanceof Error ? err.message : String(err)
  const { code, params } = err
  if (!code || code === 'validation.invalid') return err.message
  const key = `error.${code}`
  if (!hasKey(key)) return err.message
  const names = errorParams[code] ?? []
  const args = names.map((name) => paramText(language, name, params[name]))
  return format(language, strings[language][key], args)
}

export function errorText(err: unknown): string {
  return errorTextIn(currentLanguage(), err)
}

export function isConflict(err: unknown): boolean {
  return err instanceof ApiError && err.code !== undefined && conflictCodes.has(err.code)
}

export function errorUsages(err: unknown): Usage[] {
  return err instanceof ApiError ? err.usages : []
}
