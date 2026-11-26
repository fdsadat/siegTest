using Microsoft.Extensions.DependencyInjection;

namespace FiscalDocuments.Application;

public static class ApplicationServices
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddSingleton(TimeProvider.System);
        services.AddMediatR(configuration =>
        {
            configuration.RegisterServicesFromAssemblyContaining<ReceiveDocument>();
            configuration.AddOpenBehavior(typeof(ValidationBehavior<,>));
        });
        services.AddScoped<EventProcessor>();
        return services;
    }
}
