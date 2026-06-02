import {
  Button,
  Dialog,
  DialogActions,
  DialogContent,
  DialogTitle,
  Typography,
} from '@mui/material'
import { bodySx, workspaceFontFamily, workspaceText } from '../../../shared'

export interface ApplyRuntimeSpecDialogProps {
  open: boolean
  branchNames: string[]
  volumeSizeChanged: boolean
  pending: boolean
  onClose: () => void
  onNewBranchesOnly: () => void
  onApplyToAll: () => void
}

export function ApplyRuntimeSpecDialog({
  open,
  branchNames,
  volumeSizeChanged,
  pending,
  onClose,
  onNewBranchesOnly,
  onApplyToAll,
}: ApplyRuntimeSpecDialogProps) {
  const branchList =
    branchNames.length <= 3
      ? branchNames.join(', ')
      : `${branchNames.slice(0, 3).join(', ')} and ${branchNames.length - 3} more`

  return (
    <Dialog open={open} onClose={pending ? undefined : onClose} maxWidth="sm" fullWidth>
      <DialogTitle
        sx={{
          fontFamily: workspaceFontFamily.sans,
          fontWeight: 500,
          color: workspaceText.primary,
        }}
      >
        Apply to existing branches?
      </DialogTitle>
      <DialogContent>
        <Typography sx={bodySx}>
          {branchNames.length === 1
            ? `The branch "${branchList}" is still running with the old CPU/RAM settings.`
            : `${branchNames.length} branches (${branchList}) are still running with the old CPU/RAM settings.`}
        </Typography>
        <Typography sx={{ ...bodySx, mt: 2 }}>
          Apply to all existing branches? Each affected runtime will restart and may be
          unavailable for a few minutes while it reprovisions with the new size.
        </Typography>
        {volumeSizeChanged ? (
          <Typography sx={{ ...bodySx, mt: 2, color: workspaceText.muted }}>
            Disk size changes only take effect on the live volume after reset from scratch.
            CPU and RAM will still update after restart.
          </Typography>
        ) : null}
      </DialogContent>
      <DialogActions sx={{ px: 3, pb: 2.5, gap: 1 }}>
        <Button variant="text" onClick={onClose} disabled={pending}>
          Cancel
        </Button>
        <Button variant="outlined" onClick={onNewBranchesOnly} disabled={pending}>
          New branches only
        </Button>
        <Button variant="pill" color="primary" onClick={onApplyToAll} disabled={pending}>
          {pending ? 'Saving…' : 'Apply to all branches'}
        </Button>
      </DialogActions>
    </Dialog>
  )
}
