import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useRef, useState, type FormEvent } from 'react'
import { ApiError, apiGet, apiRequest } from '../api/http'
import { keys } from '../api/queries'
import type { DocumentVisibility, PortalDocument, Project } from '../api/types'
import { useAuth } from '../auth/AuthProvider'
import { SyncBadge } from '../components/SyncBadge'
import {
  Badge,
  Button,
  Card,
  Dialog,
  EmptyState,
  ErrorState,
  LoadingState,
  Select,
  TextInput,
} from '../components/ui'
import { overlayPending, type WithSync } from '../offline/optimistic'
import { useOutbox } from '../offline/OutboxProvider'

/** Mirrors the server's default; the server is the authority and re-checks everything. */
const MAX_UPLOAD_BYTES = 25 * 1024 * 1024
const ALLOWED = [
  '.pdf',
  '.png',
  '.jpg',
  '.jpeg',
  '.gif',
  '.webp',
  '.zip',
  '.docx',
  '.xlsx',
  '.pptx',
  '.txt',
  '.md',
  '.csv',
  '.json',
]

function formatSize(bytes: number): string {
  if (bytes < 1024) return `${bytes} B`
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`
  return `${(bytes / (1024 * 1024)).toFixed(1)} MB`
}

function checkFile(file: File): string | null {
  const dot = file.name.lastIndexOf('.')
  const extension = dot >= 0 ? file.name.slice(dot).toLowerCase() : ''
  if (!ALLOWED.includes(extension))
    return `That file type is not allowed. Allowed: ${ALLOWED.join(', ')}.`
  if (file.size === 0) return 'That file is empty.'
  if (file.size > MAX_UPLOAD_BYTES)
    return `That file is larger than ${MAX_UPLOAD_BYTES / (1024 * 1024)} MB.`
  return null
}

export function DocumentsTab({ project }: { project: Project }) {
  const { me } = useAuth()
  const { entries, online, mutate } = useOutbox()
  const queryClient = useQueryClient()
  const isClient = project.myRole === 'Client'
  const fileInput = useRef<HTMLInputElement>(null)
  const [file, setFile] = useState<File | null>(null)
  const [title, setTitle] = useState('')
  const [visibility, setVisibility] = useState<DocumentVisibility>(isClient ? 'Client' : 'Internal')
  const [fileError, setFileError] = useState<string | null>(null)
  const [editing, setEditing] = useState<PortalDocument | null>(null)

  const base = `/api/projects/${project.id}/documents`
  const invalidate = [[...keys.documents(project.id)]]

  const documents = useQuery({
    queryKey: keys.documents(project.id),
    queryFn: ({ signal }) => apiGet<PortalDocument[]>(base, signal),
  })

  // Uploads are online-only: the file itself must reach the server, and a large file is not queued on disk.
  const upload = useMutation({
    mutationFn: async () => {
      const form = new FormData()
      form.append('file', file as File)
      if (title.trim()) form.append('title', title.trim())
      if (!isClient) form.append('visibility', visibility)
      return apiRequest<PortalDocument>(base, {
        method: 'POST',
        form,
        idempotencyKey: crypto.randomUUID(),
      })
    },
    onSuccess: () => {
      setFile(null)
      setTitle('')
      if (fileInput.current) fileInput.current.value = ''
      void queryClient.invalidateQueries({ queryKey: keys.documents(project.id) })
    },
  })

  const submit = (event: FormEvent) => {
    event.preventDefault()
    if (!file) return
    const problem = checkFile(file)
    setFileError(problem)
    if (!problem) upload.mutate()
  }

  const save = (doc: PortalDocument, next: { title: string; visibility: DocumentVisibility }) =>
    mutate({
      method: 'PUT',
      url: `${base}/${doc.id}`,
      body: next,
      version: doc.version,
      resourceKey: `document:${doc.id}`,
      label: `Edit document “${next.title}”`,
      optimistic: { kind: 'update', list: 'documents', id: doc.id, patch: next },
      invalidate,
    })

  const remove = (doc: PortalDocument) =>
    mutate({
      method: 'DELETE',
      url: `${base}/${doc.id}`,
      version: doc.version,
      resourceKey: `document:${doc.id}`,
      label: `Delete document “${doc.title}”`,
      optimistic: { kind: 'delete', list: 'documents', id: doc.id },
      invalidate,
    })

  if (documents.isPending) return <LoadingState label="Loading documents…" />
  if (documents.isError && !documents.data)
    return <ErrorState title="Could not load documents" onRetry={() => void documents.refetch()} />

  const { existing } = overlayPending(documents.data ?? [], entries, 'documents', (e) =>
    e.url.startsWith(base),
  )
  const all: WithSync<PortalDocument>[] = existing

  const serverMessage =
    upload.error instanceof ApiError
      ? (upload.error.fieldErrors.file?.[0] ?? upload.error.problem?.title ?? upload.error.message)
      : upload.error
        ? 'The upload failed. Check your connection and try again.'
        : null

  return (
    <div className="space-y-4">
      <form onSubmit={submit} className="space-y-2" aria-label="Upload a document">
        <div className="flex flex-wrap items-end gap-2">
          <div className="space-y-1">
            <label htmlFor="document-file" className="block text-sm font-medium">
              File
            </label>
            <input
              id="document-file"
              ref={fileInput}
              type="file"
              accept={ALLOWED.join(',')}
              onChange={(e) => {
                setFile(e.target.files?.[0] ?? null)
                setFileError(null)
                upload.reset()
              }}
              className="block text-sm"
            />
          </div>
          <TextInput
            label="Title (optional)"
            value={title}
            maxLength={200}
            onChange={(e) => setTitle(e.target.value)}
          />
          {!isClient && (
            <Select
              label="Document visibility"
              value={visibility}
              onChange={(e) => setVisibility(e.target.value as DocumentVisibility)}
            >
              <option value="Internal">Internal (team only)</option>
              <option value="Client">Visible to clients</option>
            </Select>
          )}
          <Button type="submit" disabled={!file || upload.isPending || !online}>
            {upload.isPending ? 'Uploading…' : 'Upload document'}
          </Button>
        </div>
        {!online && (
          <p className="text-sm text-amber-700">
            Uploading needs a connection. Other changes are saved and synced later.
          </p>
        )}
        {(fileError ?? serverMessage) && (
          <p role="alert" className="text-sm text-red-600">
            {fileError ?? serverMessage}
          </p>
        )}
      </form>

      {all.length === 0 ? (
        <EmptyState
          title="No documents yet"
          hint={`Upload files such as briefs, logos, and brand assets (up to ${MAX_UPLOAD_BYTES / (1024 * 1024)} MB).`}
        />
      ) : (
        <ul aria-label="Documents" className="space-y-2">
          {all.map((doc) => {
            const canChange = doc.uploadedByUserId === me?.userId || project.myRole === 'Lead'
            return (
              <li key={doc.id}>
                <Card className={doc.sync?.kind === 'delete' ? 'opacity-50' : undefined}>
                  <div className="flex flex-wrap items-center justify-between gap-2">
                    <div className="min-w-0">
                      <p
                        className={`truncate font-medium ${doc.sync?.kind === 'delete' ? 'line-through' : ''}`}
                      >
                        {doc.title}
                      </p>
                      <p className="truncate text-sm text-slate-500">
                        {doc.fileName} · {formatSize(doc.sizeBytes)} · {doc.uploadedByName} ·{' '}
                        {new Date(doc.createdAt).toLocaleDateString()}
                      </p>
                    </div>
                    <div className="flex items-center gap-2">
                      <SyncBadge sync={doc.sync} />
                      {!isClient && (
                        <Badge tone={doc.visibility === 'Client' ? 'green' : 'slate'}>
                          {doc.visibility === 'Client' ? 'Client-visible' : 'Internal'}
                        </Badge>
                      )}
                      <a
                        href={`${base}/${doc.id}/download`}
                        download
                        aria-label={`Download ${doc.title}`}
                        className="rounded-md px-3 py-1.5 text-sm font-medium text-indigo-600 ring-1 ring-slate-300 hover:bg-slate-50 dark:ring-slate-600 dark:hover:bg-slate-800"
                      >
                        Download
                      </a>
                      {canChange && (
                        <>
                          <Button
                            variant="ghost"
                            onClick={() => setEditing(doc)}
                            aria-label={`Edit ${doc.title}`}
                          >
                            Edit
                          </Button>
                          <Button
                            variant="ghost"
                            onClick={() => void remove(doc)}
                            aria-label={`Delete ${doc.title}`}
                          >
                            Delete
                          </Button>
                        </>
                      )}
                    </div>
                  </div>
                </Card>
              </li>
            )
          })}
        </ul>
      )}

      {editing && (
        <EditDocumentDialog
          doc={editing}
          canChooseVisibility={!isClient}
          onClose={() => setEditing(null)}
          onSave={async (next) => {
            await save(editing, next)
            setEditing(null)
          }}
        />
      )}
    </div>
  )
}

function EditDocumentDialog({
  doc,
  canChooseVisibility,
  onClose,
  onSave,
}: {
  doc: PortalDocument
  canChooseVisibility: boolean
  onClose: () => void
  onSave: (next: { title: string; visibility: DocumentVisibility }) => Promise<void>
}) {
  const [title, setTitle] = useState(doc.title)
  const [visibility, setVisibility] = useState(doc.visibility)
  return (
    <Dialog title="Edit document" onClose={onClose}>
      <div className="space-y-3">
        <TextInput
          label="Document title"
          value={title}
          maxLength={200}
          onChange={(e) => setTitle(e.target.value)}
        />
        {canChooseVisibility && (
          <Select
            label="Visibility"
            value={visibility}
            onChange={(e) => setVisibility(e.target.value as DocumentVisibility)}
          >
            <option value="Internal">Internal (team only)</option>
            <option value="Client">Visible to clients</option>
          </Select>
        )}
      </div>
      <div className="mt-5 flex justify-end gap-2">
        <Button variant="secondary" onClick={onClose}>
          Cancel
        </Button>
        <Button
          disabled={!title.trim()}
          onClick={() => void onSave({ title: title.trim(), visibility })}
        >
          Save document
        </Button>
      </div>
    </Dialog>
  )
}
