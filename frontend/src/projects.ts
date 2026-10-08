import { createContext, useContext } from 'react'
import { useMatch } from 'react-router'
import type { Loaded } from './hooks/useLoad'
import type { Project } from './types'
import { workspaceBase } from './workspace'

/** Проекты, доступные пользователю (на десктопе — все проекты папки). Загружаются один раз на всё приложение. */
export const ProjectsContext = createContext<Loaded<Project[]> | null>(null)

export function useProjects(): Loaded<Project[]> {
  const value = useContext(ProjectsContext)
  if (!value) throw new Error('useProjects outside ProjectsProvider')
  return value
}

/** Проект из адреса (/projects/{id}/…); null — адрес не внутри проекта. */
export function useProjectId(): string | null {
  return useMatch('/projects/:projectId/*')?.params.projectId ?? null
}

/**
 * Проект, с которым работает пользователь: из адреса, а вне проекта (например, в настройках пользователя) —
 * последний открытый. null — проектов нет.
 */
export function useCurrentProjectId(): string | null {
  const fromUrl = useProjectId()
  const { data: projects } = useProjects()
  if (fromUrl) return fromUrl
  return projects ? (lastProject(projects)?.id ?? null) : null
}

// Последний открытый проект — свой у каждой рабочей области десктопа.
const lastProjectKey = `tasker.lastProject:${workspaceBase || 'server'}`

export function rememberProject(id: string) {
  try {
    localStorage.setItem(lastProjectKey, id)
  } catch {
    // Не запомнили — откроется первый проект.
  }
}

export function lastProject(projects: Project[]): Project | undefined {
  let id: string | null = null
  try {
    id = localStorage.getItem(lastProjectKey)
  } catch {
    // см. rememberProject
  }
  return projects.find((p) => p.id === id) ?? projects[0]
}
