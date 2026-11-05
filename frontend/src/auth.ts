import { useSyncExternalStore } from 'react'
import { apiUrl } from './workspace'


export type User = {
  id: string
  username: string
  kind: 'human' | 'agent'
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

const storageKey = 'tasker.session'

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

export function useSession(): Session | null {
  return useSyncExternalStore(subscribe, () => session)
}

export function updateSessionUser(user: User) {
  if (session) write({ ...session, user })
}

export type Usage = {
  kind: string
  name?: string
  count?: number
  tasks?: string[]
  board?: string
  column?: string
}

export type ErrorParams = Record<string, unknown>

export class ApiError extends Error {
  readonly status: number
  readonly code: string | undefined
  readonly params: ErrorParams
  readonly usages: Usage[]

  constructor(status: number, message: string, code?: string, params?: ErrorParams | null, usages?: Usage[] | null) {
    super(message)
    this.status = status
    this.code = code
    this.params = params ?? {}
    this.usages = usages ?? []
  }
}

export async function toApiError(response: Response): Promise<ApiError> {
  const body = (await response.json().catch(() => null)) as {
    error?: string
    code?: string
    params?: ErrorParams | null
    usages?: Usage[] | null
  } | null
  return new ApiError(response.status, body?.error ?? `HTTP ${response.status}`, body?.code, body?.params, body?.usages)
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

export function register(username: string, email: string, password: string): Promise<User> {
  return start('/auth/register', { username, email, password })
}

export async function signOut() {
  const current = session
  write(null)
  if (current) await post('/auth/logout', { refreshToken: current.refreshToken }).catch(() => {})
}

export function dropSession() {
  write(null)
}

export async function accessToken(rejected?: string): Promise<string | null> {
  const current = session
  if (!current) return null
  const fresh = Date.parse(current.accessTokenExpiresAt) - Date.now() > refreshMarginMs
  if (fresh && current.accessToken !== rejected) return current.accessToken
  return (await refresh(current.refreshToken))?.accessToken ?? null
}

let refreshing: Promise<Session | null> | null = null

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
