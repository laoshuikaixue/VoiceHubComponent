using ClassIsland.Core.Abstractions;
using ClassIsland.Core.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ClassIsland.Core.Extensions.Registry;
using VoiceHubComponent.Models;
using VoiceHubComponent.Views;

namespace VoiceHubComponent;

[PluginEntrance]
public class Plugin : PluginBase
{
    public override void Initialize(HostBuilderContext context, IServiceCollection services)
    {
        services.AddComponent<VoiceHubControl, VoiceHubSettingsView>();
        services.AddSingleton<VoiceHubSettings>();
    }
}