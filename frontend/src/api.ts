import { accessToken, ApiError, dropSession, toApiError } from './auth'
import type { Page } from './types'
import { apiUrl, isDesktop, workspaceBase } from './workspace'

export { apiUrl } from './workspace'
export { ApiError } from './auth'

/**
 * Запрос к API текущей рабочей области. На сервере — с access-токеном; если сервер его отклонил,
 * токен обновляется и запрос повторяется один раз. Ошибка — {@link ApiError} с текстом сервера.
 */
export async function api<T>(path: string, init: { method?: string; body?: unknown } = {}): Promise<T> {
  const send = (token: string | null) =>
    fetch(apiUrl(path), {
      method: init.method ?? 'GET',
      headers: {
        ...(init.body !== undefined && { 'Content-Type': 'application/json' }),
        ...(token && { Authorization: `Bearer ${token}` }),
      },
      body: init.body !== undefined ? JSON.stringify(init.body) : undefined,
    })

  let response: Response
  if (isDesktop) {
    response = await send(null)
  } else {
    let token = await accessToken()
    if (!token) throw new ApiError(401, 'Sign in again')
    response = await send(token)
    if (response.status === 401) {
      token = await accessToken(token)
      if (!token) throw new ApiError(401, 'Sign in again')
      response = await send(token)
      // Токен свежий, а сервер всё равно не принимает: пароль сменили или пользователя удалили.
      if (response.status === 401) dropSession()
    }
  }

  if (!response.ok) throw await toApiError(response)
  return (response.status === 204 ? undefined : await response.json()) as T
}

/** Все элементы постраничного списка (для коротких списков: проекты, статусы, типы). */
export async function getAll<T>(path: string): Promise<T[]> {
  const limit = 200
  const items: T[] = []
  const separator = path.includes('?') ? '&' : '?'
  for (;;) {
    const page = await api<Page<T>>(`${path}${separator}offset=${items.length}&limit=${limit}`)
    items.push(...page.data)
    if (page.data.length === 0 || items.length >= page.totalCount) return items
  }
}

/** Что изменилось в рабочей области (десктоп): entity — project, task, taskType, status, statusSet, board, user или unknown. */
export type WorkspaceChange = { entity: string; projectId: string | null; id: string | null }

const changeListeners = new Set<(change: WorkspaceChange) => void>()
let changeSource: EventSource | null = null

/**
 * Изменения в рабочей области — из соседних вкладок, от агентов через MCP, руками и через git.
 * Только на десктопе (адрес /w/{id}/…). Возвращает функцию отписки.
 * Поток один на вкладку, сколько бы экранов ни подписалось: у браузера мало соединений на один хост.
 */
export function onWorkspaceChange(listener: (change: WorkspaceChange) => void): () => void {
  if (!workspaceBase) return () => {}
  changeListeners.add(listener)
  if (!changeSource) {
    changeSource = new EventSource(apiUrl('/events'))
    changeSource.onmessage = (e) => {
      const change = JSON.parse(e.data) as WorkspaceChange
      changeListeners.forEach((l) => l(change))
    }
  }
  return () => {
    changeListeners.delete(listener)
    if (changeListeners.size === 0) {
      changeSource?.close()
      changeSource = null
    }
  }
}
