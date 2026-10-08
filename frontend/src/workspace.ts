// Десктоп: каждая вкладка — рабочая область (папка), её адрес — /w/{id}/…, и API у неё своё: /w/{id}/api/….
// Сервер: префикса нет, API — /api/….
export const workspaceBase = window.location.pathname.match(/^\/w\/[^/]+/)?.[0] ?? ''

/** Десктоп (Tasker.Desktop): входа нет, пользователь не выбирается. */
export const isDesktop = workspaceBase !== ''

/** Адрес API текущей рабочей области: apiUrl('/projects') → '/w/{id}/api/projects'. */
export function apiUrl(path: string): string {
  return `${workspaceBase}/api${path}`
}
