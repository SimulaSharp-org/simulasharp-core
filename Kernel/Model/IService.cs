using SimulaSharp.Kernel.Logging;
using SimulaSharp.Kernel.Services;

namespace SimulaSharp.Kernel.Model;

public interface IService : IDisposable
{
    // Construction Argument
    public IClock TimeKeeper { get; }

    // Construction Argument
    public ILogger Logger { get; }
    
    // Services should create their own SimSite, with IClock as
    // construction Argument. This will happen behind the scenes
    // using an abstract service class
    public IScheduler Scheduler { get; }

    // This is called by the framework after construction, assembly and config-entities setup.
    // Here the model can register its own long-running events to the scheduler, if applicable,
    // precalculate values and do other setup work
    public void Configure();
    
    // DeInit is taking place into the Dispose method
}