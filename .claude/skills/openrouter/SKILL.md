# OpenRouter AI Chat - Complete Reference

## Overview
Whenever the user needs AI functionality, chatbot or AI assistant, use this skill.
This codebase provides **minimal-code AI chat integration** via OpenRouter with:
- **Backend**: SSE streaming with automatic tool execution via `OpenRouter.NET` SDK
- **Frontend**: React hook for SSE consumption via `@openrouter-dotnet/react` SDK

Both SDKs handle all complexity - you write minimal code.

---

## Quick Start: Secrets

**Secrets are hardcoded in `appsettings.json` for quick bootstrapping:**

```json
{
  "OpenRouter": {
    "ApiKey": "sk-or-v1-your-key-here"
  }
}
```

Get your key at https://openrouter.ai/keys

---

## 1. Backend: Minimal Streaming Endpoint

The SDK handles SSE formatting, tool execution, and response building. You just call one method:

```csharp
using OpenRouter.NET;
using OpenRouter.NET.Models;
using OpenRouter.NET;  // REQUIRED for RegisterTool<T>()
using OpenRouter.NET.Sse;    // REQUIRED for StreamAsSseAsync()

[ApiController]
[Route("api/chat")]
public class ChatController : ControllerBase
{
    private readonly OpenRouterService _openRouter;

    public ChatController(OpenRouterService openRouter)
    {
        _openRouter = openRouter;
    }

    [HttpPost("stream")]
    public async Task StreamChat([FromBody] ChatRequest request)
    {
        // Create client (new instance needed to register tools per-request)
        var client = _openRouter.CreateClient();

        // Register tools (optional) - SDK auto-executes them!
        client.RegisterTool<WeatherTool>();
        client.RegisterTool<CalculatorTool>();

        // Build messages
        var messages = new List<Message>();
        if (request.Messages != null)
        {
            foreach (var m in request.Messages)
            {
                messages.Add(m.Role.ToLower() switch
                {
                    "system" => Message.FromSystem(m.Content),
                    "assistant" => Message.FromAssistant(m.Content),
                    _ => Message.FromUser(m.Content)
                });
            }
        }
        messages.Add(Message.FromUser(request.Message));

        var chatRequest = new ChatCompletionRequest
        {
            Model = request.Model ?? "anthropic/claude-sonnet-4",
            Messages = messages
        };

        // ONE LINE handles everything: SSE streaming, tool execution, response
        var result = await client.StreamAsSseAsync(chatRequest, Response);

        // Optional: Log telemetry
        _logger.LogInformation("Tokens: {Tokens}, TTFT: {TTFT}ms",
            result.Usage?.TotalTokens,
            result.TimeToFirstToken?.TotalMilliseconds);
    }
}

public record ChatRequest(
    string Message,
    string? Model = null,
    List<MessageDto>? Messages = null
);

public record MessageDto(string Role, string Content);
```

---

## 2. Backend: Class-Based Tools (No JSON Schema!)

Define tools as C# classes - the SDK generates JSON schema automatically:

### Tool with Return Value

```csharp
using OpenRouter.NET;

// Step 1: Define parameters
public class WeatherParams
{
    public string Location { get; set; } = string.Empty;
    public string Unit { get; set; } = "celsius";
}

// Step 2: Define result
public class WeatherResult
{
    public string Location { get; set; } = string.Empty;
    public double Temperature { get; set; }
    public string Condition { get; set; } = string.Empty;
}

// Step 3: Implement tool
public class WeatherTool : Tool<WeatherParams, WeatherResult>
{
    public override string Name => "get_weather";
    public override string Description => "Get current weather for a location";

    protected override WeatherResult Handle(WeatherParams p)
    {
        // Your implementation - call weather API, database, etc.
        return new WeatherResult
        {
            Location = p.Location,
            Temperature = 22,
            Condition = "Sunny"
        };
    }
}
```

### Void Tool (No Return Value)

```csharp
using OpenRouter.NET;

public class NotifyParams
{
    public string Message { get; set; } = string.Empty;
}

public class NotifyTool : VoidTool<NotifyParams>
{
    public override string Name => "send_notification";
    public override string Description => "Send a notification";

    protected override void HandleVoid(NotifyParams p)
    {
        Console.WriteLine($"Notification: {p.Message}");
    }
}
```

### Client-Side Tool (Frontend Executes)

```csharp
using OpenRouter.NET;

public class SetFiltersParams
{
    public string Category { get; set; } = string.Empty;
    public int MinPrice { get; set; }
}

public class SetFiltersTool : VoidTool<SetFiltersParams>
{
    public override string Name => "set_filters";
    public override string Description => "Set search filters in the UI";
    public override ToolMode Mode => ToolMode.ClientSide;  // Key difference!

    protected override void HandleVoid(SetFiltersParams p)
    {
        // Not called on server - frontend handles via onClientTool callback
    }
}
```

---

## 3. Frontend: Minimal React Integration

The SDK handles SSE parsing, message state, streaming updates:

```tsx
import { useOpenRouterChat, getTextContent } from '@openrouter-dotnet/react'

function ChatComponent() {
  const [input, setInput] = useState('')

  const { state, actions } = useOpenRouterChat({
    endpoints: {
      stream: '/api/chat/stream',
    },
  })

  const { messages, isStreaming, error } = state
  const { sendMessage, clearConversation, cancelStream } = actions

  const handleSend = async () => {
    if (!input.trim() || isStreaming) return
    await sendMessage(input, { model: 'anthropic/claude-sonnet-4' })
    setInput('')
  }

  return (
    <div>
      {messages.map(msg => (
        <div key={msg.id}>
          <strong>{msg.role}:</strong>
          <p>{getTextContent(msg)}</p>
        </div>
      ))}

      {error && <p className="error">{error.message}</p>}

      <input
        value={input}
        onChange={e => setInput(e.target.value)}
        disabled={isStreaming}
      />
      <button onClick={handleSend} disabled={isStreaming}>
        {isStreaming ? 'Streaming...' : 'Send'}
      </button>
      <button onClick={cancelStream} disabled={!isStreaming}>
        Cancel
      </button>
      <button onClick={clearConversation}>Clear</button>
    </div>
  )
}
```

---

## 4. Frontend: Handling Tools and Artifacts

```tsx
import {
  useOpenRouterChat,
  getTextContent,
  getToolCallBlocks,
  getArtifactBlocks
} from '@openrouter-dotnet/react'

function AdvancedChat() {
  const { state, actions } = useOpenRouterChat({
    endpoints: { stream: '/api/chat/stream' },

    // Handle client-side tools (executed in browser)
    onClientTool: (event) => {
      const args = JSON.parse(event.arguments)
      switch (event.toolName) {
        case 'set_filters':
          setFilters(args)  // Update your UI state
          break
        case 'navigate':
          router.push(args.path)
          break
      }
    },

    // Handle completion
    onCompleted: (event) => {
      console.log('Done:', event.finishReason)
    },

    // Handle artifacts (code blocks, documents)
    onArtifactCompleted: (event) => {
      console.log('Artifact:', event.title, event.content)
    },
  })

  return (
    <div>
      {state.messages.map(msg => (
        <div key={msg.id}>
          {/* Text content */}
          <p>{getTextContent(msg)}</p>

          {/* Tool calls */}
          {getToolCallBlocks(msg).map(tool => (
            <div key={tool.id}>
              Tool: {tool.toolName} - {tool.status}
              {tool.result && <pre>{tool.result}</pre>}
            </div>
          ))}

          {/* Artifacts */}
          {getArtifactBlocks(msg).map(artifact => (
            <div key={artifact.id}>
              <h4>{artifact.title}</h4>
              <pre><code>{artifact.content}</code></pre>
            </div>
          ))}
        </div>
      ))}
    </div>
  )
}
```

---

## 5. OpenRouterService (Infrastructure)

Already configured in `Source/Infrastructure/Services/OpenRouter/`:

```csharp
public class OpenRouterService
{
    private readonly OpenRouterClient _client;
    private readonly string _apiKey;

    public OpenRouterService(IConfiguration configuration, ILogger<OpenRouterService> logger)
    {
        _apiKey = configuration["OpenRouter:ApiKey"]
            ?? throw new InvalidOperationException("OpenRouter:ApiKey is required");
        _client = new OpenRouterClient(_apiKey);
    }

    // Shared client (no tools)
    public OpenRouterClient Client => _client;

    // New client per request (for tool registration)
    public OpenRouterClient CreateClient() => new OpenRouterClient(_apiKey);
}
```

Registered in DI via `AddOfflineFirstServices()`.

---

## 6. SSE Event Types

Events sent to frontend during streaming:

| Event Type | Description | Key Fields |
|------------|-------------|------------|
| `text` | Text content chunk | `textDelta` |
| `tool_executing` | Server tool starting | `toolName`, `arguments` |
| `tool_completed` | Server tool done | `toolName`, `result` |
| `tool_error` | Server tool failed | `toolName`, `error` |
| `tool_client` | Client tool requested | `toolName`, `arguments`, `toolId` |
| `artifact_started` | Artifact beginning | `artifactId`, `type`, `title` |
| `artifact_content` | Artifact chunk | `artifactId`, `contentDelta` |
| `artifact_completed` | Artifact done | `artifactId`, `content` |
| `completion` | Stream finished | `finishReason` |
| `error` | Error occurred | `message`, `details` |

---

## 7. Popular Models (2025)

```csharp
// Anthropic
"anthropic/claude-opus-4.5"    // Most capable
"anthropic/claude-sonnet-4"    // Best coding
"anthropic/claude-haiku-4"     // Fast & cheap

// OpenAI
"openai/gpt-4o"                // Multimodal flagship
"openai/gpt-4o-mini"           // Cost-effective
"openai/o3-mini"               // Budget reasoning

// Google
"google/gemini-2.5-pro"        // Top benchmark (1M context)
"google/gemini-2.5-flash"      // Fast reasoning

// DeepSeek
"deepseek/deepseek-r1"         // Open reasoning
```

---

## 8. Required Using Statements

**CRITICAL: Missing these causes "method not found" errors!**

```csharp
// Always required
using OpenRouter.NET;
using OpenRouter.NET.Models;

// Required for RegisterTool<T>()
using OpenRouter.NET;

// Required for StreamAsSseAsync()
using OpenRouter.NET.Sse;

// Required for artifacts
using OpenRouter.NET.Artifacts;
```

---

## 9. Non-Streaming (Simple Request/Response)

For one-off completions without streaming:

```csharp
var request = new ChatCompletionRequest
{
    Model = "anthropic/claude-sonnet-4",
    Messages = new List<Message>
    {
        Message.FromSystem("You are helpful."),
        Message.FromUser("What is 2+2?")
    }
};

var response = await _openRouter.Client.CreateChatCompletionAsync(request);
var answer = response.Choices[0].Message.Content?.ToString();
```

---

## 10. Troubleshooting

### "method cannot be used with type arguments"
**Cause**: Missing `using OpenRouter.NET;`

### "does not contain a definition for StreamAsSseAsync"
**Cause**: Missing `using OpenRouter.NET.Sse;`

### Tools not being called
**Cause**: Tools registered after creating the request
**Fix**: Register tools BEFORE creating ChatCompletionRequest

### Client receives empty stream
**Cause**: Exception thrown before streaming starts
**Fix**: Wrap in try/catch and log errors

---

## 11. Complete Minimal Example

### Backend (one endpoint)

```csharp
[HttpPost("stream")]
public async Task Stream([FromBody] ChatRequest request)
{
    var client = _openRouter.CreateClient();
    client.RegisterTool<WeatherTool>();

    var chatRequest = new ChatCompletionRequest
    {
        Model = request.Model ?? "anthropic/claude-sonnet-4",
        Messages = new List<Message> { Message.FromUser(request.Message) }
    };

    await client.StreamAsSseAsync(chatRequest, Response);
}
```

### Frontend (one component)

```tsx
const { state, actions } = useOpenRouterChat({
  endpoints: { stream: '/api/chat/stream' },
})

// Send: await actions.sendMessage("Hello", { model: "..." })
// Display: state.messages.map(m => getTextContent(m))
// Cancel: actions.cancelStream()
```

**That's it.** The SDKs handle everything else.
