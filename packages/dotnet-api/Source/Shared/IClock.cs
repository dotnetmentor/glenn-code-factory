namespace Source.Shared;

/// <summary>
/// Abstraction over the system clock so tests can inject deterministic time.
/// </summary>
public interface IClock
{
    DateTime UtcNow { get; }
}

/// <summary>
/// Default production implementation that reads from <see cref="DateTime.UtcNow"/>.
/// </summary>
public class SystemClock : IClock
{
    public DateTime UtcNow => DateTime.UtcNow;
}
