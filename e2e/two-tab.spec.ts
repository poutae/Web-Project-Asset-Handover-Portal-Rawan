import { expect, test, type Page } from '@playwright/test'
import {
  addNote,
  createProject,
  expectPending,
  expectRealtimeConnected,
  newProfile,
  openTab,
  readOutbox,
  registerOrganization,
  serverNotes,
  tabIdOf,
  waitForOfflineReady,
} from './helpers'

/**
 * Four behaviours that only show up with two real browser tabs sharing one signed-in session:
 * realtime sync, offline mutations, concurrent-edit conflicts, and per-tab outbox isolation.
 * Everything runs against the production build, served by the real API on a real SQL Server database.
 */

const REALTIME_BUDGET_MS = 1000

async function openProjectNotes(page: Page, projectId: string) {
  await page.goto(`/projects/${projectId}?tab=notes`)
  await expect(page.getByLabel('New note')).toBeVisible()
  await expectRealtimeConnected(page)
}

test.describe('two tabs, one session', () => {
  test('1. realtime: a change in one tab shows up in the other in under a second', async ({
    browser,
  }) => {
    const context = await newProfile(browser)
    const tabA = await context.newPage()
    await registerOrganization(tabA, 'realtime')
    const projectId = await createProject(tabA, 'Realtime project')
    await openTab(tabA, 'Notes')
    await expectRealtimeConnected(tabA)

    const tabB = await context.newPage()
    await openProjectNotes(tabB, projectId)

    for (const [from, to, label] of [
      [tabA, tabB, 'A to B'],
      [tabB, tabA, 'B to A'],
    ] as const) {
      const text = `Realtime note ${label} ${Date.now()}`
      await from.getByLabel('New note').fill(text)

      const startedAt = Date.now()
      await from.getByRole('button', { name: 'Add note' }).click()
      await expect(to.getByText(text)).toBeVisible({
        timeout: REALTIME_BUDGET_MS,
      })
      const elapsed = Date.now() - startedAt

      expect(elapsed, `${label} took ${elapsed} ms`).toBeLessThan(REALTIME_BUDGET_MS)
    }

    await context.close()
  })

  test('2. offline: changes survive a refresh and a reconnect, and are sent in order', async ({
    browser,
  }) => {
    const context = await newProfile(browser)
    const page = await context.newPage()
    await registerOrganization(page, 'offline')
    const projectId = await createProject(page, 'Offline project')
    await openTab(page, 'Notes')
    await waitForOfflineReady(page)

    await context.setOffline(true)
    await expect(page.getByTestId('offline-banner')).toBeVisible()

    const texts = ['first offline note', 'second offline note', 'third offline note']
    for (const text of texts) await addNote(page, text)

    await expectPending(page, 3)
    await expect(page.getByTestId('sync-badge')).toHaveCount(3)

    // A refresh while still offline: the app shell, the cached data, and the queue must all come back.
    await page.reload()
    await expect(page.getByLabel('New note')).toBeVisible()
    await expect(page.getByTestId('offline-banner')).toBeVisible()
    await expectPending(page, 3)
    for (const text of texts) await expect(page.getByText(text)).toBeVisible()

    // The queue lists the changes in the order they were made.
    await page.getByTestId('sync-status').click()
    const queued = await page
      .getByRole('list', { name: 'Queued changes' })
      .getByRole('listitem')
      .allTextContents()
    expect(queued.map((label) => texts.findIndex((t) => label.includes(t)))).toEqual([0, 1, 2])

    // Nothing reached the server while offline.
    expect(await readOutbox(page)).toHaveLength(3)

    await context.setOffline(false)
    await expectPending(page, 0)
    await expect(page.getByTestId('sync-badge')).toHaveCount(0)

    // The server received all three, in the order they were made, exactly once.
    expect(await serverNotes(context, projectId)).toEqual(texts)
    expect(await readOutbox(page)).toHaveLength(0)

    await context.close()
  })

  test('3. concurrent edits: the second saver gets a conflict dialog and nothing is overwritten', async ({
    browser,
  }) => {
    const context = await newProfile(browser)
    const tabA = await context.newPage()
    await registerOrganization(tabA, 'conflict')
    const projectId = await createProject(tabA, 'Conflict project')
    await openTab(tabA, 'Milestones')
    await tabA.getByLabel('New milestone').fill('Launch plan')
    await tabA.getByRole('button', { name: 'Add milestone' }).click()
    await expect(tabA.getByRole('listitem').filter({ hasText: 'Launch plan' })).toBeVisible()
    await expectPending(tabA, 0)

    const tabB = await context.newPage()
    await tabB.goto(`/projects/${projectId}?tab=milestones`)
    await expect(tabB.getByText('Launch plan')).toBeVisible()
    await expectRealtimeConnected(tabB)
    await expectRealtimeConnected(tabA)

    async function openEditor(page: Page) {
      await page.getByRole('button', { name: /^Edit Launch plan/ }).click()
      await expect(page.getByRole('dialog', { name: 'Edit milestone' })).toBeVisible()
    }
    async function saveTitle(page: Page, title: string) {
      const dialog = page.getByRole('dialog', { name: 'Edit milestone' })
      await dialog.getByLabel('Title').fill(title)
      await dialog.getByRole('button', { name: 'Save milestone' }).click()
    }

    // Both people open the same milestone; the first one saves.
    await openEditor(tabB)
    await openEditor(tabA)
    await saveTitle(tabA, 'Launch plan (A)')
    await expectPending(tabA, 0)
    await expect(tabB.getByText('Launch plan (A)')).toBeVisible() // arrives by realtime, behind B's open dialog

    // B saves from what it saw before A's change: that must conflict instead of overwriting A.
    await saveTitle(tabB, 'Launch plan (B)')
    const conflict = tabB.getByRole('dialog', { name: 'Edit conflict' })
    await expect(conflict).toBeVisible()
    await expect(conflict.getByTestId('mine-title')).toHaveText('Launch plan (B)')
    await expect(conflict.getByTestId('theirs-title')).toHaveText('Launch plan (A)')

    // A's change is still what the server holds.
    const before = (await (
      await context.request.get(`/api/projects/${projectId}/milestones`)
    ).json()) as { title: string }[]
    expect(before.map((m) => m.title)).toEqual(['Launch plan (A)'])

    // Choosing "Use server version" drops B's change.
    await conflict.getByRole('button', { name: 'Use server version' }).click()
    await expect(conflict).toBeHidden()
    await expectPending(tabB, 0)
    await expect(tabB.getByText('Launch plan (A)')).toBeVisible()
    await expect(tabB.getByText('Launch plan (B)')).toHaveCount(0)

    // Second round: this time B keeps its own change on purpose.
    await openEditor(tabB)
    await openEditor(tabA)
    await saveTitle(tabA, 'Launch plan (A2)')
    await expectPending(tabA, 0)
    await expect(tabB.getByText('Launch plan (A2)')).toBeVisible()
    await saveTitle(tabB, 'Launch plan (B2)')
    await expect(tabB.getByRole('dialog', { name: 'Edit conflict' })).toBeVisible()
    await tabB.getByRole('button', { name: 'Keep my change' }).click()
    await expectPending(tabB, 0)

    await expect(tabA.getByText('Launch plan (B2)')).toBeVisible()
    const after = (await (
      await context.request.get(`/api/projects/${projectId}/milestones`)
    ).json()) as { title: string }[]
    expect(after.map((m) => m.title)).toEqual(['Launch plan (B2)'])

    await context.close()
  })

  test('4. isolation: each tab owns and sends only its own queued changes, and a survivor adopts a closed tab’s', async ({
    browser,
  }) => {
    const context = await newProfile(browser)
    const tabA = await context.newPage()
    await registerOrganization(tabA, 'isolation')
    const projectId = await createProject(tabA, 'Isolation project')
    await openTab(tabA, 'Notes')
    await waitForOfflineReady(tabA)

    const tabB = await context.newPage()
    await openProjectNotes(tabB, projectId)
    await expectRealtimeConnected(tabA)

    // Same browser profile, same IndexedDB, but two different tab identities.
    const idA = await tabIdOf(tabA)
    const idB = await tabIdOf(tabB)
    expect(idA).not.toBe('')
    expect(idB).not.toBe('')
    expect(idA).not.toBe(idB)

    await context.setOffline(true)
    await addNote(tabA, 'written in tab A')
    await addNote(tabB, 'written in tab B')
    await expectPending(tabA, 1)
    await expectPending(tabB, 1)

    // The shared database holds both entries, each stamped with its own tab.
    const stored = await readOutbox(tabA)
    expect(stored).toHaveLength(2)
    expect(stored.find((e) => e.ownerTabId === idA)?.label).toContain('written in tab A')
    expect(stored.find((e) => e.ownerTabId === idB)?.label).toContain('written in tab B')

    // Each tab only shows (and can only send) its own.
    await tabA.getByTestId('sync-status').click()
    await expect(
      tabA.getByRole('list', { name: 'Queued changes' }).getByRole('listitem'),
    ).toHaveCount(1)
    await tabB.getByTestId('sync-status').click()
    await expect(
      tabB.getByRole('list', { name: 'Queued changes' }).getByRole('listitem'),
    ).toHaveCount(1)

    // Back online: every tab sends exactly its own change, and nothing twice.
    const postsFrom = (page: Page) => {
      const bodies: string[] = []
      page.on('request', (request) => {
        if (
          request.method() === 'POST' &&
          request.url().endsWith(`/api/projects/${projectId}/notes`)
        ) {
          bodies.push((request.postDataJSON() as { body: string }).body)
        }
      })
      return bodies
    }
    const sentByA = postsFrom(tabA)
    const sentByB = postsFrom(tabB)
    await context.setOffline(false)
    await expectPending(tabA, 0)
    await expectPending(tabB, 0)

    expect(sentByA).toEqual(['written in tab A'])
    expect(sentByB).toEqual(['written in tab B'])
    expect((await serverNotes(context, projectId)).sort()).toEqual([
      'written in tab A',
      'written in tab B',
    ])

    // A tab that closes with unsent changes leaves them behind; the surviving tab adopts them, in order.
    await context.setOffline(true)
    await addNote(tabA, 'orphan one')
    await addNote(tabA, 'orphan two')
    await expectPending(tabA, 2)
    await tabA.close()
    // Still offline, so nothing has been sent; the entries sit in IndexedDB, still stamped with A's id.
    expect((await readOutbox(tabB)).filter((e) => e.ownerTabId === idA)).toHaveLength(2)

    await context.setOffline(false)
    await expect
      .poll(async () => (await serverNotes(context, projectId)).slice(2), {
        timeout: 30_000,
      })
      .toEqual(['orphan one', 'orphan two'])
    expect(await readOutbox(tabB)).toHaveLength(0)

    await context.close()
  })
})
