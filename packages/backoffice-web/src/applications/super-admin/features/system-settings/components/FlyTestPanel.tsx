import { Stack } from '@mui/material'
import { useState } from 'react'
import {
  usePostApiAdminFlyTestConnection,
  type FlyTestConnectionResponse,
} from '../../../../../api/queries-commands'
import {
  DetailSection,
  FlagGroup,
  KeyValueRow,
  TestConnectionPanel,
} from './TestConnectionPanel'

/**
 * "Test connection" experience for the Fly category. Wraps the generic
 * <TestConnectionPanel/> with the Fly-specific mutation and the
 * presence/ping detail rows.
 */
export function FlyTestPanel() {
  const mutation = usePostApiAdminFlyTestConnection()
  const [result, setResult] = useState<FlyTestConnectionResponse | null>(null)
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

  const details = result ? <FlyResultDetails result={result} /> : null

  return (
    <TestConnectionPanel
      title="Fly configuration"
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

function FlyResultDetails({ result }: { result: FlyTestConnectionResponse }) {
  return (
    <Stack spacing={2}>
      <FlagGroup
        heading="Configuration"
        flags={[
          { label: 'ApiToken', ok: result.apiTokenSet },
          { label: 'AppName', ok: result.appNameSet },
          { label: 'OrgSlug', ok: result.orgSlugSet },
        ]}
      />

      <DetailSection
        heading="Fly ping"
        ok={result.pingSucceeded}
        summary={
          result.pingSucceeded
            ? result.appExists
              ? 'OK (app found)'
              : 'OK (app not found)'
            : 'Failed'
        }
        errorText={result.pingError ?? undefined}
      >
        <Stack spacing={0.5}>
          <KeyValueRow label="App name" value={result.appName} />
        </Stack>
      </DetailSection>
    </Stack>
  )
}
