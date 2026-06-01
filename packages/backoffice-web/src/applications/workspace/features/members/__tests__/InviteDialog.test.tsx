import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest'
import { render, screen, cleanup, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'

// Mock the generated mutation hook + invalidation key.
type Mutate = (vars: unknown, opts?: { onSuccess?: (resp: unknown) => void; onError?: (err: unknown) => void }) => void
const mutateMock = vi.fn<Mutate>()
const isPendingRef = { current: false }

vi.mock('../../../../../api/queries-commands', async () => {
  const actual = await vi.importActual<typeof import('../../../../../api/queries-commands')>(
    '../../../../../api/queries-commands',
  )
  return {
    ...actual,
    usePostApiWorkspacesSlugInvites: () => ({
      mutate: mutateMock,
      get isPending() {
        return isPendingRef.current
      },
    }),
    getGetApiWorkspacesSlugInvitesQueryKey: (slug: string) => [`/api/workspaces/${slug}/invites`],
  }
})

// Mock notification context to a no-op stub so we don't need a provider.
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

// Stand up a real QueryClient so the dialog's useQueryClient() works.
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { InviteDialog } from '../components/InviteDialog'

function renderDialog(props?: Partial<React.ComponentProps<typeof InviteDialog>>) {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  const onClose = vi.fn()
  const utils = render(
    <QueryClientProvider client={queryClient}>
      <InviteDialog
        open
        slug="my-workspace"
        canInviteOwner
        onClose={onClose}
        {...props}
      />
    </QueryClientProvider>,
  )
  return { ...utils, onClose, queryClient }
}

beforeEach(() => {
  mutateMock.mockReset()
  showSuccess.mockReset()
  showError.mockReset()
  isPendingRef.current = false
})

afterEach(() => {
  cleanup()
})

describe('InviteDialog', () => {
  it('disables the submit button when the form is empty', () => {
    renderDialog()
    const submit = screen.getByRole('button', { name: /send invite/i })
    expect(submit).toBeDisabled()
  })

  it('shows a validation error for an invalid email', async () => {
    const user = userEvent.setup()
    renderDialog()

    const emailInput = screen.getByLabelText(/email/i)
    await user.type(emailInput, 'not-an-email')
    // Trigger blur to mark touched.
    await user.tab()

    expect(screen.getByText(/enter a valid email address/i)).toBeInTheDocument()
    expect(screen.getByRole('button', { name: /send invite/i })).toBeDisabled()
  })

  it('calls the mutation with the right payload on a valid submit', async () => {
    const user = userEvent.setup()
    renderDialog()

    await user.type(screen.getByLabelText(/email/i), 'new@example.com')

    // Open the role select and pick Admin.
    const roleSelect = screen.getByLabelText(/role/i)
    await user.click(roleSelect)
    const listbox = await screen.findByRole('listbox')
    await user.click(within(listbox).getByText('Admin'))

    const submit = screen.getByRole('button', { name: /send invite/i })
    expect(submit).not.toBeDisabled()
    await user.click(submit)

    expect(mutateMock).toHaveBeenCalledTimes(1)
    const [vars] = mutateMock.mock.calls[0]
    expect(vars).toEqual({
      slug: 'my-workspace',
      data: { email: 'new@example.com', role: 'Admin' },
    })
  })

  it('hides the Owner option when the viewer cannot invite owners', async () => {
    const user = userEvent.setup()
    renderDialog({ canInviteOwner: false })

    await user.click(screen.getByLabelText(/role/i))
    const listbox = await screen.findByRole('listbox')
    expect(within(listbox).queryByText('Owner')).not.toBeInTheDocument()
    expect(within(listbox).getByText('Admin')).toBeInTheDocument()
    expect(within(listbox).getByText('Member')).toBeInTheDocument()
  })

  it('surfaces the server ProblemDetails.detail as an inline error on failure', async () => {
    const user = userEvent.setup()
    renderDialog()

    mutateMock.mockImplementation((_vars, opts) => {
      opts?.onError?.({
        response: { data: { detail: 'User is already a member.' } },
      })
    })

    await user.type(screen.getByLabelText(/email/i), 'dup@example.com')
    await user.click(screen.getByRole('button', { name: /send invite/i }))

    expect(await screen.findByText(/user is already a member\./i)).toBeInTheDocument()
  })
})
