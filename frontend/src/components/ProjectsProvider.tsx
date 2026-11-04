import type { ReactNode } from 'react'
import { useLoad } from '../hooks/useLoad'
import { ProjectsContext } from '../projects'
import { loadProjects } from '../reads'

function ProjectsProvider({ children }: { children: ReactNode }) {
  const projects = useLoad(() => loadProjects(), [])
  return <ProjectsContext.Provider value={projects}>{children}</ProjectsContext.Provider>
}

export default ProjectsProvider
