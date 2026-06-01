import { Box, Dialog, DialogContent, IconButton, Typography, Chip, Slide, Tab, Tabs, alpha } from '@mui/material'
import { TransitionProps } from '@mui/material/transitions'
import { forwardRef, useState } from 'react'
import CloseIcon from '@mui/icons-material/Close'
import BoltIcon from '@mui/icons-material/Bolt'
import CompareArrowsIcon from '@mui/icons-material/CompareArrows'
import ContentCopyIcon from '@mui/icons-material/ContentCopy'
import { DomainEventsTable } from '../components/DomainEventsTable'
import { EntityChangesTable } from '../components/EntityChangesTable'
import { JsonDiffView, JsonValueView, tryParseJson } from '../components/JsonDiffView'
import { DomainEventListItem, EntityChangeListItem } from '../../../../../api/queries-commands'
import { formatSwedishDateTime } from '../../../../shared/utils'

const SlideUp = forwardRef(function Transition(
  props: TransitionProps & { children: React.ReactElement },
  ref: React.Ref<unknown>,
) {
  return <Slide direction="up" ref={ref} {...props} />
})

function formatPayload(payload: string): string {
  try {
    return JSON.stringify(JSON.parse(payload), null, 2)
  } catch {
    return payload
  }
}

function MetaItem({ label, value, mono }: { label: string; value: string; mono?: boolean }) {
  return (
    <Box>
      <Typography
        variant="overline"
        sx={{ fontSize: '0.65rem', color: 'text.disabled', letterSpacing: '0.08em', lineHeight: 1 }}
      >
        {label}
      </Typography>
      <Typography
        variant="body2"
        sx={{
          mt: 0.25,
          fontWeight: 500,
          fontSize: '0.825rem',
          color: 'text.primary',
          ...(mono && {
            fontFamily: '"SF Mono", "Fira Code", "Consolas", monospace',
            fontSize: '0.775rem',
            wordBreak: 'break-all',
          }),
        }}
      >
        {value || '\u2014'}
      </Typography>
    </Box>
  )
}

interface ChangedProperty {
  propertyName: string
  oldValue?: string | null
  newValue?: string | null
}

function parseChangedProperties(changedProperties: string): ChangedProperty[] {
  try {
    const parsed = JSON.parse(changedProperties)
    if (Array.isArray(parsed)) {
      return parsed.map((p: Record<string, unknown>) => ({
        propertyName: (p.propertyName ?? p.Property ?? '') as string,
        oldValue: (p.oldValue ?? p.OldValue ?? null) as string | null,
        newValue: (p.newValue ?? p.NewValue ?? null) as string | null,
      }))
    }
    return []
  } catch {
    return []
  }
}

const operationChipColor: Record<string, 'success' | 'info' | 'error' | 'default'> = {
  Created: 'success',
  Updated: 'info',
  Deleted: 'error',
}

export function DomainEventsPage() {
  const [activeTab, setActiveTab] = useState(0)
  const [selectedEvent, setSelectedEvent] = useState<DomainEventListItem | null>(null)
  const [selectedChange, setSelectedChange] = useState<EntityChangeListItem | null>(null)
  const [copied, setCopied] = useState(false)

  const handleViewEvent = (event: DomainEventListItem) => {
    setSelectedEvent(event)
  }

  const handleCloseEvent = () => {
    setSelectedEvent(null)
  }

  const handleViewChange = (change: EntityChangeListItem) => {
    setSelectedChange(change)
  }

  const handleCloseChange = () => {
    setSelectedChange(null)
  }

  const handleCopyPayload = () => {
    if (!selectedEvent) return
    navigator.clipboard.writeText(formatPayload(selectedEvent.payload))
    setCopied(true)
    setTimeout(() => setCopied(false), 2000)
  }

  return (
    <>
      {/* Page header */}
      <Box sx={{ mb: 4 }}>
        <Box sx={{ display: 'flex', alignItems: 'center', gap: 1.5, mb: 0.5 }}>
          <Box
            sx={{
              width: 36,
              height: 36,
              borderRadius: 2,
              bgcolor: (theme) => alpha(theme.palette.primary.main, 0.06),
              border: '1px solid',
              borderColor: 'divider',
              display: 'flex',
              alignItems: 'center',
              justifyContent: 'center',
            }}
          >
            <BoltIcon sx={{ fontSize: 18, color: 'text.secondary' }} />
          </Box>
          <Box>
            <Typography variant="h4" component="h1" sx={{ lineHeight: 1.2 }}>
              Audit Log
            </Typography>
            <Typography variant="caption" color="text.secondary">
              Domain events and entity changes tracked by the system
            </Typography>
          </Box>
        </Box>
      </Box>

      {/* Tabs */}
      <Box sx={{ mb: 2 }}>
        <Tabs
          value={activeTab}
          onChange={(_, newValue) => setActiveTab(newValue)}
          sx={{
            minHeight: 36,
            '& .MuiTab-root': {
              minHeight: 36,
              textTransform: 'none',
              fontWeight: 500,
              fontSize: '0.825rem',
              px: 2,
            },
            '& .MuiTabs-indicator': {
              height: 2,
              borderRadius: 1,
            },
          }}
        >
          <Tab icon={<BoltIcon sx={{ fontSize: 16 }} />} iconPosition="start" label="Domain Events" />
          <Tab icon={<CompareArrowsIcon sx={{ fontSize: 16 }} />} iconPosition="start" label="Entity Changes" />
        </Tabs>
      </Box>

      {/* Content card */}
      <Box
        sx={{
          bgcolor: 'background.paper',
          borderRadius: 3,
          border: '1px solid',
          borderColor: 'divider',
          overflow: 'hidden',
          p: { xs: 2, sm: 3 },
        }}
      >
        {activeTab === 0 && <DomainEventsTable onViewEvent={handleViewEvent} />}
        {activeTab === 1 && <EntityChangesTable onViewChange={handleViewChange} />}
      </Box>

      {/* Domain Event detail dialog */}
      <Dialog
        open={!!selectedEvent}
        onClose={handleCloseEvent}
        TransitionComponent={SlideUp}
        maxWidth="sm"
        fullWidth
        PaperProps={{
          sx: {
            borderRadius: 3,
            maxHeight: '85vh',
          },
        }}
      >
        {selectedEvent && (
          <DialogContent sx={{ p: 0 }}>
            {/* Header */}
            <Box
              sx={{
                px: 3,
                pt: 2.5,
                pb: 2,
                display: 'flex',
                alignItems: 'flex-start',
                justifyContent: 'space-between',
                borderBottom: '1px solid',
                borderColor: 'divider',
              }}
            >
              <Box sx={{ display: 'flex', alignItems: 'center', gap: 1.5 }}>
                <Chip
                  label={selectedEvent.eventType}
                  size="small"
                  sx={{
                    fontWeight: 600,
                    fontSize: '0.775rem',
                    height: 26,
                  }}
                />
                {selectedEvent.entityType && (
                  <Typography variant="body2" color="text.secondary" sx={{ fontWeight: 500 }}>
                    on {selectedEvent.entityType}
                  </Typography>
                )}
              </Box>
              <IconButton size="small" onClick={handleCloseEvent} sx={{ mt: -0.5 }}>
                <CloseIcon fontSize="small" />
              </IconButton>
            </Box>

            {/* Meta grid */}
            <Box
              sx={{
                px: 3,
                py: 2.5,
                display: 'grid',
                gridTemplateColumns: '1fr 1fr',
                gap: 2.5,
                borderBottom: '1px solid',
                borderColor: 'divider',
                bgcolor: (theme) => alpha(theme.palette.background.default, 0.5),
              }}
            >
              <MetaItem label="Entity Type" value={selectedEvent.entityType} />
              <MetaItem label="Occurred At" value={formatSwedishDateTime(selectedEvent.occurredAt) || selectedEvent.occurredAt} />
              <MetaItem label="Entity ID" value={selectedEvent.entityId} mono />
              <MetaItem label="User ID" value={selectedEvent.userId || '\u2014'} mono />
            </Box>

            {/* Payload */}
            <Box sx={{ px: 3, py: 2.5 }}>
              <Box sx={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between', mb: 1.5 }}>
                <Typography
                  variant="overline"
                  sx={{ fontSize: '0.65rem', color: 'text.disabled', letterSpacing: '0.08em' }}
                >
                  Event Payload
                </Typography>
                <IconButton
                  size="small"
                  onClick={handleCopyPayload}
                  sx={{ opacity: 0.5, '&:hover': { opacity: 1 } }}
                >
                  <ContentCopyIcon sx={{ fontSize: 14 }} />
                </IconButton>
              </Box>
              {copied && (
                <Typography variant="caption" sx={{ color: 'success.main', mb: 1, display: 'block' }}>
                  Copied to clipboard
                </Typography>
              )}
              <Box
                component="pre"
                sx={{
                  p: 2,
                  m: 0,
                  borderRadius: 2,
                  bgcolor: 'grey.900',
                  color: 'grey.100',
                  overflow: 'auto',
                  maxHeight: 340,
                  fontSize: '0.75rem',
                  lineHeight: 1.6,
                  fontFamily: '"SF Mono", "Fira Code", "Consolas", monospace',
                  border: '1px solid',
                  borderColor: 'rgba(255,255,255,0.06)',
                  '&::-webkit-scrollbar': {
                    width: 6,
                    height: 6,
                  },
                  '&::-webkit-scrollbar-track': {
                    background: 'transparent',
                  },
                  '&::-webkit-scrollbar-thumb': {
                    background: 'rgba(255,255,255,0.15)',
                    borderRadius: 3,
                  },
                }}
              >
                {formatPayload(selectedEvent.payload)}
              </Box>
            </Box>
          </DialogContent>
        )}
      </Dialog>

      {/* Entity Change detail dialog */}
      <Dialog
        open={!!selectedChange}
        onClose={handleCloseChange}
        TransitionComponent={SlideUp}
        maxWidth="sm"
        fullWidth
        PaperProps={{
          sx: {
            borderRadius: 3,
            maxHeight: '85vh',
          },
        }}
      >
        {selectedChange && (
          <DialogContent sx={{ p: 0 }}>
            {/* Header */}
            <Box
              sx={{
                px: 3,
                pt: 2.5,
                pb: 2,
                display: 'flex',
                alignItems: 'flex-start',
                justifyContent: 'space-between',
                borderBottom: '1px solid',
                borderColor: 'divider',
              }}
            >
              <Box sx={{ display: 'flex', alignItems: 'center', gap: 1.5 }}>
                <Chip
                  label={selectedChange.operation}
                  size="small"
                  color={operationChipColor[selectedChange.operation] || 'default'}
                  sx={{
                    fontWeight: 600,
                    fontSize: '0.775rem',
                    height: 26,
                  }}
                />
                <Typography variant="body2" color="text.secondary" sx={{ fontWeight: 500 }}>
                  {selectedChange.entityType}
                </Typography>
              </Box>
              <IconButton size="small" onClick={handleCloseChange} sx={{ mt: -0.5 }}>
                <CloseIcon fontSize="small" />
              </IconButton>
            </Box>

            {/* Meta grid */}
            <Box
              sx={{
                px: 3,
                py: 2.5,
                display: 'grid',
                gridTemplateColumns: '1fr 1fr',
                gap: 2.5,
                borderBottom: '1px solid',
                borderColor: 'divider',
                bgcolor: (theme) => alpha(theme.palette.background.default, 0.5),
              }}
            >
              <MetaItem label="Entity Type" value={selectedChange.entityType} />
              <MetaItem label="Occurred At" value={formatSwedishDateTime(selectedChange.occurredAt) || selectedChange.occurredAt} />
              <MetaItem label="Entity ID" value={selectedChange.entityId} mono />
              <MetaItem label="User ID" value={selectedChange.userId || '\u2014'} mono />
            </Box>

            {/* Changed Properties */}
            <Box sx={{ px: 3, py: 2.5 }}>
              <Typography
                variant="overline"
                sx={{ fontSize: '0.65rem', color: 'text.disabled', letterSpacing: '0.08em', mb: 1.5, display: 'block' }}
              >
                Changed Properties
              </Typography>

              {(() => {
                const allProperties = parseChangedProperties(selectedChange.changedProperties)
                // Only show properties where old and new values actually differ
                const properties = allProperties.filter(p => {
                  const oldNorm = p.oldValue ?? null
                  const newNorm = p.newValue ?? null
                  return oldNorm !== newNorm
                })
                if (properties.length === 0) {
                  return (
                    <Typography variant="body2" color="text.disabled" sx={{ fontStyle: 'italic' }}>
                      No property changes recorded
                    </Typography>
                  )
                }

                // Split into scalar and JSON properties
                const scalarProps: typeof properties = []
                const jsonProps: { prop: (typeof properties)[0]; oldParsed: unknown; newParsed: unknown }[] = []

                for (const prop of properties) {
                  const oldParsed = tryParseJson(prop.oldValue)
                  const newParsed = tryParseJson(prop.newValue)
                  if (oldParsed !== null || newParsed !== null) {
                    jsonProps.push({ prop, oldParsed, newParsed })
                  } else {
                    scalarProps.push(prop)
                  }
                }

                return (
                  <Box sx={{ display: 'flex', flexDirection: 'column', gap: 2 }}>
                    {/* Scalar properties table */}
                    {scalarProps.length > 0 && (
                      <Box
                        sx={{
                          borderRadius: 2,
                          border: '1px solid',
                          borderColor: 'divider',
                          overflow: 'hidden',
                        }}
                      >
                        {/* Table header */}
                        <Box
                          sx={{
                            display: 'grid',
                            gridTemplateColumns: '1fr 1fr 1fr',
                            gap: 0,
                            px: 1.5,
                            py: 1,
                            bgcolor: (theme) => alpha(theme.palette.divider, 0.15),
                            borderBottom: '1px solid',
                            borderColor: 'divider',
                          }}
                        >
                          <Typography variant="overline" sx={{ fontSize: '0.6rem', color: 'text.disabled', letterSpacing: '0.08em' }}>
                            Property
                          </Typography>
                          <Typography variant="overline" sx={{ fontSize: '0.6rem', color: 'text.disabled', letterSpacing: '0.08em' }}>
                            Old Value
                          </Typography>
                          <Typography variant="overline" sx={{ fontSize: '0.6rem', color: 'text.disabled', letterSpacing: '0.08em' }}>
                            New Value
                          </Typography>
                        </Box>

                        {/* Table rows */}
                        {scalarProps.map((prop, idx) => {
                          const isLast = idx === scalarProps.length - 1
                          return (
                            <Box
                              key={prop.propertyName}
                              sx={{
                                display: 'grid',
                                gridTemplateColumns: '1fr 1fr 1fr',
                                gap: 0,
                                px: 1.5,
                                py: 1,
                                borderBottom: isLast ? 'none' : '1px solid',
                                borderColor: 'divider',
                                '&:hover': {
                                  bgcolor: (theme) => alpha(theme.palette.primary.main, 0.02),
                                },
                              }}
                            >
                              <Typography
                                variant="body2"
                                sx={{
                                  fontWeight: 600,
                                  fontSize: '0.775rem',
                                  color: 'text.primary',
                                }}
                              >
                                {prop.propertyName}
                              </Typography>
                              <Typography
                                variant="body2"
                                sx={{
                                  fontFamily: '"SF Mono", "Fira Code", "Consolas", monospace',
                                  fontSize: '0.725rem',
                                  color: 'error.main',
                                  wordBreak: 'break-all',
                                  opacity: prop.oldValue != null ? 1 : 0.4,
                                }}
                              >
                                {prop.oldValue != null ? String(prop.oldValue) : '\u2014'}
                              </Typography>
                              <Typography
                                variant="body2"
                                sx={{
                                  fontFamily: '"SF Mono", "Fira Code", "Consolas", monospace',
                                  fontSize: '0.725rem',
                                  color: 'success.main',
                                  wordBreak: 'break-all',
                                  opacity: prop.newValue != null ? 1 : 0.4,
                                }}
                              >
                                {prop.newValue != null ? String(prop.newValue) : '\u2014'}
                              </Typography>
                            </Box>
                          )
                        })}
                      </Box>
                    )}

                    {/* JSON diff properties */}
                    {jsonProps.map(({ prop, oldParsed, newParsed }) => {
                      if (oldParsed !== null && newParsed !== null) {
                        return <JsonDiffView key={prop.propertyName} oldVal={oldParsed} newVal={newParsed} propName={prop.propertyName} />
                      }
                      if (newParsed !== null) {
                        return <JsonValueView key={prop.propertyName} value={newParsed} propName={prop.propertyName} variant="added" />
                      }
                      if (oldParsed !== null) {
                        return <JsonValueView key={prop.propertyName} value={oldParsed} propName={prop.propertyName} variant="removed" />
                      }
                      return null
                    })}
                  </Box>
                )
              })()}
            </Box>
          </DialogContent>
        )}
      </Dialog>
    </>
  )
}
