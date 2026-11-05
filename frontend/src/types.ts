export type Page<T> = { totalCount: number; offset: number; limit: number; data: T[] }

export type Project = { id: string; name: string; createdAt: string; version: string }
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

export type ColumnFieldCondition = { fieldId: string; operator: string; value?: string | null }

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

export type LockKind = 'project' | 'task' | 'taskType' | 'status' | 'statusSet' | 'board' | 'series'

export type EditLock = { entity: LockKind; id: string; holder: string; mine: boolean; acquiredAt: string; expiresAt: string }

export type LockIndex = {
  byOther: (entity: LockKind, id: string) => EditLock | undefined
}

export type ProjectSettings = { statuses: Status[]; statusSets: StatusSet[]; taskTypes: TaskType[] }

export type SettingsSectionProps = { base: string; settings: ProjectSettings; reload: () => void; locks: LockIndex }

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
