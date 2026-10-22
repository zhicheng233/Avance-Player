namespace Avance_Player.Services.Persistence;

public interface IPlayerStateStore {
    string? LastError { get; }
    PlayerState? Load();
    bool Save(PlayerState state);
}
