import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest'
import { render, screen, cleanup } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter, Route, Routes } from 'react-router-dom'

// Mock generated query hook before importing the context.
const useGetApiMeWorkspacesMock = vi.fn()
vi.mock('../../../api/queries-commands', () => ({
  useGetApiMeWorkspaces: (...args: unknown[]) => useGetApiMeWorkspacesMock(...args),
}))

// Mock react-router-dom's useNavigate so we can assert navigation targets.
const navigateMock = vi.fn()
vi.mock('react-router-dom', async () => {
  const actual = await vi.importActual<typeof import('react-router-dom')>('react-router-dom')
  return {
    ...actual,
    useNavigate: () => navigateMock,
  }
})

import { useWorkspace, WorkspaceProvider, type MyWorkspaceItem } from './WorkspaceContext'

const sampleWorkspaces: MyWorkspaceItem[] = [
  {
    id: '11111111-1111-1111-1111-111111111111',
    slug: 'foo',
    name: 'Foo Workspace',
    role: 'Owner',
    isOwner: true,
    createdAt: '2026-01-01T00:00:00Z',
  },
  {
    id: '22222222-2222-2222-2222-222222222222',
    slug: 'bar',
    name: 'Bar Workspace',
    role: 'Member',
    isOwner: false,
    createdAt: '2026-02-01T00:00:00Z',
  },
]

function Probe() {
  const ws = useWorkspace()
  return (
    <div>
      <div data-testid="current-slug">{ws.currentSlug ?? 'null'}</div>
      <div data-testid="current-workspace-name">{ws.currentWorkspace?.name ?? 'null'}</div>
      <div data-testid="workspaces-count">{ws.workspaces.length}</div>
      <div data-testid="loading">{ws.isLoading ? 'true' : 'false'}</div>
      <button onClick={() => ws.switchWorkspace('bar')}>switch-to-bar</button>
    </div>
  )
}

function renderAt(initialPath: string) {
  return render(
    <MemoryRouter initialEntries={[initialPath]}>
      <Routes>
        <Route
          path="/w/:slug/*"
          element={
            <WorkspaceProvider>
              <Probe />
            </WorkspaceProvider>
          }
        />
        <Route
          path="/no-slug"
          element={
            <WorkspaceProvider>
              <Probe />
            </WorkspaceProvider>
          }
        />
      </Routes>
    </MemoryRouter>,
  )
}

beforeEach(() => {
  navigateMock.mockReset()
  useGetApiMeWorkspacesMock.mockReset()
  useGetApiMeWorkspacesMock.mockReturnValue({ data: sampleWorkspaces, isLoading: false })
})

afterEach(() => {
  cleanup()
})

describe('WorkspaceContext', () => {
  it('returns null currentSlug and currentWorkspace when no :slug in URL', () => {
    renderAt('/no-slug')
    expect(screen.getByTestId('current-slug').textContent).toBe('null')
    expect(screen.getByTestId('current-workspace-name').textContent).toBe('null')
  })

  it('resolves currentWorkspace from URL :slug when it matches a fetched workspace', () => {
    renderAt('/w/foo')
    expect(screen.getByTestId('current-slug').textContent).toBe('foo')
    expect(screen.getByTestId('current-workspace-name').textContent).toBe('Foo Workspace')
    expect(screen.getByTestId('workspaces-count').textContent).toBe('2')
  })

  it('returns null currentWorkspace when slug does not match any fetched workspace', () => {
    renderAt('/w/unknown')
    expect(screen.getByTestId('current-slug').textContent).toBe('unknown')
    expect(screen.getByTestId('current-workspace-name').textContent).toBe('null')
  })

  it('switchWorkspace preserves the subpath when navigating between workspaces', async () => {
    const user = userEvent.setup()
    renderAt('/w/foo/members')
    await user.click(screen.getByText('switch-to-bar'))
    expect(navigateMock).toHaveBeenCalledTimes(1)
    expect(navigateMock).toHaveBeenCalledWith('/w/bar/members')
  })

  it('switchWorkspace defaults to /w/:slug when there is no subpath', async () => {
    const user = userEvent.setup()
    renderAt('/w/foo')
    await user.click(screen.getByText('switch-to-bar'))
    expect(navigateMock).toHaveBeenCalledWith('/w/bar')
  })

  it('throws when useWorkspace is used outside the provider', () => {
    function Outside() {
      useWorkspace()
      return null
    }
    // Suppress React's expected error console output for cleaner test output.
    const errorSpy = vi.spyOn(console, 'error').mockImplementation(() => {})
    expect(() => render(<Outside />)).toThrow(/useWorkspace must be used within a WorkspaceProvider/)
    errorSpy.mockRestore()
  })

  it('forwards isLoading from the generated query hook', () => {
    useGetApiMeWorkspacesMock.mockReturnValue({ data: undefined, isLoading: true })
    renderAt('/w/foo')
    expect(screen.getByTestId('loading').textContent).toBe('true')
    expect(screen.getByTestId('workspaces-count').textContent).toBe('0')
  })
})
