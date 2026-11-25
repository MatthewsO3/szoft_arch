namespace QuizGame.Server.Hubs;

/// <summary>SignalR group names (plan §6.3): rooms = groups.</summary>
public static class GroupNames
{
    public static string Room(string code) => $"room:{code}";
    public static string Gm(string code) => $"room:{code}:gm";
    public static string Players(string code) => $"room:{code}:players";
}
