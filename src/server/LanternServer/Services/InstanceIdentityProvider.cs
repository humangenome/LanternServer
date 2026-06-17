using LanternServer.Configuration;
using Microsoft.Extensions.Options;

namespace LanternServer.Services;

public sealed class InstanceIdentityProvider
{
    private readonly LanternServerOptions _options;

    public InstanceIdentityProvider(IOptions<LanternServerOptions> options)
    {
        _options = options.Value;
    }

    public string InstanceId => _options.InstanceId;

    public string PipeName => _options.PipeName;

    public int GameplayPort => _options.GameplayPort;
    public int ControlPort => _options.ControlPort;
    public int QueryPort => _options.QueryPort;
    public int RconPort => _options.RconPort;
}
