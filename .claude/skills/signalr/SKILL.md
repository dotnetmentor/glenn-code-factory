# SignalR Real-time - Complete Reference

## Overview

Whenever the user needs to implement real-time bidirectional communication using SignalR, use this skill.

Real-time bidirectional communication using SignalR. Perfect for:
- Live collaboration (documents, whiteboards)
- Real-time notifications
- Live data updates
- Presence indicators (who's online)

---

## 1. Backend: Hub Definition

Use typed hubs for type safety with TypedSignalR.Client:

```csharp
using Microsoft.AspNetCore.SignalR;
using TypedSignalR.Client;
using Tapper;

// Server methods (client calls these)
[Hub]
public interface IDocumentHub
{
    Task JoinDocument(Guid documentId);
    Task LeaveDocument(Guid documentId);
    Task UpdateContent(Guid documentId, string content, int version);
}

// Client methods (server calls these)
[Receiver]
public interface IDocumentClient
{
    Task ContentUpdated(DocumentContentSnapshot snapshot);
    Task UserCountChanged(int count);
    Task UserPresenceChanged(DocumentPresenceSnapshot presence);
}

// DTOs for TypeScript generation
[TranspilationSource]
public class DocumentContentSnapshot
{
    public Guid DocumentId { get; set; }
    public string Content { get; set; } = string.Empty;
    public int Version { get; set; }
    public string UpdatedBy { get; set; } = string.Empty;
}
```

---

## 2. Backend: Hub Implementation

```csharp
public class DocumentHub : Hub<IDocumentClient>, IDocumentHub
{
    // Track connections per document
    private static readonly ConcurrentDictionary<Guid, ConcurrentDictionary<string, string>> _connections = new();

    public async Task JoinDocument(Guid documentId)
    {
        var groupName = $"document:{documentId}";
        await Groups.AddToGroupAsync(Context.ConnectionId, groupName);

        // Track connection
        var connections = _connections.GetOrAdd(documentId, _ => new());
        var userName = Context.User?.Identity?.Name ?? "Anonymous";
        connections.TryAdd(Context.ConnectionId, userName);

        // Notify others
        await Clients.Group(groupName).UserCountChanged(connections.Count);
        await Clients.OthersInGroup(groupName).UserPresenceChanged(new DocumentPresenceSnapshot
        {
            UserId = Context.ConnectionId,
            UserName = userName,
            Action = "joined"
        });
    }

    public async Task UpdateContent(Guid documentId, string content, int version)
    {
        var groupName = $"document:{documentId}";

        // Broadcast to others (excluding sender)
        await Clients.OthersInGroup(groupName).ContentUpdated(new DocumentContentSnapshot
        {
            DocumentId = documentId,
            Content = content,
            Version = version,
            UpdatedBy = Context.User?.Identity?.Name ?? "Anonymous"
        });
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        // Clean up connections
        foreach (var (documentId, connections) in _connections)
        {
            if (connections.TryRemove(Context.ConnectionId, out var userName))
            {
                var groupName = $"document:{documentId}";
                await Clients.Group(groupName).UserCountChanged(connections.Count);
            }
        }
        await base.OnDisconnectedAsync(exception);
    }
}
```

---

## 3. Backend: Registration

```csharp
// In Program.cs
builder.Services.AddSignalR()
    .AddJsonProtocol(options =>
    {
        options.PayloadSerializerOptions.Converters.Add(
            new JsonStringEnumConverter());
    });

// Map hub
app.MapHub<DocumentHub>("/hubs/document");
```

---

## 4. Frontend: React Hook

```tsx
import { useEffect, useRef, useCallback } from 'react'
import * as signalR from '@microsoft/signalr'

interface UseSignalROptions {
  hubUrl: string
  onContentUpdated?: (snapshot: DocumentContentSnapshot) => void
  onUserCountChanged?: (count: number) => void
}

export function useDocumentSignalR({ hubUrl, onContentUpdated, onUserCountChanged }: UseSignalROptions) {
  const connectionRef = useRef<signalR.HubConnection | null>(null)

  useEffect(() => {
    const connection = new signalR.HubConnectionBuilder()
      .withUrl(hubUrl, {
        accessTokenFactory: () => localStorage.getItem('token') || '',
      })
      .withAutomaticReconnect()
      .build()

    // Register handlers
    if (onContentUpdated) {
      connection.on('ContentUpdated', onContentUpdated)
    }
    if (onUserCountChanged) {
      connection.on('UserCountChanged', onUserCountChanged)
    }

    // Start connection
    connection.start().catch(console.error)
    connectionRef.current = connection

    return () => {
      connection.stop()
    }
  }, [hubUrl])

  const joinDocument = useCallback((documentId: string) => {
    connectionRef.current?.invoke('JoinDocument', documentId)
  }, [])

  const updateContent = useCallback((documentId: string, content: string, version: number) => {
    connectionRef.current?.invoke('UpdateContent', documentId, content, version)
  }, [])

  return { joinDocument, updateContent }
}
```

---

## 5. Frontend: Usage

```tsx
function DocumentEditor({ documentId }: { documentId: string }) {
  const [content, setContent] = useState('')
  const [userCount, setUserCount] = useState(0)
  const [version, setVersion] = useState(0)

  const { joinDocument, updateContent } = useDocumentSignalR({
    hubUrl: '/hubs/document',
    onContentUpdated: (snapshot) => {
      setContent(snapshot.content)
      setVersion(snapshot.version)
    },
    onUserCountChanged: setUserCount,
  })

  useEffect(() => {
    joinDocument(documentId)
  }, [documentId])

  const handleChange = (newContent: string) => {
    setContent(newContent)
    setVersion(v => v + 1)
    updateContent(documentId, newContent, version + 1)
  }

  return (
    <div>
      <span>{userCount} users online</span>
      <textarea value={content} onChange={e => handleChange(e.target.value)} />
    </div>
  )
}
```

---

## 6. Common Patterns

### Debounced Updates
```tsx
const debouncedUpdate = useMemo(
  () => debounce((content: string) => {
    updateContent(documentId, content, version)
  }, 300),
  [documentId, version]
)
```

### Reconnection Handling
```tsx
connection.onreconnected(() => {
  // Rejoin groups after reconnect
  joinDocument(documentId)
})
```

---

## 7. Troubleshooting

### Connection fails
- Check CORS configuration
- Verify JWT token is sent correctly
- Check hub URL matches backend

### Messages not received
- Ensure correct method names (case-sensitive)
- Check group membership
- Verify handler registration before start()
