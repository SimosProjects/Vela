using Vela.Analytics;

AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
    .CreateLogger();

try
{
    var options = AnalyticsOptions.Parse(args);

    Log.Information("Vela Analytics — {Report} report | {From:yyyy-MM-dd} to {To:yyyy-MM-dd ET}",
        options.Report, options.From, options.To);

    // Reuse the same DbContext pattern as Worker and Api
    var connectionString = Environment.GetEnvironmentVariable("VELA_CONNECTION_STRING")
        ?? "Host=localhost;Database=vela;Username=vela_user;Password=vela_dev";

    var services = new ServiceCollection();

    services.AddLogging(b => b.AddSerilog());

    services.AddDbContext<VelaDbContext>(o =>
    {
        o.UseNpgsql(connectionString);
        o.UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking);
    });

    services.AddTransient<AnalyticsEngine>();
    services.AddTransient<HtmlReportGenerator>();

    var provider = services.BuildServiceProvider();

    // Run the analytics engine
    var engine = provider.GetRequiredService<AnalyticsEngine>();
    var data   = await engine.RunAsync(options);

    // Generate the HTML report
    var generator = provider.GetRequiredService<HtmlReportGenerator>();
    var outputPath = await generator.GenerateAsync(data, options.OutputDirectory);

    Log.Information("Report saved to: {Path}", outputPath);
}
catch (Exception ex)
{
    Log.Fatal(ex, "Analytics run failed.");
    return 1;
}
finally
{
    await Log.CloseAndFlushAsync();
}

return 0;