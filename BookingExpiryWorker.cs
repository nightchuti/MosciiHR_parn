using Microsoft.Data.SqlClient;

public sealed class BookingExpiryWorker(
    IConfiguration configuration,
    IWebHostEnvironment environment,
    ILogger<BookingExpiryWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!environment.IsDevelopment()) return;

        var connectionString = configuration.GetConnectionString("MosciiHR");
        if (string.IsNullOrWhiteSpace(connectionString)) return;

        var builder = new SqlConnectionStringBuilder(connectionString);
        if (string.IsNullOrEmpty(builder.Password)) builder.Password = configuration["Database:Password"] ?? "";
        if (!builder.IntegratedSecurity && string.IsNullOrEmpty(builder.Password))
        {
            logger.LogWarning("Booking expiry worker is idle because Database__Password is not configured.");
            return;
        }

        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        do
        {
            try
            {
                await BookingApi.ExpirePendingBookings(configuration, stoppingToken, logger);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Could not expire pending vehicle bookings.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
