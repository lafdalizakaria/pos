using CommunityToolkit.Mvvm.ComponentModel;

namespace Newrest.Pos.Client.Core.ViewModels;

/// <summary>Runs code on the UI thread (WPF Dispatcher in the app, inline in tests).</summary>
public interface IUiDispatcher
{
    void Post(Action action);
}

public sealed class InlineDispatcher : IUiDispatcher
{
    public void Post(Action action) => action();
}

/// <summary>Base page: busy flag and French messages.</summary>
public abstract partial class PageViewModel : ObservableObject
{
    [ObservableProperty]
    private string? _error;

    [ObservableProperty]
    private string? _message;

    [ObservableProperty]
    private bool _isBusy;

    public abstract string Title { get; }

    protected async Task<bool> RunAsync(Func<Task> action)
    {
        IsBusy = true;
        Error = null;
        try
        {
            await action();
            return true;
        }
        catch (Domain.Common.DomainException ex)
        {
            Error = ex.Message;
            return false;
        }
        catch (Api.ServerRejectedException ex)
        {
            Error = $"Refusé par le serveur ({ex.Code}). {ex.Detail}";
            return false;
        }
        catch (Api.ServerUnreachableException)
        {
            Error = "Serveur injoignable.";
            return false;
        }
        catch (InvalidOperationException ex)
        {
            Error = ex.Message;
            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }
}
