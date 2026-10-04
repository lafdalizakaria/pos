using System.Diagnostics.Metrics;

namespace Newrest.Pos.Application.Operations;

/// <summary>Business metrics exported with OpenTelemetry (meter <c>Newrest.Pos</c>).</summary>
public static class PosMetrics
{
    public const string MeterName = "Newrest.Pos";
    private static readonly Meter Meter = new(MeterName);
    private static readonly Dictionary<string, int> AlertCounts = new() { ["Critical"] = 0, ["Warning"] = 0, ["Info"] = 0 };
    private static int _silentRegisters;

    public static readonly Counter<long> TicketsIngested = Meter.CreateCounter<long>("pos.tickets.ingested", description: "Fiscal tickets received from registers");
    public static readonly Counter<long> LedgerMovements = Meter.CreateCounter<long>("pos.ledger.movements", description: "Account movements recorded");
    public static readonly Counter<long> SyncRejections = Meter.CreateCounter<long>("pos.sync.rejections", description: "Register items refused by the server");
    public static readonly Counter<long> Heartbeats = Meter.CreateCounter<long>("pos.registers.heartbeats");

    static PosMetrics()
    {
        Meter.CreateObservableGauge("pos.alerts.active", () =>
        {
            lock (AlertCounts)
            {
                return AlertCounts.Select(kv => new Measurement<int>(kv.Value, new KeyValuePair<string, object?>("severity", kv.Key))).ToList();
            }
        }, description: "Active supervision alerts by severity");
        Meter.CreateObservableGauge("pos.registers.silent", () => Volatile.Read(ref _silentRegisters), description: "Open registers without news");
    }

    public static void SetAlerts(IEnumerable<(string Severity, string Code)> alerts)
    {
        var list = alerts.ToList();
        lock (AlertCounts)
        {
            foreach (var key in AlertCounts.Keys.ToList())
            {
                AlertCounts[key] = list.Count(a => a.Severity == key);
            }
        }

        Volatile.Write(ref _silentRegisters, list.Count(a => a.Code == "register_silent"));
    }
}
