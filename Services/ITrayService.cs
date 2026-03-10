using Microsoft.UI.Dispatching;

namespace Indolent.Services;

public interface ITrayService : IDisposable
{
    void Initialize(DispatcherQueue dispatcherQueue, Action showSettings, Action exitApplication);
}
