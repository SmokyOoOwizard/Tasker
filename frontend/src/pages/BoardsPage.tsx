import { useParams } from 'react-router'
import { getAll } from '../api'
import { LockBadge } from '../components/LockBadge'
import { useLoad } from '../hooks/useLoad'
import { useLocks } from '../hooks/useLocks'
import type { Board, StatusSet } from '../types'
import '../styles/settings.css'

/** Все доски проекта. Просмотр доски и её настройка — следующим шагом. */
function BoardsPage() {
  const { projectId } = useParams()
  const base = `/projects/${projectId}`
  const locks = useLocks(base)
  const { data, error } = useLoad(async () => {
    const [boards, statusSets] = await Promise.all([getAll<Board>(`${base}/boards`), getAll<StatusSet>(`${base}/status-sets`)])
    return { boards, statusSets }
  }, [base])

  const setById = new Map(data?.statusSets.map((s) => [s.id, s]))

  return (
    <div className="page page-wide">
      <div className="page-header">
        <h1 className="page-title">Доски</h1>
        {data && <span className="muted">{data.boards.length}</span>}
      </div>

      {error && !data && <div className="error-message">{error}</div>}
      {!data && !error && <p className="muted">Загрузка…</p>}
      {data?.boards.length === 0 && (
        <div className="card">
          <p>Досок пока нет. Создать доску из интерфейса пока нельзя — только через API или агента в MCP.</p>
        </div>
      )}

      <div className="board-grid">
        {data?.boards.map((board) => {
          const other = locks.byOther('board', board.id)
          return (
          <div key={board.id} className="card board-card">
            <h2 className="card-title">
              {board.name} {other && <LockBadge lock={other} />}
            </h2>
            <div className="board-card-columns">
              {board.columns.map((column) => (
                <span
                  key={column.id}
                  className="board-card-column"
                  title={column.fieldConditions.length > 0 ? `Условий по полям: ${column.fieldConditions.length}` : undefined}
                >
                  {column.name}
                </span>
              ))}
            </div>
            <div className="muted board-card-sets">
              {board.statusSetIds.map((id) => setById.get(id)?.name ?? '—').join(', ')}
            </div>
          </div>
          )
        })}
      </div>
    </div>
  )
}

export default BoardsPage
