import { useState, type FormEvent } from 'react'
import { useNavigate } from 'react-router'
import { api } from '../api'
import { useProjects } from '../projects'
import type { Project } from '../types'
import '../styles/forms.css'

function NewProjectPage() {
  const { data: projects, mutate } = useProjects()
  const navigate = useNavigate()
  const [name, setName] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  const create = async (e: FormEvent) => {
    e.preventDefault()
    setBusy(true)
    setError(null)
    try {
      const project = await api<Project>('/projects', { method: 'POST', body: { name } })
      mutate((list) => [...list, project])
      // Новый проект пустой: без статусов и типов задач задачу не создать — сразу в настройки.
      navigate(`/projects/${project.id}/settings`)
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err))
      setBusy(false)
    }
  }

  return (
    <div className="page">
      <h1 className="page-title">Новый проект</h1>
      {projects?.length === 0 && <p className="muted">Проектов пока нет — создайте первый.</p>}
      <form className="card form" onSubmit={create}>
        <div className="form-group">
          <label htmlFor="project-name">Название</label>
          <input id="project-name" value={name} onChange={(e) => setName(e.target.value)} required autoFocus />
        </div>
        {error && <div className="error-message">{error}</div>}
        <button className="button" type="submit" disabled={busy || !name.trim()}>
          Создать
        </button>
      </form>
    </div>
  )
}

export default NewProjectPage
