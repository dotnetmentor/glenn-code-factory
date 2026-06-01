import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest'
import { render, screen, cleanup } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter } from 'react-router-dom'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'

import type {
  SystemSettingCategoryDto,
  SystemSettingDto,
} from '../../../../../api/queries-commands'

// ---- Mocks ---------------------------------------------------------------

const useGetSettingsMock = vi.fn()
const useGetCategoriesMock = vi.fn()
const putSettingMutateAsync = vi.fn()

vi.mock('../../../../../api/queries-commands', async () => {
  const actual = await vi.importActual<
    typeof import('../../../../../api/queries-commands')
  >('../../../../../api/queries-commands')
  return {
    ...actual,
    useGetApiSystemSettings: (...args: unknown[]) => useGetSettingsMock(...args),
    useGetApiSystemSettingsCategories: (...args: unknown[]) =>
      useGetCategoriesMock(...args),
    usePutApiSystemSettingsKey: () => ({
      mutateAsync: putSettingMutateAsync,
      isPending: false,
    }),
    getGetApiSystemSettingsQueryKey: () => ['/api/system-settings'],
  }
})

const showSuccess = vi.fn()
const showError = vi.fn()
vi.mock('../../../../shared/contexts/NotificationContext', () => ({
  useNotification: () => ({
    showSuccess,
    showError,
    showWarning: vi.fn(),
    showInfo: vi.fn(),
    close: vi.fn(),
  }),
}))

import { SystemSettingsPage } from '../routes/SystemSettingsPage'

// ---- Fixtures ------------------------------------------------------------

const githubCategory: SystemSettingCategoryDto = {
  key: 'GitHub',
  displayName: 'GitHub',
  description: 'GitHub integration configuration',
  settings: [
    {
      key: 'GitHub:AppId',
      displayName: 'App ID',
      description: 'Numeric ID of the GitHub App.',
      isSecret: false,
    },
    {
      key: 'GitHub:ClientSecret',
      displayName: 'Client Secret',
      description: 'OAuth client secret.',
      isSecret: true,
    },
  ],
}

const appIdRow: SystemSettingDto = {
  key: 'GitHub:AppId',
  category: 'GitHub',
  displayName: 'App ID',
  description: 'Numeric ID of the GitHub App.',
  isSecret: false,
  hasValue: true,
  value: '1234567',
  updatedAt: '2026-05-01T12:00:00Z',
}

const clientSecretRow: SystemSettingDto = {
  key: 'GitHub:ClientSecret',
  category: 'GitHub',
  displayName: 'Client Secret',
  description: 'OAuth client secret.',
  isSecret: true,
  hasValue: true,
  value: null,
  updatedAt: '2026-05-01T12:00:00Z',
}

function renderPage() {
  useGetCategoriesMock.mockReturnValue({
    data: [githubCategory],
    isLoading: false,
    error: null,
  })
  useGetSettingsMock.mockReturnValue({
    data: [appIdRow, clientSecretRow],
    isLoading: false,
    error: null,
  })

  const queryClient = new QueryClient({
    defaultOptions: { queries: { retry: false } },
  })
  return render(
    <QueryClientProvider client={queryClient}>
      <MemoryRouter initialEntries={['/super-admin/system-settings']}>
        <SystemSettingsPage />
      </MemoryRouter>
    </QueryClientProvider>,
  )
}

beforeEach(() => {
  useGetSettingsMock.mockReset()
  useGetCategoriesMock.mockReset()
  putSettingMutateAsync.mockReset()
  putSettingMutateAsync.mockResolvedValue(undefined)
  showSuccess.mockReset()
  showError.mockReset()
})

afterEach(() => {
  cleanup()
})

// ---- Tests ---------------------------------------------------------------

describe('SystemSettingsPage', () => {
  it('renders the page header and the GitHub tab', () => {
    renderPage()

    expect(
      screen.getByRole('heading', { level: 1, name: /system settings/i }),
    ).toBeInTheDocument()
    expect(screen.getByRole('tab', { name: 'GitHub' })).toBeInTheDocument()
  })

  it('renders both fields with the right modes (non-secret value visible, secret shows Set chip)', () => {
    renderPage()

    // Non-secret: text input pre-filled with current value.
    const appIdInput = screen.getByDisplayValue('1234567') as HTMLInputElement
    expect(appIdInput).toBeInTheDocument()

    // Secret: shows "Set" chip and a "Replace value" toggle.
    expect(screen.getByText('Set')).toBeInTheDocument()
    expect(
      screen.getByRole('button', { name: /replace value/i }),
    ).toBeInTheDocument()
  })

  it('reveals the secret input when "Replace value" is clicked', async () => {
    renderPage()
    const user = userEvent.setup()

    // Secret input not present until toggled — search by placeholder text
    // since MUI wraps the password input in a label that some queries flag
    // as "non-labellable" in happy-dom.
    expect(screen.queryByPlaceholderText(/enter new value/i)).not.toBeInTheDocument()

    await user.click(screen.getByRole('button', { name: /replace value/i }))

    expect(screen.getByPlaceholderText(/enter new value/i)).toBeInTheDocument()
  })

  it('saves a non-secret with the typed value', async () => {
    renderPage()
    const user = userEvent.setup()

    const appIdInput = screen.getByDisplayValue('1234567') as HTMLInputElement
    await user.clear(appIdInput)
    await user.type(appIdInput, '999')

    // The non-secret save button sits in the App ID Paper. It's the
    // "Save" button that is enabled (the secret one is hidden until toggle).
    const saveButtons = screen.getAllByRole('button', { name: /^save$/i })
    // First save button corresponds to the App ID field (rendered first).
    await user.click(saveButtons[0])

    expect(putSettingMutateAsync).toHaveBeenCalledTimes(1)
    expect(putSettingMutateAsync).toHaveBeenCalledWith({
      key: 'GitHub:AppId',
      data: { value: '999' },
    })
  })

  it('submits empty for a secret in replace mode (backend treats as keep-existing)', async () => {
    renderPage()
    const user = userEvent.setup()

    await user.click(screen.getByRole('button', { name: /replace value/i }))

    // Save without typing anything in the secret input.
    const saveButtons = screen.getAllByRole('button', { name: /^save$/i })
    // After toggling replace mode, a "Save" button appears inside the secret
    // panel. There may be only one visible enabled save button (App ID is
    // not dirty), so the last one is the secret's save.
    await user.click(saveButtons[saveButtons.length - 1])

    expect(putSettingMutateAsync).toHaveBeenCalledTimes(1)
    expect(putSettingMutateAsync).toHaveBeenCalledWith({
      key: 'GitHub:ClientSecret',
      data: { value: '' },
    })
  })
})
