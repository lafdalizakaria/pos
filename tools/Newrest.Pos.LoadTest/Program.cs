using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Newrest.Pos.Contracts.V1;
using Newrest.Pos.Contracts.V1.Sync;
using Newrest.Pos.Domain.Accounts;
using Newrest.Pos.Domain.Sales;

// Load test of the central API: N virtual registers selling in parallel (reference download, session, badge lookups,
// online account debits, chained tickets, heartbeats), as on a lunch peak. Never against production.
//   dotnet run --project tools/Newrest.Pos.LoadTest -c Release -- --api http://127.0.0.1:18080 --dev-signing-key <key>
//        [--registers 40] [--trays 30] [--think-ms 0] [--audience newrest-pos-api] [--report docs/load-test-results.md]
var options = Options.Parse(args);
using var http = new HttpClient { BaseAddress = new Uri(options.Api, "/api/v1/"), Timeout = TimeSpan.FromSeconds(60) };
var metrics = new Metrics();
var admin = Jwt.DevAdmin(options.DevSigningKey, options.Audience);

Console.WriteLine($"Préparation de {options.Registers} caisse(s) virtuelle(s)…");
var pointsOfSale = await Get<List<PointOfSaleDto>>(http, "points-of-sale", admin);
var target = pointsOfSale.First(p => p.Code == "CAS-SELF");
var run = DateTime.UtcNow.ToString("HHmmss", CultureInfo.InvariantCulture);
var registers = new List<VirtualRegister>();
for (var i = 0; i < options.Registers; i++)
{
    var prefix = $"L{run}{i:D3}";
    var created = await Send<RegisterDto>(http, HttpMethod.Post, "registers", admin, new RegisterUpsert(target.Id, prefix, $"Charge {i}", prefix));
    var key = await Send<DeviceKeyIssued>(http, HttpMethod.Post, $"registers/{created.Id}/device-key", admin, null);
    registers.Add(new VirtualRegister(created.Id, prefix, key.DeviceKey));
}

// Generous balances so that the account debit path (row locks on the ledger) is exercised throughout the run.
foreach (var account in await VirtualRegister.AccountsAsync(http, registers[0]))
{
    await Send<LedgerResultDto>(http, HttpMethod.Post, $"accounts/{account}/top-ups", admin,
        new TopUpRequest(100_000m, "BankTransfer", Guid.NewGuid(), "Test de charge"));
}

Console.WriteLine($"Vente : {options.Trays} plateau(x) par caisse, pause {options.ThinkMs} ms…");
var clock = Stopwatch.StartNew();
await Task.WhenAll(registers.Select(r => r.RunAsync(http, options, metrics)));
clock.Stop();

var report = metrics.Report(options, clock.Elapsed);
Console.WriteLine(report);
if (options.ReportPath is { } path)
{
    await File.WriteAllTextAsync(path, report);
}

return metrics.Errors.IsEmpty ? 0 : 1;

static async Task<T> Get<T>(HttpClient http, string path, string token) => await Send<T>(http, HttpMethod.Get, path, token, null);

static async Task<T> Send<T>(HttpClient http, HttpMethod method, string path, string token, object? body)
{
    using var request = new HttpRequestMessage(method, path);
    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
    if (body is not null)
    {
        request.Content = JsonContent.Create(body, body.GetType());
    }

    using var response = await http.SendAsync(request);
    if (!response.IsSuccessStatusCode)
    {
        throw new InvalidOperationException($"{method} {path}: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
    }

    return response.StatusCode == System.Net.HttpStatusCode.NoContent ? default! : (await response.Content.ReadFromJsonAsync<T>())!;
}

internal sealed record Options(Uri Api, string DevSigningKey, string Audience, int Registers, int Trays, int ThinkMs, string? ReportPath)
{
    public static Options Parse(string[] args)
    {
        string? Value(string name, string? fallback = null) => Array.IndexOf(args, name) is var i and >= 0 ? args[i + 1] : fallback;
        return new Options(new Uri(Value("--api", "http://127.0.0.1:18080")!),
            Value("--dev-signing-key") ?? throw new ArgumentException("--dev-signing-key (non-production API with Authentication:Users:DevSigningKey)"),
            Value("--audience", "newrest-pos-api")!, int.Parse(Value("--registers", "40")!, CultureInfo.InvariantCulture),
            int.Parse(Value("--trays", "30")!, CultureInfo.InvariantCulture), int.Parse(Value("--think-ms", "0")!, CultureInfo.InvariantCulture),
            Value("--report"));
    }
}

internal static class Jwt
{
    /// <summary>HS256 token accepted by a non-production API configured with a development signing key.</summary>
    public static string DevAdmin(string key, string audience)
    {
        static string B64(byte[] data) => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var header = B64(JsonSerializer.SerializeToUtf8Bytes(new { alg = "HS256", typ = "JWT" }));
        var payload = B64(JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>
        {
            ["iss"] = "newrest-pos-dev",
            ["aud"] = audience,
            ["preferred_username"] = "loadtest@newrest.ma",
            ["roles"] = PosRoles.Admin,
            ["nbf"] = now - 60,
            ["iat"] = now,
            ["exp"] = now + 7200,
        }));
        var signature = B64(HMACSHA256.HashData(Encoding.UTF8.GetBytes(key), Encoding.ASCII.GetBytes($"{header}.{payload}")));
        return $"{header}.{payload}.{signature}";
    }
}

internal sealed class Metrics
{
    private readonly ConcurrentDictionary<string, ConcurrentBag<double>> _latencies = new();

    public ConcurrentDictionary<string, int> Errors { get; } = new();

    public ConcurrentDictionary<string, int> Refusals { get; } = new();

    public int Trays;

    public async Task<T> TimeAsync<T>(string operation, Func<Task<T>> call)
    {
        var watch = Stopwatch.StartNew();
        try
        {
            return await call();
        }
        finally
        {
            _latencies.GetOrAdd(operation, _ => []).Add(watch.Elapsed.TotalMilliseconds);
        }
    }

    public string Report(Options options, TimeSpan elapsed)
    {
        var sb = new StringBuilder();
        var fr = CultureInfo.GetCultureInfo("fr-FR");
        sb.AppendLine(CultureInfo.InvariantCulture, $"## Résultats — {options.Registers} caisses × {options.Trays} plateaux, pause {options.ThinkMs} ms");
        sb.AppendLine();
        sb.AppendLine(string.Create(fr, $"Durée {elapsed.TotalSeconds:0.0} s — {Trays} plateaux — **{Trays / elapsed.TotalSeconds:0.0} plateaux/s** "
                                        + $"({Trays / elapsed.TotalMinutes:0} /min)"));
        sb.AppendLine();
        sb.AppendLine("| Opération | Appels | p50 (ms) | p95 (ms) | p99 (ms) | max (ms) |");
        sb.AppendLine("|---|---:|---:|---:|---:|---:|");
        foreach (var (name, values) in _latencies.OrderBy(kv => kv.Key))
        {
            var sorted = values.Order().ToArray();
            double P(double q) => sorted[Math.Min(sorted.Length - 1, (int)Math.Ceiling(q * sorted.Length) - 1)];
            sb.AppendLine(string.Create(fr, $"| {name} | {sorted.Length} | {P(0.5):0} | {P(0.95):0} | {P(0.99):0} | {sorted[^1]:0} |"));
        }

        sb.AppendLine();
        sb.AppendLine("Refus métier attendus (solde insuffisant, etc.) : " + (Refusals.IsEmpty ? "aucun" : string.Join(", ", Refusals.Select(kv => $"{kv.Key} × {kv.Value}"))));
        sb.AppendLine();
        sb.AppendLine("Erreurs : " + (Errors.IsEmpty ? "**aucune**" : string.Join(", ", Errors.Select(kv => $"{kv.Key} × {kv.Value}"))));
        return sb.ToString();
    }
}

internal sealed class VirtualRegister(Guid id, string prefix, string deviceKey)
{
    public static async Task<IReadOnlyList<Guid>> AccountsAsync(HttpClient http, VirtualRegister register)
    {
        using var token = await http.PostAsJsonAsync("auth/register-token", new RegisterTokenRequest(register.Id, register.DeviceKey));
        register._token = (await token.Content.ReadFromJsonAsync<RegisterTokenResponse>())!.AccessToken;
        var reference = await register.Call<ReferenceSyncResponse>(http, HttpMethod.Get, "register/reference");
        return [.. reference.Accounts.Where(a => a.IsActive).Select(a => a.Id)];
    }

    public Guid Id => id;

    public string DeviceKey => deviceKey;

    private string _token = "";
    private long _sequence;
    private string _previousHash = TicketHasher.GenesisHash;

    public async Task RunAsync(HttpClient http, Options options, Metrics metrics)
    {
        try
        {
            var token = await metrics.TimeAsync("auth/register-token", async () =>
            {
                using var response = await http.PostAsJsonAsync("auth/register-token", new RegisterTokenRequest(id, deviceKey));
                response.EnsureSuccessStatusCode();
                return (await response.Content.ReadFromJsonAsync<RegisterTokenResponse>())!;
            });
            _token = token.AccessToken;
            var reference = await metrics.TimeAsync("register/reference (complet)", () => Call<ReferenceSyncResponse>(http, HttpMethod.Get, "register/reference"));
            var cashier = reference.Operators.First(o => o.Code == "CAIS01");
            var businessDate = DateOnly.FromDateTime(DateTime.UtcNow);
            var menu = reference.Menus.FirstOrDefault(m => m.Date == businessDate)?.Items ?? [];
            var articles = reference.Articles.ToDictionary(a => a.Id);
            var badges = reference.Badges.Where(b => b.Status == "Active").ToList();
            var accounts = reference.Accounts.ToLookup(a => a.DinerId);
            var session = new CashSessionSyncDto(Guid.NewGuid(), cashier.Id, DateTimeOffset.UtcNow, businessDate, 200m);
            await metrics.TimeAsync("register/cash-sessions", () => Call<SyncAck>(http, HttpMethod.Post, "register/cash-sessions", session));
            var random = new Random(id.GetHashCode());

            for (var tray = 0; tray < options.Trays; tray++)
            {
                var items = Enumerable.Range(0, random.Next(1, 4)).Select(_ => menu[random.Next(menu.Count)]).ToList();
                var lines = items.Select(i => new TicketLineInput(i.ArticleId, i.ArticleCode, i.ArticleName, 1, i.EffectivePrice,
                    articles[i.ArticleId].VatRate, articles[i.ArticleId].IsSubsidizable)).ToList();
                var total = lines.Sum(l => l.UnitPrice);
                var badge = badges[random.Next(badges.Count)];
                await metrics.TimeAsync("register/badges", () => Call<BadgeContextDto>(http, HttpMethod.Get,
                    $"register/badges/{badge.Number}?businessDate={businessDate:yyyy-MM-dd}"));
                var account = accounts[badge.DinerId].FirstOrDefault();
                var ticketId = Guid.NewGuid();
                PaymentInput payment = new(PaymentMethod.Cash, total) { Tendered = Math.Ceiling(total) };
                if (account is not null && random.Next(3) > 0)
                {
                    try
                    {
                        var idempotencyKey = Guid.NewGuid(); // the payment references the movement by its client-generated key
                        await metrics.TimeAsync("register/account-movements (débit)", () => Call<LedgerResultDto>(http, HttpMethod.Post,
                            "register/account-movements", new AccountMovementSyncDto(idempotencyKey, account.Id, "Consumption", -total, DateTimeOffset.UtcNow,
                                "Account", ticketId, cashier.Id, null, false)));
                        payment = new PaymentInput(PaymentMethod.Account, total) { AccountMovementId = idempotencyKey };
                    }
                    catch (RefusedException ex)
                    {
                        metrics.Refusals.AddOrUpdate(ex.Code, 1, (_, n) => n + 1);
                    }
                }

                var ticket = Ticket.Issue(new TicketIssueRequest
                {
                    Id = ticketId,
                    RegisterId = id,
                    RegisterPrefix = prefix,
                    Sequence = ++_sequence,
                    PreviousHash = _previousHash,
                    CashSessionId = session.Id,
                    OperatorId = cashier.Id,
                    BusinessDate = businessDate,
                    IssuedAt = DateTimeOffset.UtcNow,
                    Lines = lines,
                    Payments = [payment],
                    DinerId = payment.Method == PaymentMethod.Account ? badge.DinerId : null,
                    AccountId = payment.Method == PaymentMethod.Account ? account!.Id : null,
                    BadgeNumber = payment.Method == PaymentMethod.Account ? badge.Number : null,
                });
                _previousHash = ticket.Hash;
                await metrics.TimeAsync("register/tickets", () => Call<SyncAck>(http, HttpMethod.Post, "register/tickets", ticket.ToSyncDto()));
                Interlocked.Increment(ref metrics.Trays);
                if (tray % 10 == 0)
                {
                    await metrics.TimeAsync("register/heartbeat", () => Call<JsonElement?>(http, HttpMethod.Post, "register/heartbeat",
                        new RegisterHeartbeatDto("loadtest", 0, null, null, _sequence, session.OpenedAt, DateTimeOffset.UtcNow)));
                }

                if (options.ThinkMs > 0)
                {
                    await Task.Delay(random.Next(options.ThinkMs / 2, options.ThinkMs * 3 / 2));
                }
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TaskCanceledException or RefusedException)
        {
            metrics.Errors.AddOrUpdate($"{prefix}: {ex.Message.Split('\n')[0][..Math.Min(120, ex.Message.Length)]}", 1, (_, n) => n + 1);
        }
    }

    private async Task<T> Call<T>(HttpClient http, HttpMethod method, string path, object? body = null)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, body.GetType());
        }

        using var response = await http.SendAsync(request);
        if ((int)response.StatusCode is >= 400 and < 500)
        {
            var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
            throw new RefusedException(problem.TryGetProperty("code", out var code) ? code.GetString()! : $"http_{(int)response.StatusCode}");
        }

        response.EnsureSuccessStatusCode();
        return response.StatusCode == System.Net.HttpStatusCode.NoContent ? default! : (await response.Content.ReadFromJsonAsync<T>())!;
    }
}

internal sealed class RefusedException(string code) : Exception(code)
{
    public string Code { get; } = code;
}
