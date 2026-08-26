import { Stack } from '@mui/material'
import { useState } from 'react'
import {
  usePostApiAdminBoxTestConnection,
  type BoxTestConnectionResponse,
} from '../../../../../api/queries-commands'
import {
  DetailSection,
  FlagGroup,
  TestConnectionPanel,
} from './TestConnectionPanel'

/**
 * "Test connection" experience for the Box category. Wraps the generic
 * <TestConnectionPanel/> with the Box-specific mutation and the
 * presence/ping detail rows.
 */
export function BoxTestPanel() {
  const mutation = usePostApiAdminBoxTestConnection()
  const [result, setResult] = useState<BoxTestConnectionResponse | null>(null)
  const [errorText, setErrorText] = useState<string | null>(null)

  const handleTest = () => {
    // Wipe the previous result while the new call is in flight.
    setResult(null)
    setErrorText(null)
    mutation.reset()
    mutation.mutate(undefined, {
      onSuccess: (data) => {
        setResult(data)
      },
      onError: (err) => {
        const message = err instanceof Error ? err.message : 'Request failed'
        setErrorText(message)
      },
    })
  }

  const details = result ? <BoxResultDetails result={result} /> : null

  return (
    <TestConnectionPanel
      title="Box configuration"
      isPending={mutation.isPending}
      isValid={result ? result.isValid : null}
      message={result ? result.message : null}
      hasResult={result !== null || errorText !== null}
      requestError={errorText}
      details={details}
      onTest={handleTest}
    />
  )
}

function BoxResultDetails({ result }: { result: BoxTestConnectionResponse }) {
  return (
    <Stack spacing={2}>
      <FlagGroup
        heading="Configuration"
        flags={[{ label: 'ApiKey', ok: result.apiKeySet }]}
      />

      <DetailSection
        heading="Box ping"
        ok={result.pingSucceeded}
        summary={result.pingSucceeded ? 'OK' : 'Failed'}
        errorText={result.pingError ?? undefined}
      />
    </Stack>
  )
}
