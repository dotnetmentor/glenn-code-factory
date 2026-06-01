# Frontend Implementation

## Chat View Component

Core component that handles streaming, history loading, and message persistence.

```tsx
const AI_MODEL = 'anthropic/claude-sonnet-4.5'

interface AnalyticsChatViewProps {
  conversationId: string
  slug: string
  initialMessage?: string | null
}

export function AnalyticsChatView({ conversationId, slug, initialMessage }: AnalyticsChatViewProps) {
  const [input, setInput] = useState('')
  const [historyLoaded, setHistoryLoaded] = useState(false) // MUST be state, not ref
  const [streamingSlowly, setStreamingSlowly] = useState(false)
  const messageCountBeforeStream = useRef(0)
  const hasSentInitial = useRef(false)
  const slowTimerRef = useRef<ReturnType<typeof setTimeout>>(undefined)

  // Load DB messages
  const { data: dbMessages, isLoading } = useGetConversationMessages(slug, conversationId)

  const { state, actions } = useOpenRouterChat({
    endpoints: { stream: `/api/organisations/${slug}/analytics/chat/stream` },
    conversationId,
    onCompleted: () => {
      // Save NEW messages after stream completes
      const allMessages = stateRef.current.messages
      const newMessages = allMessages.slice(messageCountBeforeStream.current)
      if (newMessages.length === 0) return

      const toSave = newMessages.map(m => ({
        role: m.role,
        content: getTextContent(m) || '',
        blocksJson: JSON.stringify(m.blocks),  // Full blocks for frontend rendering
      }))

      saveMessages(slug, conversationId, { messages: toSave })
        .then(() => { messageCountBeforeStream.current = allMessages.length })
    },
    onError: () => {
      clearTimeout(slowTimerRef.current)
      setStreamingSlowly(false)
    },
  })

  // Restore DB messages into chat state on mount
  useEffect(() => {
    if (historyLoaded || !dbMessages || isLoading) return
    setHistoryLoaded(true)

    if (dbMessages.length > 0) {
      const restored = dbMessages.map(m => ({
        id: m.id,
        role: m.role as 'user' | 'assistant',
        blocks: m.blocksJson
          ? JSON.parse(m.blocksJson)           // Full fidelity: tool calls, artifacts, text
          : [{ id: m.id, type: 'text', content: m.content, order: 0, timestamp: new Date(m.createdAt) }],
        timestamp: new Date(m.createdAt),
        isStreaming: false,
      }))
      actions.setMessages(restored)
      messageCountBeforeStream.current = restored.length
    }
  }, [dbMessages, isLoading, actions, historyLoaded])

  // Send initial message after history loaded
  useEffect(() => {
    if (!historyLoaded || hasSentInitial.current || !initialMessage) return
    hasSentInitial.current = true
    const timer = setTimeout(() => {
      const msg = initialMessage.trim()
      if (!msg) return
      messageCountBeforeStream.current = stateRef.current.messages.length
      sendMessage(msg, { model: AI_MODEL })
    }, 100)
    return () => clearTimeout(timer)
  }, [historyLoaded, initialMessage, sendMessage])

  // Slow-stream warning (20s)
  useEffect(() => {
    if (isStreaming) {
      slowTimerRef.current = setTimeout(() => setStreamingSlowly(true), 20_000)
    } else {
      clearTimeout(slowTimerRef.current)
      setStreamingSlowly(false)
    }
    return () => clearTimeout(slowTimerRef.current)
  }, [isStreaming])
}
```

**Key patterns:**
- `historyLoaded` MUST be `useState`, not `useRef` — initial message effect depends on it
- `messageCountBeforeStream` tracks where new messages start for saving
- `BlocksJson` round-trip: stringify on save, parse on load — preserves tool calls and artifacts

## Tool Call Visualization

Pre-process ContentBlocks into segments, group consecutive tool calls:

```tsx
type Segment =
  | { kind: 'text'; id: string; content: string }
  | { kind: 'table'; id: string; queryId: string; description?: string }
  | { kind: 'tools'; id: string; groups: ToolGroup[] }

interface ToolGroup {
  toolName: string
  count: number
  allCompleted: boolean
  hasExecuting: boolean
}

function buildSegments(blocks: ContentBlock[]): Segment[] {
  const segments: Segment[] = []
  let pendingTools: { id: string; toolName: string; status: string; result?: string }[] = []

  const flushTools = () => {
    if (pendingTools.length === 0) return
    // Group by toolName, preserving order of first appearance
    const groups: ToolGroup[] = []
    const groupMap = new Map<string, ToolGroup>()
    for (const t of pendingTools) {
      const existing = groupMap.get(t.toolName)
      if (existing) {
        existing.count++
        if (t.status !== 'completed') existing.allCompleted = false
        if (t.status === 'executing') existing.hasExecuting = true
      } else {
        const g = { toolName: t.toolName, count: 1, allCompleted: t.status === 'completed', hasExecuting: t.status === 'executing' }
        groups.push(g)
        groupMap.set(t.toolName, g)
      }
    }
    segments.push({ kind: 'tools', id: pendingTools[0].id, groups })
    pendingTools = []
  }

  for (const block of blocks) {
    if (block.type === 'text') {
      const content = (block as { content: string }).content
      if (!content) continue
      flushTools()
      segments.push({ kind: 'text', id: block.id, content })
    } else if (block.type === 'tool_call') {
      const tool = block as { id: string; toolName: string; result?: string; status: string }

      // save_and_present_query with queryId → render as data table
      if (tool.toolName === 'save_and_present_query' && tool.result) {
        try {
          const parsed = JSON.parse(tool.result)
          if (parsed.queryId) {
            flushTools()
            segments.push({ kind: 'table', id: block.id, queryId: parsed.queryId, description: parsed.description })
            continue
          }
        } catch { /* fall through */ }
      }

      pendingTools.push({ id: block.id, toolName: tool.toolName, status: tool.status })
    }
  }
  flushTools()
  return segments
}
```

### Tool Chip Rendering

```tsx
const toolMeta: Record<string, { label: string; icon: ReactNode; color: string }> = {
  get_schema:             { label: 'Schema',   icon: <StorageIcon />,    color: '#5856d6' },
  get_business_overview:  { label: 'Översikt',  icon: <InsightsIcon />,   color: '#9333ea' },
  execute_query:          { label: 'Fråga',    icon: <QueryStatsIcon />, color: '#2E7D5A' },
  save_and_present_query: { label: 'Tabell',   icon: <TableChartIcon />, color: '#2B6CB0' },
}

// Render: horizontal row of chips
// - Executing → spinning AutorenewIcon
// - Completed → CheckCircleOutlineIcon
// - Count > 1 → "Label ×N"
```

## Markdown Rendering

Use `react-markdown` + `remark-gfm` with custom MUI components:

```tsx
import ReactMarkdown from 'react-markdown'
import remarkGfm from 'remark-gfm'

const components = {
  p: ({ children }) => <Typography sx={{ fontSize: '0.8125rem', lineHeight: 1.7 }}>{children}</Typography>,
  strong: ({ children }) => <Box component="strong" sx={{ fontWeight: 600 }}>{children}</Box>,
  code: ({ children, className }) => {
    const content = String(children).replace(/\n$/, '')
    const isBlock = !!className || content.includes('\n')
    // Block: monospace, subtle bg in <pre> wrapper
    // Inline: small monospace with grey background pill
  },
  pre: ({ children }) => <Box component="pre" sx={{ borderRadius: '8px', bgcolor: alpha(grey[900], 0.04) }}>{children}</Box>,
  table: ({ children }) => /* rounded border container with overflow scroll */,
  th: ({ children }) => /* uppercase, small, grey headers */,
  // ... blockquote, links, lists
}

export function MarkdownContent({ content }) {
  return <ReactMarkdown remarkPlugins={[remarkGfm]} components={components}>{content.trim()}</ReactMarkdown>
}
```

## Conversation Management

### AnalyticsPanel (parent)

```tsx
export function AnalyticsPanel({ open, onToggle }) {
  const [conversationId, setConversationId] = useState<string | null>(
    () => localStorage.getItem(`analytics-conv-${slug}`)
  )
  const [pendingMessage, setPendingMessage] = useState<string | null>(null)
  const staleCheckDone = useRef(false)

  const { data: conversations = [], isSuccess } = useGetConversations(slug)

  // Stale ID cleanup — ONCE on mount only (prevents race condition)
  useEffect(() => {
    if (!isSuccess || staleCheckDone.current) return
    staleCheckDone.current = true
    if (!conversationId) return
    if (!conversations.some(c => c.id === conversationId)) {
      setConversationId(null)
      localStorage.removeItem(`analytics-conv-${slug}`)
    }
  }, [isSuccess, conversations, conversationId, slug])

  const handleNewConversation = async (initialMessage?: string) => {
    const result = await createConversation.mutateAsync({ slug, data: { title: null } })
    setConversationId(result.id!)
    localStorage.setItem(`analytics-conv-${slug}`, result.id!)
    invalidateConversations()
    setPendingMessage(initialMessage ?? null)
  }

  return (
    <>
      <ConversationPicker ... />
      {conversationId ? (
        <AnalyticsChatView
          key={conversationId}  // Force re-mount on switch
          conversationId={conversationId}
          slug={slug}
          initialMessage={pendingMessage}
        />
      ) : (
        <EmptyState onSuggestionClick={msg => handleNewConversation(msg)} />
      )}
    </>
  )
}
```

### ConversationPicker

MUI Menu dropdown showing:
- "Ny konversation" button at top
- List of conversations: title + relative time ("2 timmar sedan")
- Delete icon on hover per item
- Active conversation highlighted

### Slow Stream Warning

After 20 seconds of streaming, show a warning with cancel button:

```tsx
{streamingSlowly && (
  <Box sx={{ display: 'flex', alignItems: 'center', gap: 1, bgcolor: alpha(warning.main, 0.06), borderRadius: '10px' }}>
    <HourglassBottomIcon />
    <Typography>Det tar längre tid än vanligt...</Typography>
    <Button onClick={handleCancel}>Avbryt</Button>
  </Box>
)}
```

## QueryDataTable

Renders saved query results in a MUI DataGrid:

```tsx
export function QueryDataTable({ queryId, slug, description }) {
  const { data, isLoading, error } = useQuery({
    queryKey: ['saved-query', queryId],
    queryFn: () => customClient(`/api/organisations/${slug}/analytics/saved-queries/${queryId}/execute`),
    staleTime: Infinity,  // Saved queries are immutable
  })

  // Render MUI DataGrid with auto-generated columns from data.columns
  // Show description above the table
  // Handle loading/error states
}
```
