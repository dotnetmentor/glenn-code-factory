import { describe, it, expect, vi, afterEach } from 'vitest'
import { render, screen, cleanup } from '@testing-library/react'
import userEvent from '@testing-library/user-event'

// Clean up the DOM between tests (normally auto-handled but we're explicit here
// since we haven't configured globals cleanup hooks yet).
afterEach(() => {
  cleanup()
})

// Trivial component used to prove React + happy-dom + RTL pipeline works.
function Greeting({ name }: { name: string }) {
  return <h1>Hello, {name}!</h1>
}

describe('Vitest scaffolding — Test A: render a trivial component', () => {
  it('renders a greeting with the provided name', () => {
    render(<Greeting name="World" />)
    expect(
      screen.getByRole('heading', { name: 'Hello, World!' }),
    ).toBeInTheDocument()
  })
})

describe('Vitest scaffolding — Test B: userEvent click fires callback', () => {
  it('calls the onClick handler when a button is clicked', async () => {
    const handleClick = vi.fn()
    const user = userEvent.setup()

    render(<button onClick={handleClick}>click</button>)

    await user.click(screen.getByRole('button', { name: 'click' }))

    expect(handleClick).toHaveBeenCalledTimes(1)
  })
})

describe('Vitest scaffolding — Test C: navigator.sendBeacon mockability', () => {
  // This pattern will be used by Phase 3 error-reporting client tests.
  // The frontend error reporter will use navigator.sendBeacon to ship errors
  // to the backend on page unload; tests must be able to stub it and assert
  // on the payload that would have been sent.
  it('allows navigator.sendBeacon to be stubbed and inspected', () => {
    const sendBeaconMock = vi.fn(() => true)

    // happy-dom provides a navigator object; we override sendBeacon on it.
    // Using defineProperty in case the property is non-writable in some envs.
    Object.defineProperty(globalThis.navigator, 'sendBeacon', {
      configurable: true,
      writable: true,
      value: sendBeaconMock,
    })

    const url = '/api/frontend-errors'
    const payload = JSON.stringify({ message: 'boom' })
    const result = navigator.sendBeacon(url, payload)

    expect(result).toBe(true)
    expect(sendBeaconMock).toHaveBeenCalledTimes(1)
    expect(sendBeaconMock).toHaveBeenCalledWith(url, payload)
  })
})
