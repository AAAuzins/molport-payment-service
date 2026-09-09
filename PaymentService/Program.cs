using PaymentService;
using PaymentService.Clients;
using PaymentService.DB;
using PaymentService.Services;
using PaymentService.Workers;

var builder = Host.CreateApplicationBuilder(args);

var settings = builder.Configuration.GetSection("AppSettings").Get<AppSettings>()
    ?? throw new InvalidOperationException("AppSettings section is missing from configuration");

builder.Services.Configure<AppSettings>(builder.Configuration.GetSection("AppSettings"));
builder.Services.AddSingleton(new OracleRepository(settings.OracleConnectionString));
builder.Services.AddSingleton(sp => new SwedBankSgwClient(
    settings.SwedBank,
    sp.GetRequiredService<ILogger<SwedBankSgwClient>>()));
builder.Services.AddSingleton<CurrencyCache>();
builder.Services.AddSingleton<HorizonService>();
builder.Services.AddSingleton<PaymentMatchingService>();
builder.Services.AddHostedService<StripeWorker>();
builder.Services.AddHostedService<SwedBankWorker>();
builder.Services.AddHostedService<PaymentReviewRetryWorker>();

var host = builder.Build();
host.Run();
