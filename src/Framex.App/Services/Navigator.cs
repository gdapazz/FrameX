namespace Framex.App.Services;

public enum PageKey
{
    Dashboard,
    Games,
    Hardware,
    SetupScore,
    Reports,
    Customization,
    Settings,
    About,
}

public sealed record NavigationRequest(PageKey Page, Guid? SessionId = null, Guid? CompareWith = null);

/// <summary>Desacopla as telas da janela principal: qualquer ViewModel pode pedir navegação.</summary>
public sealed class Navigator
{
    public event EventHandler<NavigationRequest>? Requested;

    public void Go(PageKey page) => Requested?.Invoke(this, new NavigationRequest(page));

    public void OpenReport(Guid sessionId) => Requested?.Invoke(this, new NavigationRequest(PageKey.Reports, sessionId));

    public void Compare(Guid a, Guid b) => Requested?.Invoke(this, new NavigationRequest(PageKey.Reports, a, b));
}
