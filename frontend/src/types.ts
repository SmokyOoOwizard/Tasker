// Сущности API (см. Tasker.Core). version — непрозрачная строка: клиент только передаёт её обратно в PATCH/DELETE.

export type Page<T> = { totalCount: number; offset: number; limit: number; data: T[] }

export type Project = { id: string; name: string; createdAt: string; version: string }
/** Сколько чего в проекте (GET /projects/{id}/stats). */
export type ProjectStats = {
  tasks: number
  boards: number
  statuses: number
  statusSets: number
  taskTypes: number
  series: number
  linkTypes: number
}

export type Status = { id: string; projectId: string; name: string; color: string; description: string; version: string }

export type StatusSet = { id: string; projectId: string; name: string; statusIds: string[]; version: string }

export type TaskType = { id: string; projectId: string; name: string; description: string; statusSetId: string; version: string }

export type Task = {
  id: string
  projectId: string
  title: string
  description: string | null
  typeId: string
  statusId: string
  createdAt: string
  updatedAt: string
  version: string
}

/** Условие колонки по полю каталога: по id поля и значения (у enum — id значения); оператор — слово (equal, notEqual, greater, set, unset, attached…). */
export type ColumnFieldCondition = { fieldId: string; operator: string; value?: string | null }

/** Задача попадает в колонку, если её статус из statusIds И выполнены все fieldConditions (пусто — условий нет). */
export type BoardColumn = {
  id: string
  name: string
  statusIds: string[]
  dropStatuses: Record<string, string>
  fieldConditions: ColumnFieldCondition[]
}

export type Board = {
  id: string
  projectId: string
  name: string
  statusSetIds: string[]
  columns: BoardColumn[]
  version: string
}

/** Какие сущности блокируются на время правки (см. Tasker.Core.Locks); совпадает с видом сущности в событиях. */
export type LockKind = 'project' | 'task' | 'taskType' | 'status' | 'statusSet' | 'board' | 'series'

/** Блокировка на время правки: holder — кто правит, mine — это вы. Для проекта id — id самого проекта. */
export type EditLock = { entity: LockKind; id: string; holder: string; mine: boolean; acquiredAt: string; expiresAt: string }

/** Кто сейчас правит сущности проекта — для плашек «правит Иван» в списках. */
export type LockIndex = {
  /** Блокировка сущности другим человеком; своя и отсутствующая — undefined. */
  byOther: (entity: LockKind, id: string) => EditLock | undefined
}

/** Настройки проекта, которые экран настроек грузит вместе. */
export type ProjectSettings = { statuses: Status[]; statusSets: StatusSet[]; taskTypes: TaskType[] }

/** Пропсы раздела настроек: base — /projects/{id}, reload — перечитать все настройки. */
export type SettingsSectionProps = { base: string; settings: ProjectSettings; reload: () => void; locks: LockIndex }

/** Токен агента для MCP. Само значение приходит только при выпуске. */
export type AgentToken = {
  id: string
  agentId: string
  name: string
  prefix: string
  createdAt: string
  expiresAt: string | null
  lastUsedAt: string | null
  revokedAt: string | null
}
