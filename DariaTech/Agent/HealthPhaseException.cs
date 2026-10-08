namespace DariaTech.Agent;

// Startup verification failure with the phase that did not complete, for actionable installer diagnostics.
public sealed class HealthPhaseException(string phase,string? lastError,DateTimeOffset since):Exception($"Startup phase {phase} not completed")
{
 public string Phase {get;}=phase; public string? LastError {get;}=lastError; public TimeSpan Waited {get;}=DateTimeOffset.UtcNow-since;
}
