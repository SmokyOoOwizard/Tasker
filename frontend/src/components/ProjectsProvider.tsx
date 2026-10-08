import type { ReactNode } from 'react'
import { getAll } from '../api'
import { useLoad } from '../hooks/useLoad'
import { ProjectsContext } from '../projects'
import type { Project } from '../types'

function ProjectsProvider({ children }: { children: ReactNode }) {
  const projects = useLoad(() => getAll<Project>('/projects'), [])
  return <ProjectsContext.Provider value={projects}>{children}</ProjectsContext.Provider>
}

export default ProjectsProvider
