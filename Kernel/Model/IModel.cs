using SimulaSharp.Kernel.Logging;
using SimulaSharp.Kernel.Services;

namespace SimulaSharp.Kernel.Model;

public interface IModel : IDisposable
{
    public IClock TimeKeeper { get; } // Construction Argument
    public IScheduler Scheduler { get; } // Construction Argument
    public ILogger Logger { get; } // Construction Argument

    // This is called by the framework after construction, assembly and config-entities setup.
    // Here the model can register its own long-running events to the scheduler, if applicable,
    // precalculate values and do other setup work
    public void Configure();
    
    // DeInit is taking place into the Dispose method
}