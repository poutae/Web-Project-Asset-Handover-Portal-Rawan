import { expect, test, type Browser, type BrowserContext, type Page } from '@playwright/test'

export const PASSWORD = 'correct horse battery staple'

/** A fresh browser profile: its own cookies, IndexedDB, and service worker, shared by the tabs opened in it. */
export function newProfile(browser: Browser): Promise<BrowserContext> {
  return browser.newContext({
    baseURL: test.info().project.use.baseURL,
    serviceWorkers: 'allow',
  })
}

let counter = 0
const unique = () =>
  `${Date.now().toString(36)}${(counter++).toString(36)}${Math.random().toString(36).slice(2, 6)}`

/** Registers a brand-new agency workspace through the UI and lands on the projects page. */
export async function registerOrganization(page: Page, label: string): Promise<{ email: string }> {
  const id = unique()
  const email = `e2e-${id}@example.test`
  await page.goto('/register')
  await page.getByLabel('Agency name').fill(`E2E ${label} ${id}`)
  await page.getByLabel('Your name').fill('E2E Admin')
  await page.getByLabel('Email').fill(email)
  await page.getByLabel('Password (12+ characters)').fill(PASSWORD)
  await page.getByRole('button', { name: 'Create workspace' }).click()
  await expect(page.getByRole('heading', { name: 'Projects' })).toBeVisible()
  return { email }
}

/** Creates a project through the UI, opens it, and returns its id. */
export async function createProject(page: Page, name: string): Promise<string> {
  await page.getByLabel('Project name').fill(name)
  await page.getByRole('button', { name: 'Create project' }).click()
  await page.getByRole('link', { name }).click()
  await expect(page.getByRole('heading', { name })).toBeVisible()
  const match = /\/projects\/([0-9a-f-]{36})/.exec(page.url())
  if (!match?.[1]) throw new Error(`Could not read the project id from ${page.url()}`)
  return match[1]
}

export async function openTab(page: Page, tab: 'Milestones' | 'Notes' | 'Members'): Promise<void> {
  await page.getByRole('tab', { name: tab }).click()
  await expect(page.getByRole('tab', { name: tab })).toHaveAttribute('aria-selected', 'true')
}

export async function addNote(page: Page, text: string): Promise<void> {
  await page.getByLabel('New note').fill(text)
  await page.getByRole('button', { name: 'Add note' }).click()
}

export async function expectRealtimeConnected(page: Page): Promise<void> {
  await expect(page.getByTestId('realtime-status')).toHaveAttribute('data-state', 'connected')
}

export async function expectPending(page: Page, count: number, timeout = 20_000): Promise<void> {
  await expect(page.getByTestId('sync-status')).toHaveAttribute('data-pending', String(count), {
    timeout,
  })
}

/**
 * Waits until the service worker controls the page and has cached the app shell, so that going offline
 * (and reloading) still works. Without this, "offline" would just be a broken page.
 */
export async function waitForOfflineReady(page: Page): Promise<void> {
  await page.waitForFunction(async () => {
    if (!navigator.serviceWorker.controller) return false
    if (!(await caches.has('portal-shell-v1'))) return false
    const cache = await caches.open('portal-shell-v1')
    return (await cache.keys()).length > 1
  })
}

export async function tabIdOf(page: Page): Promise<string> {
  return page.evaluate(() => sessionStorage.getItem('portal.tabId') ?? '')
}

interface StoredEntry {
  ownerTabId: string
  label: string
  status: string
  method: string
}

/** Reads the raw outbox straight out of IndexedDB, bypassing the app. */
export async function readOutbox(page: Page): Promise<StoredEntry[]> {
  return page.evaluate(
    () =>
      new Promise<StoredEntry[]>((resolve, reject) => {
        const open = indexedDB.open('portal-offline')
        open.onerror = () => reject(open.error)
        open.onsuccess = () => {
          const db = open.result
          const request = db.transaction('outbox').objectStore('outbox').getAll()
          request.onerror = () => reject(request.error)
          request.onsuccess = () => {
            db.close()
            resolve(request.result as StoredEntry[])
          }
        }
      }),
  )
}

interface ApiNote {
  body: string
  createdAt: string
}

/** Server-side truth: notes in the order they were created. */
export async function serverNotes(context: BrowserContext, projectId: string): Promise<string[]> {
  const response = await context.request.get(`/api/projects/${projectId}/notes`)
  expect(response.ok()).toBe(true)
  const notes = (await response.json()) as ApiNote[]
  return notes.sort((a, b) => a.createdAt.localeCompare(b.createdAt)).map((n) => n.body)
}
