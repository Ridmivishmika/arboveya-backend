using Arboveya.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace Arboveya.Api.Services;

/// <summary>
/// Background service that periodically runs automated verification for in-flight PayHere transactions and refunds.
/// Solves the gap where verification previously only executed when manual 'Check Live PayHere Status' was clicked.
/// Automatically queries PayHere Retrieval API for orders with RefundStatus in ('RefundPending', 'RefundProcessing')
/// and finalizes orders to 'Refunded', restores inventory, and notifies customers when PayHere gateway completes the refund.
/// </summary>
public class PayHereBackgroundVerificationService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<PayHereBackgroundVerificationService> _logger;
    private readonly IConfiguration _configuration;

    public PayHereBackgroundVerificationService(
        IServiceScopeFactory scopeFactory,
        ILogger<PayHereBackgroundVerificationService> logger,
        IConfiguration configuration)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _configuration = configuration;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("PayHere Background Verification Service initialized and started.");

        // Initial delay to allow web host and database initialization to finish cleanly
        await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await VerifyPendingRefundOrdersAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error in PayHere Background Verification Service loop.");
            }

            // Polling interval: default to 60 seconds (customizable via PayHere:VerificationIntervalSeconds)
            var intervalSeconds = 60;
            var configuredInterval = _configuration["PayHere:VerificationIntervalSeconds"];
            if (int.TryParse(configuredInterval, out var parsedInterval) && parsedInterval > 0)
            {
                intervalSeconds = parsedInterval;
            }

            await Task.Delay(TimeSpan.FromSeconds(intervalSeconds), stoppingToken);
        }

        _logger.LogInformation("PayHere Background Verification Service has stopped.");
    }

    private async Task VerifyPendingRefundOrdersAsync(CancellationToken stoppingToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var orderService = scope.ServiceProvider.GetRequiredService<IOrderService>();

        // Query orders that have in-flight refund requests awaiting final gateway settlement
        var pendingOrders = await context.Orders
            .Where(o => o.RefundStatus == "RefundPending" || o.RefundStatus == "RefundProcessing")
            .OrderBy(o => o.UpdatedAt)
            .Take(15)
            .Select(o => new { o.Id, o.PayHereOrderId, o.RefundStatus })
            .ToListAsync(stoppingToken);

        if (pendingOrders.Count == 0)
        {
            return;
        }

        _logger.LogInformation("[PayHere Background Verification] Found {Count} pending refund order(s) to verify with PayHere.", pendingOrders.Count);

        foreach (var po in pendingOrders)
        {
            if (stoppingToken.IsCancellationRequested) break;

            try
            {
                _logger.LogInformation("[PayHere Background Verification] Querying live status for Order '{OrderId}' ({PayHereOrderId}, Status: {RefundStatus})...",
                    po.Id, po.PayHereOrderId, po.RefundStatus);

                // RetrievePayHerePaymentAsync automatically checks PayHere Retrieval API,
                // logs '[PayHere Retrieval API Response]', and if PayHere reports 'REFUNDED',
                // automatically finalizes order payment status, restores stock inventory, and dispatches customer notification emails!
                var details = await orderService.RetrievePayHerePaymentAsync(po.Id, currentUserId: null, isAdmin: true);

                if (details == null || !details.Found)
                {
                    _logger.LogWarning("[PayHere Background Verification] Order '{PayHereOrderId}': Payment lookup failed ({Message}).",
                        po.PayHereOrderId, details?.Message);
                }
                else
                {
                    _logger.LogInformation("[PayHere Background Verification] Order '{PayHereOrderId}' live PayHere status: '{Status}'.",
                        po.PayHereOrderId, details.Status);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[PayHere Background Verification] Error verifying Order '{PayHereOrderId}'.", po.PayHereOrderId);
            }
        }
    }
}
