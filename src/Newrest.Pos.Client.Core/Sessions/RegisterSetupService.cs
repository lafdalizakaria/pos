using Microsoft.EntityFrameworkCore;
using Newrest.Pos.Client.Core.Api;
using Newrest.Pos.Client.Core.Configuration;
using Newrest.Pos.Client.Core.Local;
using Newrest.Pos.Client.Core.Sync;
using Newrest.Pos.Contracts.V1.Sync;

namespace Newrest.Pos.Client.Core.Sessions;

/// <summary>First installation (server URL + register id + device key) and loading at startup.</summary>
public sealed class RegisterSetupService(LocalStore store, PosApiClient api, IDeviceKeyStore keys, RegisterOptions options, SyncService sync)
{
    public RegisterProfileDto? Profile { get; private set; }

    /// <summary>Loads the stored registration. Returns false when the register still has to be registered.</summary>
    public async Task<bool> LoadAsync(CancellationToken ct = default)
    {
        await store.InitializeAsync(ct);
        Profile = await store.GetSettingAsync<RegisterProfileDto>(SettingKeys.Profile, ct);
        if (Profile is null || keys.Load() is null)
        {
            return false;
        }

        options.ServerUrl ??= new Uri(await store.GetSettingAsync(SettingKeys.ServerUrl, ct) ?? throw new InvalidOperationException("Server URL missing."));
        api.RegisterId = Profile.RegisterId;
        return true;
    }

    /// <summary>
    /// Registers this machine: validates the device key against the server, downloads the profile and the reference
    /// data. On a reinstalled register, the ticket chain continues from the last ticket known by the server.
    /// </summary>
    public async Task<RegisterProfileDto> RegisterAsync(Uri serverUrl, Guid registerId, string deviceKey, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(serverUrl);
        if (serverUrl.Scheme != Uri.UriSchemeHttps && !serverUrl.IsLoopback)
        {
            throw new InvalidOperationException("Le serveur doit être joint en HTTPS.");
        }

        await store.InitializeAsync(ct);
        options.ServerUrl = serverUrl;
        api.RegisterId = registerId;
        keys.Save(deviceKey.Trim());
        await api.AuthenticateAsync(ct);
        var profile = await api.GetProfileAsync(ct);

        await using (var db = store.Open())
        {
            var state = await db.RegisterStates.SingleAsync(ct);
            var hasLocalTickets = await db.Tickets.AnyAsync(ct);
            if (!hasLocalTickets && profile.LastSyncedTicketSequence > state.LastSequence)
            {
                state.LastSequence = profile.LastSyncedTicketSequence;
                state.LastHash = profile.LastSyncedTicketHash ?? state.LastHash;
                state.LastZNumber = profile.LastZNumber;
                await db.SaveChangesAsync(ct);
            }
        }

        await store.SetSettingAsync(SettingKeys.ServerUrl, serverUrl.ToString(), ct);
        await store.SetSettingAsync(SettingKeys.RegisterId, registerId.ToString(), ct);
        await store.SetSettingAsync(SettingKeys.Profile, profile, ct);
        Profile = profile;
        await sync.PullReferenceAsync(ct);
        return profile;
    }

    /// <summary>Refreshes the fiscal mentions (company, site) when online.</summary>
    public async Task RefreshProfileAsync(CancellationToken ct = default)
    {
        try
        {
            Profile = await api.GetProfileAsync(ct);
            await store.SetSettingAsync(SettingKeys.Profile, Profile, ct);
        }
        catch (ServerUnreachableException)
        {
        }
    }
}
