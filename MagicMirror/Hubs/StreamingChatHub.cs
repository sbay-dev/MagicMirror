using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;

namespace MagicMirror.Hubs;

/// <summary>
/// Streaming chat hub — demonstrates IAsyncEnumerable streaming over SignalR.
/// Messages are streamed character-by-character to simulate typing effect.
/// </summary>
public class StreamingChatHub : Hub
{
    /// <summary>
    /// Streams a message character-by-character to all connected clients.
    /// </summary>
    public async IAsyncEnumerable<string> StreamMessage(string user, string message)
    {
        // Notify all clients that a new message stream is starting
        await Clients.All.SendAsync("StreamStart", user);

        foreach (var ch in message)
        {
            yield return ch.ToString();
            await Task.Delay(30); // typing effect delay
        }
    }

    /// <summary>
    /// Sends a complete message instantly (non-streaming fallback).
    /// </summary>
    public async Task SendMessage(string user, string message)
    {
        await Clients.All.SendAsync("ReceiveMessage", user, message);
    }
}