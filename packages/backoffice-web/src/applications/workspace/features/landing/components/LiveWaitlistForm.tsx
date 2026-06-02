import { useState } from 'react'
import { Box, Button, CircularProgress, Stack, TextField, Typography } from '@mui/material'
import CheckCircleRoundedIcon from '@mui/icons-material/CheckCircleRounded'
import { usePostApiWaitlist } from '@/api/queries-commands'
import {
  surfaceTokens,
  workspaceFontFamily,
  semanticTokens,
} from '../../../shared/designTokens'

const SANS = workspaceFontFamily.sans

/**
 * The one genuinely live piece of the landing page. This is the same form the
 * "movie" shows GlennCode building — wired to the real {@code POST /api/waitlist}
 * via the generated Orval hook. The visitor signs up *inside the preview*, which
 * proves the "it's really running" claim better than any copy could.
 */
export function LiveWaitlistForm() {
  const [email, setEmail] = useState('')
  const [note, setNote] = useState('')
  const join = usePostApiWaitlist()
  const [done, setDone] = useState(false)

  const emailValid = /^[^@\s]+@[^@\s]+\.[^@\s]+$/.test(email.trim())

  const submit = () => {
    if (!emailValid || join.isPending) return
    join.mutate(
      { data: { email: email.trim(), note: note.trim() || undefined, source: 'landing-preview' } },
      { onSuccess: () => setDone(true) },
    )
  }

  if (done) {
    return (
      <Stack alignItems="center" spacing={1.5} sx={{ textAlign: 'center', py: 4, px: 3 }}>
        <CheckCircleRoundedIcon sx={{ fontSize: 40, color: semanticTokens.success }} />
        <Typography sx={{ fontFamily: SANS, fontWeight: 600, fontSize: '1.05rem', color: surfaceTokens.textPrimary }}>
          You're on the list 🎉
        </Typography>
        <Typography sx={{ fontFamily: SANS, fontSize: '0.875rem', color: surfaceTokens.textMuted, maxWidth: 320 }}>
          We'll be in touch as we open up access. Thanks for the interest.
        </Typography>
      </Stack>
    )
  }

  return (
    <Stack spacing={2.5} sx={{ width: '100%', maxWidth: 420, mx: 'auto', px: 3, py: 4 }}>
      <Box sx={{ textAlign: 'center' }}>
        <Typography sx={{ fontFamily: SANS, fontWeight: 600, fontSize: '1.35rem', letterSpacing: '-0.02em', color: surfaceTokens.textPrimary }}>
          Build software by describing it.
        </Typography>
        <Typography sx={{ fontFamily: SANS, fontSize: '0.9rem', color: surfaceTokens.textMuted, mt: 0.75 }}>
          Join the waitlist for early access.
        </Typography>
      </Box>

      <Stack spacing={1.25}>
        <TextField
          placeholder="you@company.com"
          value={email}
          onChange={(e) => setEmail(e.target.value)}
          onKeyDown={(e) => { if (e.key === 'Enter') submit() }}
          size="small"
          type="email"
          autoComplete="email"
          fullWidth
        />
        <TextField
          placeholder="What would you build? (optional)"
          value={note}
          onChange={(e) => setNote(e.target.value)}
          onKeyDown={(e) => { if (e.key === 'Enter') submit() }}
          size="small"
          fullWidth
        />
        {join.isError && (
          <Typography sx={{ fontFamily: SANS, fontSize: '0.8rem', color: semanticTokens.error }}>
            Something went wrong — please try again.
          </Typography>
        )}
        <Button
          variant="contained"
          fullWidth
          disableElevation
          onClick={submit}
          disabled={!emailValid || join.isPending}
          sx={{ py: 1.25, fontFamily: SANS, fontWeight: 600, textTransform: 'none', fontSize: '0.95rem' }}
        >
          {join.isPending && <CircularProgress size={15} sx={{ color: 'inherit', mr: 1 }} />}
          Join the waitlist
        </Button>
      </Stack>

      <Typography sx={{ fontFamily: SANS, fontSize: '0.72rem', color: surfaceTokens.textFaint, textAlign: 'center' }}>
        No spam. We'll only email you about access.
      </Typography>
    </Stack>
  )
}
