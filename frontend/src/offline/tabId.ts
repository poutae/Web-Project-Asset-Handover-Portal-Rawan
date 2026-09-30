const STORAGE_KEY = 'portal.tabId'
const LOCK_PREFIX = 'portal-outbox-tab:'

let current: string | null = null

export function lockNameFor(tabId: string): string {
  return `${LOCK_PREFIX}${tabId}`
}

function newId(): string {
  return crypto.randomUUID()
}

function readStored(): string | null {
  try {
    return sessionStorage.getItem(STORAGE_KEY)
  } catch {
    return null
  }
}

function store(id: string): void {
  try {
    sessionStorage.setItem(STORAGE_KEY, id)
  } catch {
    // sessionStorage can be unavailable (private mode); the id then lasts for this page load only.
  }
}

/** Resolves true if this page now holds the lock for `name` (kept until the page closes). */
function acquire(name: string): Promise<boolean> {
  const locks = typeof navigator === 'undefined' ? undefined : navigator.locks
  if (!locks) return Promise.resolve(true)
  return new Promise((resolve) => {
    void locks.request(name, { ifAvailable: true }, (lock) => {
      resolve(lock !== null)
      // Holding the lock forever keeps it taken until this tab closes, which is how other tabs
      // tell a live tab from a dead one.
      return lock ? new Promise<void>(() => {}) : undefined
    })
  })
}

const LOCK_RETRY_WINDOW_MS = 1500
const LOCK_RETRY_STEP_MS = 50

/**
 * Like <c>acquire</c>, but tolerant of a page reload: the previous page of this very tab may still be
 * releasing its lock for a moment. A genuine duplicate tab holds the lock for good, so waiting a short
 * while only ever delays that rare case.
 */
async function acquireAfterReload(name: string): Promise<boolean> {
  const deadline = Date.now() + LOCK_RETRY_WINDOW_MS
  for (;;) {
    if (await acquire(name)) return true
    if (Date.now() >= deadline) return false
    await new Promise((resolve) => setTimeout(resolve, LOCK_RETRY_STEP_MS))
  }
}

/**
 * Gives this tab its identity. The id survives reloads (sessionStorage), but a duplicated tab copies
 * sessionStorage, so the id is only kept if no other live tab already holds its lock.
 */
export async function initTabIdentity(): Promise<string> {
  const stored = readStored()
  let id = stored ?? newId()
  const kept = stored ? await acquireAfterReload(lockNameFor(id)) : await acquire(lockNameFor(id))
  if (!kept) {
    id = newId()
    await acquire(lockNameFor(id))
  }
  store(id)
  current = id
  return id
}

export function getTabId(): string {
  if (!current) current = readStored() ?? newId()
  return current
}
