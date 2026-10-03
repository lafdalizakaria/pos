using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Newrest.Pos.BackOffice.Security;
using Newrest.Pos.BackOffice.Ui;
using Newrest.Pos.Contracts.V1;

namespace Newrest.Pos.BackOffice.Components.Shared;

/// <summary>Base page: runs use cases in a fresh scope, shows French success / error messages, exposes roles.</summary>
public abstract class PageBase : ComponentBase
{
    [Inject]
    protected BackOfficeRunner Runner { get; set; } = null!;

    [Inject]
    protected ILogger<PageBase> Logger { get; set; } = null!;

    [CascadingParameter]
    private Task<AuthenticationState>? AuthenticationState { get; set; }

    protected string? Error { get; set; }

    protected string? Success { get; set; }

    protected bool Busy { get; private set; }

    protected System.Security.Claims.ClaimsPrincipal User { get; private set; } = new();

    protected bool IsAdmin => User.IsInRole(PosRoles.Admin);

    protected bool CanWrite => IsAdmin || User.IsInRole(PosRoles.Manager);

    protected bool CanHandleMoney => CanWrite || User.IsInRole(PosRoles.Accountant);

    protected bool IsFinance => IsAdmin || User.IsInRole(PosRoles.Accountant);

    protected override async Task OnInitializedAsync()
    {
        if (AuthenticationState is not null)
        {
            User = (await AuthenticationState).User;
        }

        await LoadAsync();
    }

    protected virtual Task LoadAsync() => Task.CompletedTask;

    protected async Task<T?> Run<TService, T>(Func<TService, Task<T>> action, string? success = null)
        where TService : notnull
    {
        Busy = true;
        Error = null;
        if (success is not null)
        {
            // Read-only calls keep the previous confirmation visible (e.g. reload after a top-up).
            Success = null;
        }

        try
        {
            var result = await Runner.RunAsync(action);
            if (success is not null)
            {
                Success = success;
            }

            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (ex is not Domain.Common.DomainException)
            {
                LogUnexpected(Logger, ex);
            }

            Error = Messages.For(ex);
            Success = null;
            return default;
        }
        finally
        {
            Busy = false;
        }
    }

    protected async Task<bool> Run<TService>(Func<TService, Task> action, string? success = null)
        where TService : notnull =>
        await Run<TService, bool>(async s =>
        {
            await action(s);
            return true;
        }, success);

    private static void LogUnexpected(ILogger logger, Exception ex) => logger.LogError(ex, "Unexpected back-office error");
}
