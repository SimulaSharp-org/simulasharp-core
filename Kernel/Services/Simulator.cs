namespace SimulaSharp.Kernel.Services;

/// <summary>
/// Top-level owner and control surface for a simulation run. A <see cref="Simulator"/>
/// encapsulates the single <see cref="IClock"/>, every <see cref="SimSite"/>, and (once
/// the model/codegen layers exist) all instantiated models wired together from an
/// Assembly description.
/// <para>
/// Lifecycle is deliberately minimal and one-shot: <see cref="Start"/> / <see cref="Pause"/>
/// only. There is no reset — to start over, dispose this instance and create a new one.
/// </para>
/// <para>
/// This is the artifact the MMI drives. Assembly parsing and model wiring are not yet
/// implemented; for now sites are created directly via <see cref="CreateSite"/>.
/// </para>
/// </summary>
public sealed class Simulator : IDisposable
{
    private readonly SimClock _clock;
    private readonly List<SimSite> _sites = [];
    private bool _started;

    /// <summary>Creates a simulation whose mission time is anchored at <paramref name="missionStart"/>.</summary>
    /// <param name="missionStart">Absolute wall-clock time mapped to simulation time 0.</param>
    public Simulator(DateTime missionStart)
    {
        _clock = new SimClock(missionStart);
    }

    /// <summary>The simulation clock shared by every site.</summary>
    public IClock Clock => _clock;

    /// <summary>The sites currently registered with this simulation.</summary>
    public IReadOnlyList<SimSite> Sites => _sites;

    /// <summary>
    /// Creates and registers a new site. Must be called before <see cref="Start"/>; sites
    /// added after the run has started are rejected (there is no hot-add yet).
    /// </summary>
    /// <param name="name">Diagnostic site name.</param>
    /// <param name="maxEvents">Capacity of the site's event queue.</param>
    public SimSite CreateSite(string name, int maxEvents = 1024)
    {
        if (_started)
            throw new InvalidOperationException("Cannot add a site after the simulation has started.");

        var site = new SimSite(name, _clock, maxEvents);
        _sites.Add(site);
        return site;
    }

    /// <summary>
    /// Starts (or resumes) the run. On first start every site thread is launched and
    /// parked; starting the clock then raises <see cref="IClock.Started"/>, releasing the
    /// sites to begin spinning. Ordering matters: sites subscribe before the clock starts,
    /// so none miss the signal.
    /// </summary>
    public void Start()
    {
        if (!_started)
        {
            foreach (var site in _sites)
                site.Start();
            _started = true;
        }

        _clock.Start();
    }

    /// <summary>Freezes time; every site parks until the next <see cref="Start"/>.</summary>
    public void Pause() => _clock.Pause();

    /// <summary>Tears down all sites and the clock. The instance is not reusable afterwards.</summary>
    public void Dispose()
    {
        foreach (var site in _sites)
            site.Dispose();
        _sites.Clear();
        _clock.Dispose();
    }
}
