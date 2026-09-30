import path from 'node:path'
import { fileURLToPath } from 'node:url'
import { defineConfig, devices } from '@playwright/test'

const here = path.dirname(fileURLToPath(import.meta.url))
const port = Number(process.env.E2E_PORT ?? 5080)
const baseURL = `http://localhost:${port}`

// The API runs the real production build of the frontend against a real SQL Server database that is
// created fresh (via the EF migrations) for this run. E2E_SQL_SERVER is a connection string WITHOUT a
// database name; the database name is generated.
const sqlServer =
  process.env.E2E_SQL_SERVER ??
  'Server=localhost;Integrated Security=true;TrustServerCertificate=true'
const database = process.env.E2E_DATABASE ?? `PortalE2E_${Date.now()}`

export default defineConfig({
  testDir: '.',
  testMatch: '*.spec.ts',
  // The realtime test asserts a one second budget, so tests run one at a time on an idle machine.
  workers: 1,
  fullyParallel: false,
  retries: 0,
  timeout: 90_000,
  expect: { timeout: 10_000 },
  reporter: process.env.CI ? [['list'], ['html', { open: 'never' }]] : [['list']],
  use: {
    baseURL,
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure',
    serviceWorkers: 'allow',
  },
  projects: [{ name: 'chromium', use: { ...devices['Desktop Chrome'] } }],
  webServer: {
    command: `dotnet run --no-build -c Release --no-launch-profile --project ${path.join(here, '..', 'backend', 'src', 'Portal.Api')}`,
    url: `${baseURL}/api/health/ready`,
    reuseExistingServer: !process.env.CI,
    timeout: 180_000,
    stdout: 'pipe',
    stderr: 'pipe',
    env: {
      ASPNETCORE_ENVIRONMENT: 'Testing',
      ASPNETCORE_URLS: baseURL,
      ConnectionStrings__Default: `${sqlServer};Database=${database}`,
      Database__MigrateOnStartup: 'true',
      Frontend__DistPath: path.join(here, '..', 'frontend', 'dist'),
      RateLimit__AuthPerMinute: '100000',
      Logging__LogLevel__Default: 'Warning',
    },
  },
})
