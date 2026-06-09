using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Usmga.FunctionApp.Options;
using Usmga.FunctionApp.Services;

var host = new HostBuilder()
    .ConfigureFunctionsWorkerDefaults()
    .ConfigureServices((context, services) =>
    {
        services.Configure<GitHubOptions>(context.Configuration.GetSection("GitHub"));
        services.Configure<TwilioOptions>(context.Configuration.GetSection("Twilio"));
        services.Configure<StorageOptions>(context.Configuration.GetSection("Storage"));
        services.Configure<NotifyOptions>(context.Configuration.GetSection("Notify"));
        services.AddSingleton<ITokenGenerator, SecureTokenGenerator>();
        services.AddSingleton<MessageClassifier>();
        services.AddSingleton<IStateStore, TableStateStore>();
        services.AddSingleton<ISmsClient, TwilioSmsClient>();
        services.AddHttpClient<IGitHubClient, GitHubClient>();
        services.AddSingleton<RequestProcessor>();
    })
    .Build();

host.Run();
