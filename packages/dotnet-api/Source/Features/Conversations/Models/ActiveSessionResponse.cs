namespace Source.Features.Conversations.Models;

public record ActiveSessionResponse(
    Guid SessionId,
    Guid ConversationId,
    string Prompt,
    string? AgentId);
