using System.Runtime.CompilerServices;
using QuizGame.Server.Domain;

namespace QuizGame.Server.Services;

/// <summary>
/// One command at a time per room. State changes and their delivery run inside the gate, so a reconnect
/// snapshot can never overtake the events of the change that preceded it. Not reentrant: code that already runs
/// inside the gate must call the <c>…Core</c> variants instead of taking it again.
/// </summary>
public sealed class RoomCommandGate
{
    private readonly ConditionalWeakTable<GameRoom, SemaphoreSlim> _gates = new();

    public async Task<T> RunAsync<T>(GameRoom room, Func<Task<T>> action)
    {
        var gate = _gates.GetValue(room, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();
        try { return await action(); }
        finally { gate.Release(); }
    }
}
