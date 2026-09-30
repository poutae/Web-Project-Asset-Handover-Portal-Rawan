export const keys = {
  me: ['me'] as const,
  projects: ['projects'] as const,
  project: (id: string) => ['project', id] as const,
  members: (id: string) => ['members', id] as const,
  milestones: (id: string) => ['milestones', id] as const,
  notes: (id: string) => ['notes', id] as const,
  documents: (id: string) => ['documents', id] as const,
  orgMembers: ['org', 'members'] as const,
  invitations: ['org', 'invitations'] as const,
}
