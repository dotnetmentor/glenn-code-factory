import { useState } from 'react'
import {
  Box,
  Stack,
  Tab,
  Tabs,
  Typography,
} from '@mui/material'
import { MachinesTab } from '../components/MachinesTab'
import { VolumesTab } from '../components/VolumesTab'

type TabKey = 'machines' | 'volumes'

/**
 * Operator surface for cleaning up Fly.io resources (machines + volumes)
 * that linger after runtimes are deleted. Two tabs, identical chrome:
 * filter row, table, bulk-destroy confirmation. The linkage badge on each
 * row tells operators which resources still map back to a live DB runtime —
 * those are not safe to nuke without breaking something.
 */
export function FlyCleanupPage() {
  const [tab, setTab] = useState<TabKey>('machines')

  return (
    <Stack spacing={3}>
          <Box>
            <Typography variant="overline" color="text.secondary">
              Super admin
            </Typography>
            <Typography variant="h4" component="h1" sx={{ mb: 0.5 }}>
              Fly cleanup
            </Typography>
            <Typography variant="body2" color="text.secondary">
              Destroy machines and volumes &mdash; destructive, irreversible.
              Use the linkage badge to spot which resources are safe to nuke.
            </Typography>
          </Box>

          <Tabs
            value={tab}
            onChange={(_, v: TabKey) => setTab(v)}
            sx={{ borderBottom: 1, borderColor: 'divider' }}
            aria-label="Fly cleanup tabs"
          >
            <Tab value="machines" label="Machines" />
            <Tab value="volumes" label="Volumes" />
          </Tabs>

          {tab === 'machines' ? <MachinesTab /> : <VolumesTab />}
        </Stack>
  )
}
