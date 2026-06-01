import { Stack } from '@mui/material'
import { useState } from 'react'
import {
  usePostApiAdminGithubTestConnection,
  type GithubTestConnectionResponse,
} from '../../../../../api/queries-commands'
import {
  DetailSection,
  FlagGroup,
  KeyValueRow,
  TestConnectionPanel,
} from './TestConnectionPanel'

/**
 * "Test connection" experience for the GitHub category. Wraps the generic
 * <TestConnectionPanel/> with the GitHub-specific mutation and the
 * presence/JWT/app-call detail rows.
 */
export function GithubTestPanel() {
  const mutation = usePostApiAdminGithubTestConnection()
  const [result, setResult] = useState<GithubTestConnectionResponse | null>(null)
  const [errorText, setErrorText] = useState<string | null>(null)

  const handleTest = () => {
    // Wipe the previous result while the new call is in flight, per UX spec.
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

  const details = result ? <GithubResultDetails result={result} /> : null

  return (
    <TestConnectionPanel
      title="GitHub configuration"
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

function GithubResultDetails({
  result,
}: {
  result: GithubTestConnectionResponse
}) {
  const appCallSummary = result.appCallSucceeded
    ? `OK${result.appCallStatusCode ? ` (status ${result.appCallStatusCode})` : ''}`
    : `Failed${result.appCallStatusCode ? ` (status ${result.appCallStatusCode})` : ''}`

  return (
    <Stack spacing={2}>
      <FlagGroup
        heading="App"
        flags={[
          { label: 'AppId', ok: result.appIdSet },
          { label: 'PrivateKeyPem', ok: result.privateKeyPemSet },
          { label: 'AppSlug', ok: result.appSlugSet },
        ]}
      />
      <FlagGroup
        heading="OAuth"
        flags={[
          { label: 'ClientId', ok: result.clientIdSet },
          { label: 'ClientSecret', ok: result.clientSecretSet },
          { label: 'RedirectUri', ok: result.oAuthRedirectUriSet },
        ]}
      />
      <FlagGroup
        heading="Webhook"
        flags={[{ label: 'WebhookSecret', ok: result.webhookSecretSet }]}
      />
      <FlagGroup
        heading="Install"
        flags={[
          {
            label: 'AppInstallRedirectUri',
            ok: result.appInstallRedirectUriSet,
          },
        ]}
      />

      <DetailSection
        heading="JWT mint"
        ok={result.jwtMintable}
        summary={result.jwtMintable ? 'Mintable' : 'Failed'}
        errorText={result.jwtMintError ?? undefined}
      />

      <DetailSection
        heading="GitHub /app call"
        ok={result.appCallSucceeded}
        summary={appCallSummary}
        errorText={result.appCallError ?? undefined}
      >
        <Stack spacing={0.5}>
          <KeyValueRow label="App name" value={result.appName} />
          <KeyValueRow label="App owner" value={result.appOwner} />
          <KeyValueRow label="App slug" value={result.appSlug} />
        </Stack>
      </DetailSection>
    </Stack>
  )
}
