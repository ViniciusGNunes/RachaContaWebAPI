using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using RachaConta.Api.Contracts;
using RachaConta.Api.Domain;
using RachaConta.Api.Infrastructure;

namespace RachaConta.Tests;

public class ValidationAndObservabilityIntegrationTests : IClassFixture<RachaContaAppFactory>
{
    private readonly RachaContaAppFactory _factory;

    public ValidationAndObservabilityIntegrationTests(RachaContaAppFactory factory)
    {
        _factory = factory;
    }

    // ---------- RFC 7807 ProblemDetails Validation Tests ----------

    [Fact]
    public async Task Create_group_validation_failure_returns_400_problem_details()
    {
        var client = _factory.CreateClient();

        // 1. Group name empty
        var emptyNameReq = new CreateGroupRequest("", "Desc", [new MemberRequest("Ana", null, null)]);
        var response = await client.PostAsJsonAsync("/api/groups", emptyNameReq);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal(400, root.GetProperty("status").GetInt32());
        Assert.True(root.TryGetProperty("errors", out var errors));
        Assert.True(errors.TryGetProperty("name", out _) || errors.TryGetProperty("Name", out _));
    }

    [Fact]
    public async Task Create_expense_validation_failures_return_400_problem_details()
    {
        var client = _factory.CreateClient();

        // Create group with Alice & Bob
        var groupRes = await client.PostAsJsonAsync("/api/groups", new CreateGroupRequest(
            "Apê", null, [new MemberRequest("Alice", null, null), new MemberRequest("Bob", null, null)]));
        var group = (await groupRes.Content.ReadFromJsonAsync<GroupDto>())!;
        var alice = group.Members.Single(m => m.Name == "Alice");
        var bob = group.Members.Single(m => m.Name == "Bob");

        client.SetGroupToken(group.AccessToken);
        var aliceClaim = await client.PostAsJsonAsync($"/api/groups/{group.Id}/claim", new ClaimRequest(alice.Id, null));
        var aliceToken = (await aliceClaim.Content.ReadFromJsonAsync<ClaimResultDto>())!.MemberToken;
        client.SetGroupToken(aliceToken);

        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        // 1. Invalid amount (<= 0)
        var negativeAmountReq = new ExpenseRequest(
            Description: "Mercado",
            Amount: -50.00m,
            Date: today,
            Category: ExpenseCategory.Food,
            PaymentMethod: PaymentMethod.CreditCard,
            PaidByMemberId: alice.Id,
            ShareMode: ShareMode.Equal,
            Shares: [new ExpenseShareRequest(alice.Id, null, null), new ExpenseShareRequest(bob.Id, null, null)],
            Notes: null);

        var resNegative = await client.PostAsJsonAsync($"/api/groups/{group.Id}/expenses", negativeAmountReq);
        Assert.Equal(HttpStatusCode.BadRequest, resNegative.StatusCode);
        Assert.Equal("application/problem+json", resNegative.Content.Headers.ContentType?.MediaType);

        // 2. Custom shares sum mismatch
        var customMismatchReq = new ExpenseRequest(
            Description: "Mercado",
            Amount: 100.00m,
            Date: today,
            Category: ExpenseCategory.Food,
            PaymentMethod: PaymentMethod.CreditCard,
            PaidByMemberId: alice.Id,
            ShareMode: ShareMode.Custom,
            Shares:
            [
                new ExpenseShareRequest(alice.Id, 40.00m, null),
                new ExpenseShareRequest(bob.Id, 40.00m, null), // Sum = 80 != 100
            ],
            Notes: null);

        var resMismatch = await client.PostAsJsonAsync($"/api/groups/{group.Id}/expenses", customMismatchReq);
        Assert.Equal(HttpStatusCode.BadRequest, resMismatch.StatusCode);
        Assert.Equal("application/problem+json", resMismatch.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Create_settlement_validation_failures_return_400()
    {
        var client = _factory.CreateClient();

        var groupRes = await client.PostAsJsonAsync("/api/groups", new CreateGroupRequest(
            "Apê", null, [new MemberRequest("Alice", null, null), new MemberRequest("Bob", null, null)]));
        var group = (await groupRes.Content.ReadFromJsonAsync<GroupDto>())!;
        var alice = group.Members.Single(m => m.Name == "Alice");

        client.SetGroupToken(group.AccessToken);
        var aliceClaim = await client.PostAsJsonAsync($"/api/groups/{group.Id}/claim", new ClaimRequest(alice.Id, null));
        var aliceToken = (await aliceClaim.Content.ReadFromJsonAsync<ClaimResultDto>())!.MemberToken;
        client.SetGroupToken(aliceToken);

        // Member cannot pay themselves
        var selfPaymentReq = new CreateSettlementRequest(
            FromMemberId: alice.Id,
            ToMemberId: alice.Id,
            Amount: 50.00m,
            Date: DateOnly.FromDateTime(DateTime.UtcNow));

        var resSelf = await client.PostAsJsonAsync($"/api/groups/{group.Id}/settlements", selfPaymentReq);
        Assert.Equal(HttpStatusCode.BadRequest, resSelf.StatusCode);
        Assert.Equal("application/problem+json", resSelf.Content.Headers.ContentType?.MediaType);
    }

    // ---------- Observability & Correlation ID Tests ----------

    [Fact]
    public async Task Correlation_id_middleware_generates_id_when_not_provided()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        Assert.True(response.Headers.Contains(CorrelationIdMiddleware.HeaderName));
        var correlationId = response.Headers.GetValues(CorrelationIdMiddleware.HeaderName).FirstOrDefault();
        Assert.False(string.IsNullOrWhiteSpace(correlationId));
        Assert.True(Guid.TryParse(correlationId, out _));
    }

    [Fact]
    public async Task Correlation_id_middleware_echoes_provided_id()
    {
        var client = _factory.CreateClient();
        const string customCorrelationId = "custom-client-trace-12345678";

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/health");
        request.Headers.Add(CorrelationIdMiddleware.HeaderName, customCorrelationId);

        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        Assert.True(response.Headers.Contains(CorrelationIdMiddleware.HeaderName));
        var returnedId = response.Headers.GetValues(CorrelationIdMiddleware.HeaderName).FirstOrDefault();
        Assert.Equal(customCorrelationId, returnedId);
    }

    [Fact]
    public async Task App_exception_handler_logs_unexpected_exceptions_with_correlation_id()
    {
        var testLogger = new TestCorrelationLogger<AppExceptionHandler>();
        var problemDetails = new TestProblemDetailsService();
        var handler = new AppExceptionHandler(problemDetails, testLogger);

        var httpContext = new DefaultHttpContext();
        const string testCorrId = "corr-test-id-998877";
        httpContext.Items[CorrelationIdMiddleware.ItemKey] = testCorrId;

        var unexpectedException = new InvalidOperationException("Simulated unexpected database failure");

        var handled = await handler.TryHandleAsync(httpContext, unexpectedException, CancellationToken.None);

        // Handler returns false for unexpected exceptions so the pipeline continues
        Assert.False(handled);

        // Verify that the unexpected exception was logged with Error log level and contains the correlation ID
        Assert.Single(testLogger.Entries);
        var entry = testLogger.Entries[0];
        Assert.Equal(LogLevel.Error, entry.LogLevel);
        Assert.Same(unexpectedException, entry.Exception);
        Assert.Contains(testCorrId, entry.Message);
    }

    // Helper classes for testing AppExceptionHandler
    private sealed class TestCorrelationLogger<T> : ILogger<T>
    {
        public record LogEntry(LogLevel LogLevel, string Message, Exception? Exception);
        public List<LogEntry> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Entries.Add(new LogEntry(logLevel, formatter(state, exception), exception));
        }
    }

    private sealed class TestProblemDetailsService : IProblemDetailsService
    {
        public ValueTask WriteAsync(ProblemDetailsContext context) => ValueTask.CompletedTask;

        public ValueTask<bool> TryWriteAsync(ProblemDetailsContext context) => ValueTask.FromResult(true);
    }
}
