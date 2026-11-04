import { graphql, graphqlAll } from './api'
import type { User } from './auth'
import type { AgentToken, Board, EditLock, Page, Project, ProjectSettings, ProjectStats, Status, StatusSet, Task, TaskType } from './types'

const projectFields = 'id name createdAt version'
const userFields = 'id username kind ownerId email isAdmin createdAt version'
const statusFields = 'id projectId name color description version'
const statusSetFields = 'id projectId name statusIds version'
const taskTypeFields = 'id projectId name description statusSetId version'
const taskFields = 'id projectId title description typeId statusId createdAt updatedAt version'
const boardFields =
  'id projectId name statusSetIds version columns { id name statusIds dropStatuses { statusSetId statusId } fieldConditions { fieldId operator value } }'
const lockFields = 'entity id holder mine acquiredAt expiresAt'
const tokenFields = 'id agentId name prefix createdAt expiresAt lastUsedAt revokedAt'

/**
 * Десктоп: MCP обслуживает демон tasker-mcpd. Его состояние, общий адрес MCP (null — демон не работает или ещё не открыл эту папку),
 * имя этой папки для агента (аргумент workspace инструментов) и заголовок, которым выбирается агент.
 */
export type LocalMcp = {
  daemon: 'running' | 'starting' | 'stopped' | 'failed'
  port: number | null
  url: string | null
  workspace: string | null
  workspaceState: 'open' | 'opening' | 'failed' | null
  error: string | null
  agentHeader: string
}

type ProjectPage<K extends string, T> = { project: { [key in K]: Page<T> } | null }

function inProject<K extends string, T>(field: K, projectId: string, fields: string): Promise<T[]> {
  return graphqlAll<T, ProjectPage<K, T>>(
    `query($projectId: UUID!, $offset: Int, $limit: Int) { project(id: $projectId) { ${field}(offset: $offset, limit: $limit) { totalCount data { ${fields} } } } }`,
    { projectId },
    (data) => found(data.project, projectId)[field],
  )
}

function found<T>(value: T | null, projectId: string): T {
  if (!value) throw new Error(`Project ${projectId} not found`)
  return value
}

export async function loadMe(): Promise<User> {
  return (await graphql<{ me: User }>(`{ me { ${userFields} } }`)).me
}

export async function loadRegistrationOpen(): Promise<boolean> {
  return (await graphql<{ registration: { open: boolean } }>('{ registration { open } }', {}, { anonymous: true })).registration.open
}

export function loadProjects(): Promise<Project[]> {
  return graphqlAll<Project, { projects: Page<Project> }>(
    `query($offset: Int, $limit: Int) { projects(offset: $offset, limit: $limit) { totalCount data { ${projectFields} } } }`,
    {},
    (data) => data.projects,
  )
}

export async function loadProjectStats(projectId: string): Promise<ProjectStats> {
  const data = await graphql<{ project: { stats: ProjectStats } | null }>(
    'query($projectId: UUID!) { project(id: $projectId) { stats { tasks boards statuses statusSets taskTypes series linkTypes } } }',
    { projectId },
  )
  return found(data.project, projectId).stats
}

export async function loadProjectSettings(projectId: string): Promise<ProjectSettings> {
  const [statuses, statusSets, taskTypes] = await Promise.all([
    inProject<'statuses', Status>('statuses', projectId, statusFields),
    loadStatusSets(projectId),
    inProject<'taskTypes', TaskType>('taskTypes', projectId, taskTypeFields),
  ])
  return { statuses, statusSets, taskTypes }
}

export function loadStatusSets(projectId: string): Promise<StatusSet[]> {
  return inProject<'statusSets', StatusSet>('statusSets', projectId, statusSetFields)
}

export async function loadTasks(projectId: string, offset: number, limit: number): Promise<Page<Task>> {
  const data = await graphql<{ project: { tasks: Page<Task> } | null }>(
    `query($projectId: UUID!, $offset: Int, $limit: Int) { project(id: $projectId) { tasks(offset: $offset, limit: $limit) { totalCount offset limit data { ${taskFields} } } } }`,
    { projectId, offset, limit },
  )
  return found(data.project, projectId).tasks
}

type BoardData = Omit<Board, 'columns'> & {
  columns: (Omit<Board['columns'][number], 'dropStatuses'> & { dropStatuses: { statusSetId: string; statusId: string }[] })[]
}

export async function loadBoards(projectId: string): Promise<Board[]> {
  const boards = await inProject<'boards', BoardData>('boards', projectId, boardFields)
  return boards.map((board) => ({
    ...board,
    columns: board.columns.map((column) => ({
      ...column,
      dropStatuses: Object.fromEntries(column.dropStatuses.map((drop) => [drop.statusSetId, drop.statusId])),
    })),
  }))
}

export function loadLocks(projectId: string): Promise<EditLock[]> {
  return inProject<'locks', EditLock>('locks', projectId, lockFields)
}

export function loadMembers(projectId: string): Promise<User[]> {
  return inProject<'members', User>('members', projectId, userFields)
}

export function loadUsers(): Promise<User[]> {
  return graphqlAll<User, { users: Page<User> }>(
    `query($offset: Int, $limit: Int) { users(offset: $offset, limit: $limit) { totalCount data { ${userFields} } } }`,
    {},
    (data) => data.users,
  )
}

export function loadAgents(): Promise<User[]> {
  return graphqlAll<User, { agents: Page<User> }>(
    `query($offset: Int, $limit: Int) { agents(offset: $offset, limit: $limit) { totalCount data { ${userFields} } } }`,
    {},
    (data) => data.agents,
  )
}

export function loadAgentTokens(agentId: string): Promise<AgentToken[]> {
  return graphqlAll<AgentToken, { agentTokens: Page<AgentToken> | null }>(
    `query($agentId: UUID!, $offset: Int, $limit: Int) { agentTokens(agentId: $agentId, offset: $offset, limit: $limit) { totalCount data { ${tokenFields} } } }`,
    { agentId },
    (data) => {
      if (!data.agentTokens) throw new Error(`Agent ${agentId} not found`)
      return data.agentTokens
    },
  )
}

export async function loadWorkspaceMcp(): Promise<LocalMcp> {
  return (
    await graphql<{ workspaceMcp: LocalMcp }>('{ workspaceMcp { daemon port url workspace workspaceState error agentHeader } }')
  ).workspaceMcp
}
