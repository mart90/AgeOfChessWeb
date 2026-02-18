namespace AgeOfChess.Server.Services;

public record ChatMessageDto(string SenderName, string Message, long TimestampMs);
