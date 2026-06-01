import { useCallback, useEffect, useRef, useState } from 'react'
import { Box, Button, Stack, Typography, Alert } from '@mui/material'
import AutoFixHighIcon from '@mui/icons-material/AutoFixHigh'
import ErrorOutlineIcon from '@mui/icons-material/ErrorOutline'
import Editor, { type OnMount } from '@monaco-editor/react'
import type { RuntimeSpecV3 } from '@/api/queries-commands'
import {
  workspaceColors,
  workspaceAccent,
  workspaceFontFamily,
  workspaceText,
} from '@/applications/workspace/shared/designTokens'
import { canonicalizeSpec } from './specValidation'

export interface SpecEditorJsonProps {
  /** Current canonical spec. */
  spec: RuntimeSpecV3
  /** Called with a successfully-parsed spec; never called on parse failure. */
  onChange: (next: RuntimeSpecV3) => void
}

/**
 * JSON view of the runtime spec editor. Wraps Monaco with JSON language
 * features (auto-format, line numbers, soft wrap, brace matching) and
 * provides a debounced parser that updates the parent state without
 * dropping user input mid-typing.
 *
 * <p>Visually the editor is wrapped in a hairline-bordered card on the
 * workspace paper canvas and runs the {@code vs} light theme so it reads
 * as part of the workspace surface rather than a black console pasted on
 * top. The parse-error strip uses the same inline pattern as the rest of
 * the editor (pale bronze-red tint, slim icon).</p>
 */
export function SpecEditorJson({ spec, onChange }: SpecEditorJsonProps) {
  const [text, setText] = useState(() => canonicalizeSpec(spec))
  const [parseError, setParseError] = useState<string | null>(null)

  const canonical = canonicalizeSpec(spec)
  const lastSyncedCanonical = useRef(canonical)

  useEffect(() => {
    if (canonical !== lastSyncedCanonical.current) {
      lastSyncedCanonical.current = canonical
      setText(canonical)
      setParseError(null)
    }
  }, [canonical])

  useEffect(() => {
    const handle = window.setTimeout(() => {
      if (text === canonical) {
        setParseError(null)
        return
      }
      try {
        const parsed = JSON.parse(text) as RuntimeSpecV3
        if (typeof parsed !== 'object' || parsed === null) {
          throw new Error('Expected a JSON object at the top level.')
        }
        setParseError(null)
        lastSyncedCanonical.current = canonicalizeSpec(parsed)
        onChange(parsed)
      } catch (err) {
        setParseError(
          err instanceof Error
            ? err.message
            : 'Invalid JSON. Fix syntax to apply.',
        )
      }
    }, 250)
    return () => window.clearTimeout(handle)
  }, [text, canonical, onChange])

  const onMount = useCallback<OnMount>((editor, monaco) => {
    editor.updateOptions({
      tabSize: 2,
      wordWrap: 'on',
      automaticLayout: true,
      minimap: { enabled: false },
      scrollBeyondLastLine: false,
      fontFamily: workspaceFontFamily.mono,
      renderLineHighlight: 'gutter',
    })

    // Custom workspace-toned light theme — paper canvas, near-black text,
    // bronze accent for keywords. Keeps Monaco from looking like a foreign
    // VS Code panel inside the warm-paper editor.
    monaco.editor.defineTheme('workspace-light', {
      base: 'vs',
      inherit: true,
      rules: [
        { token: 'string.key.json', foreground: '1A1A18' },
        { token: 'string.value.json', foreground: '6B6B66' },
        { token: 'number', foreground: 'B07F44' },
        { token: 'keyword.json', foreground: 'B07F44' },
      ],
      colors: {
        'editor.background': workspaceColors.canvasBg,
        'editor.foreground': workspaceText.primary,
        'editorLineNumber.foreground': workspaceText.faint,
        'editorLineNumber.activeForeground': workspaceText.muted,
        'editor.lineHighlightBackground': workspaceColors.chipBg,
        'editor.selectionBackground': workspaceAccent.active,
        'editor.inactiveSelectionBackground': workspaceAccent.surface,
        'editorCursor.foreground': workspaceText.primary,
        'editorIndentGuide.background1': workspaceColors.hairline,
      },
    })
    monaco.editor.setTheme('workspace-light')

    monaco.languages.json.jsonDefaults.setDiagnosticsOptions({
      validate: true,
      allowComments: false,
      schemaValidation: 'error',
    })
  }, [])

  const handleFormat = () => {
    const formatted = canonicalizeSpec(spec)
    setText(formatted)
    setParseError(null)
  }

  return (
    <Stack sx={{ height: '100%', minHeight: 0, p: { xs: 2, md: 3 } }}>
      <Box
        sx={{
          flex: 1,
          minHeight: 0,
          display: 'flex',
          flexDirection: 'column',
          border: 1,
          borderColor: 'instrument.hairline',
          borderRadius: 1.5,
          overflow: 'hidden',
          bgcolor: 'instrument.canvas',
        }}
      >
        {/* Toolbar — quiet Format button + count of bytes. */}
        <Stack
          direction="row"
          alignItems="center"
          spacing={1}
          sx={{
            flexShrink: 0,
            px: 1.5,
            py: 0.75,
            bgcolor: 'instrument.chrome',
            borderBottom: 1,
            borderColor: 'instrument.hairline',
          }}
        >
          <Button
            size="small"
            startIcon={<AutoFixHighIcon sx={{ fontSize: 14 }} />}
            onClick={handleFormat}          >
            Format JSON
          </Button>
          <Box sx={{ flex: 1 }} />
          <Typography
            sx={{
              fontSize: '0.6875rem',
              fontFamily: workspaceFontFamily.mono,
              color: workspaceText.faint,
            }}
          >
            {text.length.toLocaleString()} bytes
          </Typography>
        </Stack>

        {parseError && (
          <Alert
            variant="errorStrip"
            severity="error"
            icon={<ErrorOutlineIcon sx={{ fontSize: 14 }} />}
          >
            {parseError}
          </Alert>
        )}

        <Box sx={{ flex: 1, minHeight: 0, bgcolor: 'instrument.canvas' }}>
          <Editor
            language="json"
            value={text}
            onChange={(value) => setText(value ?? '')}
            onMount={onMount}
            theme="workspace-light"
            options={{
              fontSize: 13,
              lineNumbers: 'on',
              fontFamily: workspaceFontFamily.mono,
            }}
          />
        </Box>
      </Box>
    </Stack>
  )
}
