import { useSyncExternalStore } from 'react'
import { apiUrl } from './workspace'

// Сессия входа на сервере. На десктопе входа нет, и этот модуль не используется.

export type User = {
  id: string
  username: string
  kind: 'human' | 'agent'
  /** Только у агента: человек, который им управляет. */
  ownerId?: string | null
  email: string | null
  isAdmin: boolean
  createdAt: string
  version: string
}

export type Session = {
  accessToken: string
  accessTokenExpiresAt: string
  refreshToken: string
  refreshTokenExpiresAt: string
  user: User
}

// Одна сессия на все вкладки браузера: localStorage общий, а изменения из соседних вкладок приходят событием storage.
const storageKey = 'tasker.session'

// Access-токен обновляем заранее, чтобы он не истёк по дороге к серверу.
const refreshMarginMs = 30_000

let session: Session | null = read()
const listeners = new Set<() => void>()

function read(): Session | null {
  try {
    const raw = localStorage.getItem(storageKey)
    return raw ? (JSON.parse(raw) as Session) : null
  } catch {
    return null
  }
}

function write(next: Session | null) {
  session = next
  try {
    if (next) localStorage.setItem(storageKey, JSON.stringify(next))
    else localStorage.removeItem(storageKey)
  } catch {
    // Хранилище недоступно (приватный режим): сессия живёт до закрытия вкладки.
  }
  listeners.forEach((l) => l())
}

window.addEventListener('storage', (e) => {
  if (e.key !== storageKey) return
  session = read()
  listeners.forEach((l) => l())
})

function subscribe(listener: () => void) {
  listeners.add(listener)
  return () => listeners.delete(listener)
}

/** Текущая сессия; null — не выполнен вход (показывается экран входа). */
export function useSession(): Session | null {
  return useSyncExternalStore(subscribe, () => session)
}

/** Пользователь изменил свой профиль — обновить его в сессии. */
export function updateSessionUser(user: User) {
  if (session) write({ ...session, user })
}

/** Ошибка API: текст и код (`in_use`, `modified`…) из ответа сервера. */
export class ApiError extends Error {
  readonly status: number
  readonly code: string | undefined
  /** Код `locked`: кто правит запись и до какого времени. */
  readonly heldBy: { name: string; expiresAt: string } | undefined

  constructor(status: number, message: string, code?: string, heldBy?: { name: string; expiresAt: string }) {
    super(message)
    this.status = status
    this.code = code
    this.heldBy = heldBy
  }
}

export async function toApiError(response: Response): Promise<ApiError> {
  const body = (await response.json().catch(() => null)) as {
    error?: string
    code?: string
    heldBy?: { name: string; expiresAt: string } | null
  } | null
  return new ApiError(response.status, body?.error ?? `HTTP ${response.status}`, body?.code, body?.heldBy ?? undefined)
}

async function post(path: string, body: unknown): Promise<Response> {
  return fetch(apiUrl(path), {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(body),
  })
}

async function start(path: string, body: unknown): Promise<User> {
  const response = await post(path, body)
  if (!response.ok) throw await toApiError(response)
  const next = (await response.json()) as Session
  write(next)
  return next.user
}

export function signIn(login: string, password: string): Promise<User> {
  return start('/auth/login', { login, password })
}

/** Регистрация первого пользователя сервера — он становится админом. */
export function register(username: string, email: string, password: string): Promise<User> {
  return start('/auth/register', { username, email, password })
}

/** Выход: сессия отзывается на сервере, все вкладки возвращаются на экран входа. */
export async function signOut() {
  const current = session
  write(null)
  if (current) await post('/auth/logout', { refreshToken: current.refreshToken }).catch(() => {})
}

/** Сессия закончилась (refresh-токен истёк или отозван): на экран входа. */
export function dropSession() {
  write(null)
}

/**
 * Access-токен для запроса; при необходимости обменивает refresh-токен.
 * null — сессии нет или она закончилась.
 * @param rejected токен, который сервер только что отклонил (401): его нужно заменить, даже если срок не вышел.
 */
export async function accessToken(rejected?: string): Promise<string | null> {
  const current = session
  if (!current) return null
  const fresh = Date.parse(current.accessTokenExpiresAt) - Date.now() > refreshMarginMs
  if (fresh && current.accessToken !== rejected) return current.accessToken
  return (await refresh(current.refreshToken))?.accessToken ?? null
}

let refreshing: Promise<Session | null> | null = null

// Обмен refresh-токена — из одного места на все вкладки: параллельный обмен одного токена
// сервер считает повтором украденного токена и отзывает сессию. Поэтому обмен идёт под
// блокировкой Web Locks, общей для вкладок, а внутри неё сессия перечитывается: если токен
// уже обменяла другая вкладка, берётся её результат. Без Web Locks (http не на localhost) —
// очередь только внутри вкладки.
function refresh(stale: string): Promise<Session | null> {
  refreshing ??= (navigator.locks
    ? navigator.locks.request('tasker.auth.refresh', () => exchange(stale))
    : exchange(stale)
  ).finally(() => (refreshing = null))
  return refreshing
}

async function exchange(stale: string): Promise<Session | null> {
  const current = read()
  if (!current) {
    write(null)
    return null
  }
  if (current.refreshToken !== stale) {
    session = current
    return current
  }

  const response = await post('/auth/refresh', { refreshToken: stale })
  if (response.status === 401) {
    write(null)
    return null
  }
  if (!response.ok) throw await toApiError(response)
  const next = (await response.json()) as Session
  write(next)
  return next
}
