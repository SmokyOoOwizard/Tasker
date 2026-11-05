import { useParams } from 'react-router'
import { LockBadge } from '../components/LockBadge'
import { useT } from '../i18n'
import { useLoad } from '../hooks/useLoad'
import { useLocks } from '../hooks/useLocks'
import { loadBoards, loadStatusSets } from '../reads'
import '../styles/settings.css'

function BoardsPage() {
  const t = useT()
  const { projectId } = useParams()
  const locks = useLocks(projectId!)
  const { data, error } = useLoad(async () => {
    const [boards, statusSets] = await Promise.all([loadBoards(projectId!), loadStatusSets(projectId!)])
    return { boards, statusSets }
  }, [projectId])

  const setById = new Map(data?.statusSets.map((s) => [s.id, s]))

  return (
    <div className="page page-wide">
      <div className="page-header">
        <h1 className="page-title">{t('web.boards.title')}</h1>
        {data && <span className="muted">{data.boards.length}</span>}
      </div>

      {error && !data && <div className="error-message">{error}</div>}
      {!data && !error && <p className="muted">{t('web.common.loading')}</p>}
      {data?.boards.length === 0 && (
        <div className="card">
          <p>{t('web.boards.empty')}</p>
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
                  title={column.fieldConditions.length > 0 ? t('web.boards.conditions', column.fieldConditions.length) : undefined}
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
