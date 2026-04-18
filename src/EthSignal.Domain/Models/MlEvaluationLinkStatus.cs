namespace EthSignal.Domain.Models;

public static class MlEvaluationLinkStatus
{
    public const string Pending = "PENDING";
    public const string SignalLinked = "SIGNAL_LINKED";
    public const string NoSignalExpected = "NO_SIGNAL_EXPECTED";
    public const string MlFiltered = "ML_FILTERED";
    public const string OperationallyBlocked = "OPERATIONALLY_BLOCKED";
}
