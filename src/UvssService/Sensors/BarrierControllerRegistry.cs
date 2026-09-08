using System.Collections.Concurrent;

namespace UvssService.Sensors;

/// <summary>Lets the Gate Setup admin page reach the SAME running
/// IBarrierController instance GateWorkerHostedService is using for a gate
/// (registered once at startup) -- needed for the page's "Test Open Barrier"
/// button to actually operate the real barrier rather than a separate,
/// disconnected controller object.</summary>
public class BarrierControllerRegistry
{
    private readonly ConcurrentDictionary<string, IBarrierController> _controllers = new();

    public void Register(string gateName, IBarrierController controller) => _controllers[gateName] = controller;

    public IBarrierController? Find(string gateName) => _controllers.TryGetValue(gateName, out var controller) ? controller : null;
}
