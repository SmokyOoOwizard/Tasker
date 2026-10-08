import { useEffect } from 'react'
import { BrowserRouter, Navigate, Outlet, Route, Routes, useParams } from 'react-router'
import { api } from './api'
import { updateSessionUser, useSession, type User } from './auth'
import ProjectsProvider from './components/ProjectsProvider'
import Sidebar from './components/Sidebar'
import BoardsPage from './pages/BoardsPage'
import LoginPage from './pages/LoginPage'
import NewProjectPage from './pages/NewProjectPage'
import ProfilePage from './pages/ProfilePage'
import SettingsPage from './pages/SettingsPage'
import TasksPage from './pages/TasksPage'
import { lastProject, rememberProject, useProjects } from './projects'
import { isDesktop, workspaceBase } from './workspace'
import './styles/pages.css'

function Layout() {
  return (
    <ProjectsProvider>
      <Sidebar />
      <main className="app-content">
        <Outlet />
      </main>
    </ProjectsProvider>
  )
}

function Loading({ error }: { error?: string | null }) {
  return <div className="page">{error ? <div className="error-message">{error}</div> : <p className="muted">Загрузка…</p>}</div>
}

// Корень: последний открытый проект, а если проектов нет — создание первого.
function DefaultRoute() {
  const { data: projects, error } = useProjects()
  if (!projects) return <Loading error={error} />
  const project = lastProject(projects)
  return <Navigate to={project ? `/projects/${project.id}/tasks` : '/new-project'} replace />
}

// Страницы проекта — под /projects/{id}/…: проект берётся из адреса (вкладку десктопа можно переоткрыть по адресу).
// Недоступный или удалённый проект — на проект по умолчанию.
function ProjectRoute() {
  const { projectId } = useParams()
  const { data: projects, error } = useProjects()
  const valid = !!projects?.some((p) => p.id === projectId)

  useEffect(() => {
    if (valid) rememberProject(projectId!)
  }, [valid, projectId])

  if (!projects) return <Loading error={error} />
  if (!valid) return <Navigate to="/" replace />
  return <Outlet />
}

function App() {
  const signedIn = useSession() !== null

  // Имя, почта или права могли измениться с прошлого входа — берём пользователя с сервера.
  useEffect(() => {
    if (!isDesktop && signedIn) api<User>('/auth/me').then(updateSessionUser, () => {})
  }, [signedIn])

  // Сервер без входа: экран входа по любому адресу, после входа открывается тот же адрес.
  if (!isDesktop && !signedIn) return <LoginPage />

  return (
    // Десктоп: адрес вкладки — /w/{папка}/…, маршруты — внутри него.
    <BrowserRouter basename={workspaceBase || undefined}>
      <Routes>
        <Route element={<Layout />}>
          <Route index element={<DefaultRoute />} />
          <Route path="profile" element={<ProfilePage />} />
          <Route path="profile/:tab" element={<ProfilePage />} />
          <Route path="new-project" element={<NewProjectPage />} />
          <Route path="projects/:projectId" element={<ProjectRoute />}>
            <Route index element={<Navigate to="tasks" replace />} />
            <Route path="tasks" element={<TasksPage />} />
            <Route path="boards" element={<BoardsPage />} />
            <Route path="settings" element={<Navigate to="statuses" replace />} />
            <Route path="settings/:tab" element={<SettingsPage />} />
          </Route>
          <Route path="*" element={<Navigate to="/" replace />} />
        </Route>
      </Routes>
    </BrowserRouter>
  )
}

export default App
