import { accessToken, ApiError, dropSession, toApiError } from './auth'
import type { Page } from './types'
import { apiUrl, isDesktop, workspaceBase } from './workspace'

export { apiUrl } from './workspace'
export { ApiError } from './auth'

type Send = { method: string; body?: unknown; headers?: Record<string, string>; signal?: AbortSignal; anonymous?: boolean }

/**
 * Запрос к API текущей рабочей области. На сервере — с access-токеном; если сервер его отклонил,
 * токен обновляется и запрос повторяется один раз. Ошибка — {@link ApiError} с текстом сервера.
 */
async function send(path: string, init: Send): Promise<Response> {
  const request = (token: string | null) =>
    fetch(apiUrl(path), {
      method: init.method,
      headers: {
        ...(init.body !== undefined && { 'Content-Type': 'application/json' }),
        ...(token && { Authorization: `Bearer ${token}` }),
        ...init.headers,
      },
      body: init.body !== undefined ? JSON.stringify(init.body) : undefined,
      signal: init.signal,
    })

  if (isDesktop || init.anonymous) return request(null)

  let token = await accessToken()
  if (!token) throw new ApiError(401, 'Sign in again')
  let response = await request(token)
  if (response.status === 401) {
    token = await accessToken(token)
    if (!token) throw new ApiError(401, 'Sign in again')
    response = await request(token)
    // Токен свежий, а сервер всё равно не принимает: пароль сменили или пользователя удалили.
    if (response.status === 401) dropSession()
  }
  return response
}

export async function api<T>(path: string, init: { method: 'POST' | 'PATCH' | 'DELETE'; body?: unknown }): Promise<T> {
  const response = await send(path, init)
  if (!response.ok) throw await toApiError(response)
  return (response.status === 204 ? undefined : await response.json()) as T
}

type GraphError = {
  message: string
  extensions?: { status?: number; conflict?: string; heldBy?: { name: string; expiresAt: string } }
}

export async function graphql<T>(query: string, variables: Record<string, unknown> = {}, options: { anonymous?: boolean } = {}): Promise<T> {
  const response = await send('/graphql', { method: 'POST', body: { query, variables }, anonymous: options.anonymous })
  const body = (await response.json().catch(() => null)) as { data?: T | null; errors?: GraphError[] } | null
  const error = body?.errors?.[0]
  if (error)
    throw new ApiError(error.extensions?.status ?? response.status, error.message, error.extensions?.conflict, error.extensions?.heldBy)
  if (!response.ok || !body?.data) throw new ApiError(response.status, `HTTP ${response.status}`)
  return body.data
}

/** Все элементы постраничного списка (для коротких списков: проекты, статусы, типы). */
export async function graphqlAll<T, R = unknown>(
  query: string,
  variables: Record<string, unknown>,
  pick: (data: R) => Page<T>,
): Promise<T[]> {
  const limit = 200
  const items: T[] = []
  for (;;) {
    const page = pick(await graphql<R>(query, { ...variables, offset: items.length, limit }))
    items.push(...page.data)
    if (page.data.length === 0 || items.length >= page.totalCount) return items
  }
}

/** Что изменилось в рабочей области (десктоп): entity — project, task, taskType, status, statusSet, board, user или unknown. */
export type WorkspaceChange = { entity: string; projectId: string | null; id: string | null }

const changeListeners = new Set<(change: WorkspaceChange) => void>()
let changeStream: AbortController | null = null

const reconnectDelayMs = 3_000
const changesQuery = 'subscription { workspaceChanges { entity projectId id } }'

/**
 * Изменения в рабочей области — из соседних вкладок, от агентов через MCP, руками и через git.
 * Только на десктопе (адрес /w/{id}/…). Возвращает функцию отписки.
 * Поток один на вкладку, сколько бы экранов ни подписалось: у браузера мало соединений на один хост.
 */
export function onWorkspaceChange(listener: (change: WorkspaceChange) => void): () => void {
  if (!workspaceBase) return () => {}
  changeListeners.add(listener)
  if (!changeStream) {
    changeStream = new AbortController()
    void watchChanges(changeStream.signal)
  }
  return () => {
    changeListeners.delete(listener)
    if (changeListeners.size === 0) {
      changeStream?.abort()
      changeStream = null
    }
  }
}

async function watchChanges(signal: AbortSignal) {
  while (!signal.aborted) {
    const response = await send('/graphql', {
      method: 'POST',
      body: { query: changesQuery },
      headers: { Accept: 'text/event-stream' },
      signal,
    }).catch(() => null)
    if (response?.ok && response.body) await readEvents(response.body, signal).catch(() => undefined)
    if (!signal.aborted) await new Promise((resolve) => setTimeout(resolve, reconnectDelayMs))
  }
}

async function readEvents(body: ReadableStream<BufferSource>, signal: AbortSignal) {
  const reader = body.pipeThrough(new TextDecoderStream()).getReader()
  signal.addEventListener('abort', () => void reader.cancel().catch(() => {}))
  let buffer = ''
  for (;;) {
    const { value, done } = await reader.read()
    if (done) return
    buffer += value
    let end: number
    while ((end = buffer.indexOf('\n\n')) >= 0) {
      const block = buffer.slice(0, end)
      buffer = buffer.slice(end + 2)
      const lines = block.split('\n')
      if (!lines.includes('event: next')) continue
      const data = lines.filter((l) => l.startsWith('data:')).map((l) => l.slice('data:'.length).trimStart()).join('\n')
      const change = (JSON.parse(data) as { data?: { workspaceChanges?: WorkspaceChange } }).data?.workspaceChanges
      if (change) changeListeners.forEach((l) => l(change))
    }
  }
}
