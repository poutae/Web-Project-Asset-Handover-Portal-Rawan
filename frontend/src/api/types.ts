export type OrgRole = 'Admin' | 'Member' | 'Client'
export type ProjectRole = 'Lead' | 'Contributor' | 'Client'
export type ProjectStatus = 'Active' | 'OnHold' | 'Completed' | 'Archived'
export type MilestoneStatus = 'Planned' | 'InProgress' | 'Done' | 'Blocked'
export type NoteVisibility = 'Internal' | 'Client'
export type DeploymentStatus =
  'Queued' | 'Building' | 'Activating' | 'HealthChecking' | 'Succeeded' | 'Failed' | 'Cancelled'
export type DeploymentTrigger = 'Manual' | 'Redeploy' | 'Rollback'
export type DocumentVisibility = 'Internal' | 'Client'
export type InvitationStatus = 'Pending' | 'Accepted' | 'Revoked' | 'Expired'

export interface Organization {
  id: string
  name: string
  slug: string
}

export interface Me {
  userId: string
  email: string
  displayName: string
  role: OrgRole
  organization: Organization
}

export interface Project {
  id: string
  name: string
  description: string
  status: ProjectStatus
  createdAt: string
  myRole: ProjectRole | null
  version: string
}

export interface ProjectMember {
  userId: string
  displayName: string
  email: string
  role: ProjectRole
  version: string
}

export interface OrgMember {
  id: string
  email: string
  displayName: string
  role: OrgRole
}

export interface Milestone {
  id: string
  projectId: string
  title: string
  description: string
  dueDate: string | null
  status: MilestoneStatus
  createdAt: string
  version: string
}

export interface Note {
  id: string
  projectId: string
  authorUserId: string
  authorName: string
  body: string
  visibility: NoteVisibility
  createdAt: string
  updatedAt: string
  version: string
}

export interface PortalDocument {
  id: string
  projectId: string
  title: string
  fileName: string
  contentType: string
  sizeBytes: number
  sha256: string
  visibility: DocumentVisibility
  uploadedByUserId: string
  uploadedByName: string
  createdAt: string
  updatedAt: string
  version: string
}

export interface Invitation {
  id: string
  email: string
  role: OrgRole
  status: InvitationStatus
  createdAt: string
  expiresAt: string
}

export interface CreatedInvitation {
  id: string
  email: string
  role: OrgRole
  expiresAt: string
  token: string
}

export interface RealtimeChange {
  kind: 'project' | 'member' | 'milestone' | 'note' | 'document' | 'environment' | 'deployment'
  action: 'created' | 'updated' | 'deleted'
  projectId: string
  entityId: string
  version: string | null
  originClientId: string | null
  at: string
}

export interface DeploymentEnvironment {
  id: string
  name: string
  siteLabel: string
  url: string | null
  repositoryUrl: string | null
  branch: string | null
  buildCommand: string | null
  outputDirectory: string | null
  healthPath: string
  spaFallback: boolean
  hasAccessToken: boolean
  currentDeploymentId: string | null
  version: string
}

export interface Deployment {
  id: string
  environmentId: string
  status: DeploymentStatus
  trigger: DeploymentTrigger
  ref: string
  commitSha: string | null
  sourceDeploymentId: string | null
  requestedByName: string
  createdAt: string
  startedAt: string | null
  finishedAt: string | null
  failureReason: string | null
  revertedToPrevious: boolean
  isCurrent: boolean
  canRollback: boolean
}

export interface DeploymentLogLine {
  id: number
  at: string
  channel: 'System' | 'Stdout' | 'Stderr'
  message: string
}

export interface DeploymentLogs {
  lines: DeploymentLogLine[]
  next: number
  complete: boolean
}
