export const workspaceBase = window.location.pathname.match(/^\/w\/[^/]+/)?.[0] ?? ''

export const isDesktop = workspaceBase !== ''

export function apiUrl(path: string): string {
  return `${workspaceBase}/api${path}`
}
